using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PasswordTool.Autofill.Transport;

[SupportedOSPlatform("windows")]
public static class PipePeer
{
    public static string PipeName
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            var user = identity.User?.Value ?? throw new UnauthorizedAccessException();
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(user)))[..24];
#if DEBUG
            const string channel = "dev";
#else
            const string channel = "prod";
#endif
            return $"YourSafe.Autofill.v1.{channel}.{hash}.{Process.GetCurrentProcess().SessionId}";
        }
    }

    public static void Verify(PipeStream pipe, bool server, string expectedExecutable)
    {
        var success = server
            ? GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid)
            : GetNamedPipeServerProcessId(pipe.SafePipeHandle, out pid);
        if (!success || pid == 0 || !ProcessIdToSessionId(pid, out var session)
            || session != Process.GetCurrentProcess().SessionId) throw new UnauthorizedAccessException("Invalid pipe peer.");
        using var process = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
        var path = new StringBuilder(32768);
        var length = path.Capacity;
        if (process.IsInvalid || !QueryFullProcessImageName(process, 0, path, ref length)
            || !string.Equals(Path.GetFullPath(path.ToString()), Path.GetFullPath(expectedExecutable), StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Invalid pipe peer.");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint pid, out uint session);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref int length);
}
