using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using PasswordTool.Autofill;
using PasswordTool.Autofill.Transport;

if (!OperatingSystem.IsWindows()) return 1;
// The browser enforces the manifest's extension allowlist. Never trust JSON for peer identity.
if (args.Length == 0 || !System.Text.RegularExpressions.Regex.IsMatch(args[0], "^chrome-extension://[a-p]{32}/?$")) return 1;
using var input = Console.OpenStandardInput();
using var output = Console.OpenStandardOutput();
try
{
    while (true)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var bytes = await Framing.ReadAsync(input, timeout.Token);
        if (bytes is null) return 0;
        byte[]? responseBytes = null;
        try
        {
            var request = WireProtocol.ParseRequest(bytes);
            try
            {
                using var pipe = new NamedPipeClientStream(".", PipePeer.PipeName, PipeDirection.InOut,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.ConnectAsync(3000, timeout.Token);
                PipePeer.Verify(pipe, server: false, Path.Combine(AppContext.BaseDirectory, "YourSafe.exe"));
                await Framing.WriteAsync(pipe, bytes, timeout.Token);
                responseBytes = await Framing.ReadAsync(pipe, timeout.Token) ?? throw new EndOfStreamException();
                _ = WireProtocol.ParseResponse(responseBytes, request);
            }
            catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException)
            {
                if (responseBytes is not null) CryptographicOperations.ZeroMemory(responseBytes);
                responseBytes = JsonSerializer.SerializeToUtf8Bytes(WireProtocol.Failure(request.RequestId, "desktopUnavailable"), WireProtocol.Json);
            }
            await Framing.WriteAsync(output, responseBytes, timeout.Token);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (responseBytes is not null) CryptographicOperations.ZeroMemory(responseBytes);
        }
    }
}
catch (Exception error) when (error is IOException or JsonException or System.Text.DecoderFallbackException or OperationCanceledException or UnauthorizedAccessException)
{
    // No payloads, stack traces, secrets or non-protocol output on stdout/stderr.
    return 1;
}
