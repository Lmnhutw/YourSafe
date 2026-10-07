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
                    var response = await DispatchAsync(request, timeout.Token).ConfigureAwait(false);
                    responseBytes = JsonSerializer.SerializeToUtf8Bytes(response, WireProtocol.Json);
                    if (request.Action is "findCredentials" or "getCredentialSecret" && !flow.IsCurrentUnlock(version))
                    {
                        CryptographicOperations.ZeroMemory(responseBytes);
                        response = WireProtocol.Failure(request.RequestId, "locked");
                        responseBytes = JsonSerializer.SerializeToUtf8Bytes(response, WireProtocol.Json);
                    }
                    var needsUnlock = response.Ok && request.Action is "findCredentials" or "getCredentialSecret";
                    await Framing.WriteAsync(pipe, responseBytes, timeout.Token,
                        () => !needsUnlock || flow.IsCurrentUnlock(version)).ConfigureAwait(false);
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
                "getStatus" => WireProtocol.Success(request.RequestId, new StatusResult(flow.IsCurrentUnlock(flow.LifecycleVersion))),
                "findCredentials" => WireProtocol.DiscoverySuccess(request.RequestId,
                    await flow.FindAutofillCredentialsAsync(request.Payload.Origin!, cancellationToken).ConfigureAwait(false)),
                "getCredentialSecret" => WireProtocol.Success(request.RequestId,
                    await flow.GetAutofillCredentialSecretAsync(request.Payload.Origin!, request.Payload.CredentialId!.Value, cancellationToken).ConfigureAwait(false)),
                "showApp" => WireProtocol.Success(request.RequestId, new ShowAppResult(await ShowAppAsync().WaitAsync(cancellationToken).ConfigureAwait(false))),
                _ => WireProtocol.Failure(request.RequestId, "invalidRequest")
            };
        }
        catch (Exception error) when (error is OperationCanceledException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            return WireProtocol.Failure(request.RequestId, flow.IsCurrentUnlock(flow.LifecycleVersion) ? "unavailable" : "locked");
        }
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
