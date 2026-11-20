using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.UI.Windowing;
using PasswordTool.Autofill;
using PasswordTool.Autofill.Transport;
using PasswordTool.Presentation;
using PasswordTool.Presentation.Autofill;

namespace PasswordTool_WinUI;

internal sealed class AutofillPipeServer
{
    private readonly CancellationTokenSource stopping = new();
    private readonly AppFlowCoordinator flow;
    private readonly Task listening;

    public AutofillPipeServer(AppFlowCoordinator flow)
    {
        this.flow = flow;
        listening = Task.WhenAll(ListenAsync(stopping.Token), ListenAsync(stopping.Token));
    }

    private async Task ListenAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // A second bounded listener can reject competing requests while one waits for approval.
                using var pipe = new NamedPipeServerStream(PipePeer.PipeName, PipeDirection.InOut, 2,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 4096, 4096);
                await pipe.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                // Failure closes the connection before parsing or accessing the vault.
                PipePeer.Verify(pipe, server: true, "YourSafe.NativeHost.exe");
                var bytes = await Framing.ReadAsync(pipe, timeout.Token).ConfigureAwait(false);
                if (bytes is null) continue;
                byte[]? responseBytes = null;
                try
                {
                    var request = WireProtocol.ParseRequest(bytes);
                    var protectedAction = request.Action is "findCredentials" or "getCredentialSecret" or "getCredentialTotp" or "copyCredentialTotp";
                    var version = flow.LifecycleVersion;
                    var mutationVersion = protectedAction ? flow.AutofillMutationVersion : 0;
                    timeout.CancelAfter(TimeSpan.FromSeconds(70));
                    using var requestLifetime = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                    var disconnect = CancelOnDisconnectAsync(pipe, requestLifetime);
                    try
                    {
                        bool CanDeliver() => !requestLifetime.IsCancellationRequested && flow.BrowserIntegrationEnabled && flow.IsCurrentNormalUnlock(version)
                            && flow.AutofillMutationVersion == mutationVersion;
                        var response = await DispatchAsync(request, requestLifetime.Token, CanDeliver).ConfigureAwait(false);
                        if (protectedAction && !CanDeliver())
                            response = WireProtocol.Failure(request.RequestId, ProtectedErrorCode());
                        responseBytes = JsonSerializer.SerializeToUtf8Bytes(response, WireProtocol.Json);
                        await Framing.WriteAsync(pipe, responseBytes, requestLifetime.Token,
                            () => !response.Ok || !protectedAction || CanDeliver()).ConfigureAwait(false);
                    }
                    finally
                    {
                        requestLifetime.Cancel();
                        await disconnect.ConfigureAwait(false);
                    }
                }
                catch (ProtocolMismatchException mismatch)
                {
                    responseBytes = JsonSerializer.SerializeToUtf8Bytes(WireProtocol.Failure(mismatch.RequestId, "protocolMismatch"), WireProtocol.Json);
                    await Framing.WriteAsync(pipe, responseBytes, timeout.Token).ConfigureAwait(false);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(bytes);
                    if (responseBytes is not null) CryptographicOperations.ZeroMemory(responseBytes);
                }
            }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException or System.Text.DecoderFallbackException or UnauthorizedAccessException or OperationCanceledException)
            {
                // Malformed input, impersonation, disconnect and timeout fail closed without payload logging.
                try { await Task.Delay(250, stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task<AutofillResponse> DispatchAsync(AutofillRequest request, CancellationToken cancellationToken, Func<bool> canDeliver)
    {
        try
        {
            var control = request.Action is "ping" or "getStatus" or "showApp";
            if (control && !flow.TryAcceptAutofillControlRequest()) return WireProtocol.Failure(request.RequestId, "unavailable");
            if (!control && (!flow.BrowserIntegrationEnabled || !flow.IsCurrentNormalUnlock(flow.LifecycleVersion)))
                return WireProtocol.Failure(request.RequestId, ProtectedErrorCode());
            return request.Action switch
            {
                "ping" => WireProtocol.Success(request.RequestId, new PingResult("YourSafe")),
                "getStatus" => WireProtocol.Success(request.RequestId, flow.GetAutofillStatus()),
                "findCredentials" => WireProtocol.DiscoverySuccess(request.RequestId,
                    await flow.FindAutofillCredentialsAsync(request.Payload.Origin!, cancellationToken).ConfigureAwait(false)),
                "getCredentialSecret" => WireProtocol.Success(request.RequestId,
                    await flow.GetAutofillCredentialSecretAsync(request.Payload.Origin!, request.Payload.CredentialId!.Value, cancellationToken).ConfigureAwait(false)),
                "getCredentialTotp" => WireProtocol.Success(request.RequestId,
                    ToWireTotp(await flow.GetAutofillCredentialTotpAsync(request.Payload.Origin!, request.Payload.CredentialId!.Value, cancellationToken).ConfigureAwait(false))),
                "copyCredentialTotp" => await CopyTotpAsync(request, cancellationToken, canDeliver).ConfigureAwait(false),
                "showApp" => WireProtocol.Success(request.RequestId, new ShowAppResult(await ShowAppAsync().WaitAsync(cancellationToken).ConfigureAwait(false))),
                _ => WireProtocol.Failure(request.RequestId, "invalidRequest")
            };
        }
        catch (AutofillApprovalDeclinedException)
        {
            return WireProtocol.Failure(request.RequestId, "approvalDeclined");
        }
        catch (Exception error) when (error is OperationCanceledException or InvalidOperationException or UnauthorizedAccessException or ArgumentException
            or System.Runtime.InteropServices.COMException)
        {
            return WireProtocol.Failure(request.RequestId, request.Action is "ping" or "getStatus" or "showApp" ? "unavailable" : ProtectedErrorCode());
        }
    }

    private string ProtectedErrorCode() => !flow.BrowserIntegrationEnabled ? "integrationDisabled"
        : !flow.IsCurrentNormalUnlock(flow.LifecycleVersion) ? "locked" : "unavailable";

    private async Task<AutofillResponse> CopyTotpAsync(AutofillRequest request, CancellationToken cancellationToken, Func<bool> canDeliver)
    {
        await flow.CopyAutofillCredentialTotpAsync(request.Payload.Origin!, request.Payload.CredentialId!.Value, cancellationToken, canDeliver).ConfigureAwait(false);
        return WireProtocol.Success(request.RequestId, new CopyTotpResult(true));
    }

    private static CredentialTotp ToWireTotp(PasswordTool.Core.Models.TotpCodeResult result) =>
        new(result.Code, result.PeriodSeconds, result.ExpiresAtUtc.ToUnixTimeMilliseconds());

    private static async Task CancelOnDisconnectAsync(Stream pipe, CancellationTokenSource lifetime)
    {
        try
        {
            // This pipe accepts one request. EOF or an unexpected second frame invalidates the pending operation.
            await pipe.ReadAsync(new byte[1], lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
        finally { lifetime.Cancel(); }
    }

    private static Task<bool> ShowAppAsync()
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!App.DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                var window = App.Window.AppWindow;
                if (window.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
                    presenter.Restore();
                window.Show();
                window.MoveInZOrderAtTop();
                App.Window.Activate();
                completion.TrySetResult(true);
            }
            catch { completion.TrySetResult(false); }
        })) completion.TrySetResult(false);
        return completion.Task;
    }

    public async Task StopAsync()
    {
        stopping.Cancel();
        await listening.ConfigureAwait(false);
        stopping.Dispose();
    }
}
