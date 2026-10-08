using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.UI.Windowing;
using PasswordTool.Autofill;
using PasswordTool.Autofill.Transport;
using PasswordTool.Presentation;

namespace PasswordTool_WinUI;

internal sealed class AutofillPipeServer
{
    private readonly CancellationTokenSource stopping = new();
    private readonly AppFlowCoordinator flow;
    private readonly Task listening;

    public AutofillPipeServer(AppFlowCoordinator flow)
    {
        this.flow = flow;
        listening = ListenAsync();
    }

    private async Task ListenAsync()
    {
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                // ponytail: one connection at a time; add bounded listeners if concurrent requests exceed the 3s connect timeout.
                using var pipe = new NamedPipeServerStream(PipePeer.PipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 4096, 4096);
                await pipe.WaitForConnectionAsync(stopping.Token).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                // Failure closes the connection before parsing or accessing the vault.
                PipePeer.Verify(pipe, server: true, Path.Combine(AppContext.BaseDirectory, "YourSafe.NativeHost.exe"));
                var bytes = await Framing.ReadAsync(pipe, timeout.Token).ConfigureAwait(false);
                if (bytes is null) continue;
                byte[]? responseBytes = null;
                try
                {
                    var request = WireProtocol.ParseRequest(bytes);
                    var version = flow.LifecycleVersion;
                    using var requestLifetime = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                    var disconnect = CancelOnDisconnectAsync(pipe, requestLifetime);
                    try
                    {
                        var response = await DispatchAsync(request, requestLifetime.Token).ConfigureAwait(false);
                        var protectedAction = request.Action is "findCredentials" or "getCredentialSecret" or "getCredentialTotp" or "copyCredentialTotp";
                        if (protectedAction && !flow.IsCurrentNormalUnlock(version))
                            response = WireProtocol.Failure(request.RequestId, "locked");
                        responseBytes = JsonSerializer.SerializeToUtf8Bytes(response, WireProtocol.Json);
                        await Framing.WriteAsync(pipe, responseBytes, requestLifetime.Token,
                            () => !response.Ok || !protectedAction || flow.IsCurrentNormalUnlock(version)).ConfigureAwait(false);
                    }
                    finally
                    {
                        requestLifetime.Cancel();
                        await disconnect.ConfigureAwait(false);
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(bytes);
                    if (responseBytes is not null) CryptographicOperations.ZeroMemory(responseBytes);
                }
            }
            catch (Exception error) when (error is IOException or JsonException or System.Text.DecoderFallbackException or UnauthorizedAccessException or OperationCanceledException)
            {
                // Malformed input, impersonation, disconnect and timeout fail closed without payload logging.
                try { await Task.Delay(250, stopping.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task<AutofillResponse> DispatchAsync(AutofillRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return request.Action switch
            {
                "ping" => WireProtocol.Success(request.RequestId, new PingResult("YourSafe")),
                "getStatus" => WireProtocol.Success(request.RequestId, new StatusResult(flow.IsCurrentNormalUnlock(flow.LifecycleVersion))),
                "findCredentials" => WireProtocol.DiscoverySuccess(request.RequestId,
                    await flow.FindAutofillCredentialsAsync(request.Payload.Origin!, cancellationToken).ConfigureAwait(false)),
                "getCredentialSecret" => WireProtocol.Success(request.RequestId,
                    await flow.GetAutofillCredentialSecretAsync(request.Payload.Origin!, request.Payload.CredentialId!.Value, cancellationToken).ConfigureAwait(false)),
                "getCredentialTotp" => WireProtocol.Success(request.RequestId,
                    ToWireTotp(await flow.GetAutofillCredentialTotpAsync(request.Payload.Origin!, request.Payload.CredentialId!.Value, cancellationToken).ConfigureAwait(false))),
                "copyCredentialTotp" => await CopyTotpAsync(request, cancellationToken).ConfigureAwait(false),
                "showApp" => WireProtocol.Success(request.RequestId, new ShowAppResult(await ShowAppAsync().WaitAsync(cancellationToken).ConfigureAwait(false))),
                _ => WireProtocol.Failure(request.RequestId, "invalidRequest")
            };
        }
        catch (Exception error) when (error is OperationCanceledException or InvalidOperationException or UnauthorizedAccessException or ArgumentException
            or System.Runtime.InteropServices.COMException)
        {
            return WireProtocol.Failure(request.RequestId, flow.IsCurrentNormalUnlock(flow.LifecycleVersion) ? "unavailable" : "locked");
        }
    }

    private async Task<AutofillResponse> CopyTotpAsync(AutofillRequest request, CancellationToken cancellationToken)
    {
        await flow.CopyAutofillCredentialTotpAsync(request.Payload.Origin!, request.Payload.CredentialId!.Value, cancellationToken).ConfigureAwait(false);
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
                if (App.Window.AppWindow.Presenter is OverlappedPresenter presenter && presenter.State != OverlappedPresenterState.Restored)
                    presenter.Restore();
                App.Window.AppWindow.Show();
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
