using System.IO.Pipes;
using PasswordTool.Autofill.Transport;

if (!OperatingSystem.IsWindows()) return 1;
var executable = Environment.ProcessPath ?? throw new InvalidOperationException();
if (string.Equals(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), Path.GetDirectoryName(executable), StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Run the self-extracting single-file publish to exercise the release path.");

var name = "YourSafe.PublishedPeer.Tests." + Guid.NewGuid().ToString("N");
using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut,
    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
var waiting = server.WaitForConnectionAsync(timeout.Token);
await client.ConnectAsync(timeout.Token);
await waiting;

foreach (var serverPeer in new[] { true, false })
{
    PipeStream pipe = serverPeer ? server : client;
    PipePeer.Verify(pipe, serverPeer, Path.GetFileName(executable));
    try
    {
        PipePeer.Verify(pipe, serverPeer, Path.GetFileName(executable) + ".wrong");
        throw new InvalidOperationException("Wrong executable was accepted.");
    }
    catch (UnauthorizedAccessException) { }
}
Console.WriteLine("PASS: self-extracting publish verifies adjacent executable peers and rejects wrong paths in both directions.");
return 0;
