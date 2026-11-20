using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using PasswordTool.Autofill;
using PasswordTool.Autofill.Transport;

if (!OperatingSystem.IsWindows()) return 1;
// The browser enforces the manifest's extension allowlist. Never trust JSON for peer identity.
if (args.Length == 0 || !System.Text.RegularExpressions.Regex.IsMatch(args[0], "^chrome-extension://[a-p]{32}/?$")) return 1;
using var input = new LookaheadInput(Console.OpenStandardInput());
using var output = Console.OpenStandardOutput();
try
{
    while (true)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(70));
        var bytes = await Framing.ReadAsync(input, timeout.Token);
        if (bytes is null) return 0;
        byte[]? responseBytes = null;
        using var requestFinished = new CancellationTokenSource();
        var watching = Task.CompletedTask;
        try
        {
            watching = CancelOnExtraInputAsync(input.WatchNextByte(), timeout, requestFinished.Token);
            try
            {
                var request = WireProtocol.ParseRequest(bytes);
                try
                {
                    using var pipe = new NamedPipeClientStream(".", PipePeer.PipeName, PipeDirection.InOut,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    var openDesktop = request.Action == "showApp";
                    try { await pipe.ConnectAsync(openDesktop ? 500 : 3000, timeout.Token); }
                    catch (TimeoutException) when (openDesktop)
                    {
                        StartDesktopIfNeeded(timeout.Token);
                        await pipe.ConnectAsync(12000, timeout.Token);
                    }
                    PipePeer.Verify(pipe, server: false, "YourSafe.exe");
                    await Framing.WriteAsync(pipe, bytes, timeout.Token);
                    responseBytes = await Framing.ReadAsync(pipe, timeout.Token) ?? throw new EndOfStreamException();
                    _ = WireProtocol.ParseResponse(responseBytes, request);
                }
                catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException)
                {
                    if (responseBytes is not null) CryptographicOperations.ZeroMemory(responseBytes);
                    responseBytes = JsonSerializer.SerializeToUtf8Bytes(WireProtocol.Failure(request.RequestId, "desktopUnavailable"), WireProtocol.Json);
                }
            }
            catch (ProtocolMismatchException mismatch)
            {
                if (responseBytes is not null) CryptographicOperations.ZeroMemory(responseBytes);
                responseBytes = JsonSerializer.SerializeToUtf8Bytes(WireProtocol.Failure(mismatch.RequestId, "protocolMismatch"), WireProtocol.Json);
            }
            requestFinished.Cancel();
            await watching;
            await Framing.WriteAsync(output, responseBytes, timeout.Token, () => !input.Disconnected);
        }
        finally
        {
            requestFinished.Cancel();
            await watching;
            CryptographicOperations.ZeroMemory(bytes);
            if (responseBytes is not null) CryptographicOperations.ZeroMemory(responseBytes);
        }
    }
}
catch (Exception error) when (error is IOException or InvalidDataException or JsonException or System.Text.DecoderFallbackException or OperationCanceledException or UnauthorizedAccessException)
{
    // No payloads, stack traces, secrets or non-protocol output on stdout/stderr.
    return 1;
}

static void StartDesktopIfNeeded(CancellationToken cancellationToken)
{
    cancellationToken.ThrowIfCancellationRequested();
    var directory = Path.GetDirectoryName(Environment.ProcessPath)
        ?? throw new UnauthorizedAccessException("Invalid NativeHost executable path.");
    var executable = Path.Combine(directory, "YourSafe.exe");
    if (!File.Exists(executable)) throw new FileNotFoundException("The adjacent YourSafe app is unavailable.");

    using var launch = new Mutex(false, $@"Local\{PipePeer.PipeName}.Launch");
    var acquired = false;
    try
    {
        try { acquired = launch.WaitOne(0); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) return;

        using var host = Process.GetCurrentProcess();
        var desktops = Process.GetProcessesByName("YourSafe");
        try
        {
            // A running or starting desktop may temporarily have no available pipe listener.
            if (desktops.Any(desktop => desktop.SessionId == host.SessionId)) return;
        }
        finally
        {
            foreach (var desktop in desktops) desktop.Dispose();
        }

        cancellationToken.ThrowIfCancellationRequested();
        // The fixed sibling path needs no browser arguments or inherited Native Messaging streams.
        using var started = Process.Start(new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Normal
        });
    }
    catch (Exception error) when (error is Win32Exception or InvalidOperationException)
    {
        throw new IOException("YourSafe could not be started.", error);
    }
    finally { if (acquired) launch.ReleaseMutex(); }
}

static async Task CancelOnExtraInputAsync(Task<int> incoming, CancellationTokenSource lifetime, CancellationToken requestFinished)
{
    try
    {
        await incoming.WaitAsync(requestFinished);
        lifetime.Cancel(); // EOF or another frame invalidates the current desktop request.
    }
    catch (OperationCanceledException) when (requestFinished.IsCancellationRequested) { }
    catch (Exception error) when (error is IOException or ObjectDisposedException) { lifetime.Cancel(); }
}

// Carry the single pending stdin read into the next request; cancelling Console reads can leave competing readers.
sealed class LookaheadInput(Stream source) : Stream
{
    private readonly byte[] nextByte = new byte[1];
    private Task<int>? lookahead;

    public Task<int> WatchNextByte() => lookahead ??= source.ReadAsync(nextByte.AsMemory()).AsTask();
    public bool Disconnected => lookahead is { IsCompleted: true }
        && (!lookahead.IsCompletedSuccessfully || lookahead.Result == 0);

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0) return 0;
        if (lookahead is null) return await source.ReadAsync(buffer, cancellationToken);
        var read = await lookahead.WaitAsync(cancellationToken);
        lookahead = null;
        if (read != 0) buffer.Span[0] = nextByte[0];
        return read;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override bool CanRead => source.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing) source.Dispose();
        base.Dispose(disposing);
    }
}
