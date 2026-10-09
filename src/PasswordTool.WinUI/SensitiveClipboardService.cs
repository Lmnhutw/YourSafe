using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using PasswordTool.Presentation;
using Windows.ApplicationModel.DataTransfer;

namespace PasswordTool_WinUI;

internal sealed class SensitiveClipboardService : ISensitiveClipboardService, IDisposable
{
    private static readonly TimeSpan ClearDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);
    private readonly object sync = new();
    private CancellationTokenSource? expiration;
    private byte[]? ownedValueHash;
    private long ownershipGeneration;
    private uint ownedSequence;
    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")]
    private static extern nint GetClipboardOwner();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(nint owner);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();
    [DllImport("user32.dll")]
    private static extern nint SetClipboardData(uint format, nint memory);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string name);
    [DllImport("kernel32.dll")]
    private static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll")]
    private static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(nint memory);
    [DllImport("kernel32.dll")]
    private static extern nint GlobalFree(nint memory);
    private bool disposed;

    public Task CopyAsync(string value, CancellationToken cancellationToken = default) =>
        CopyAsync(value, () => true, cancellationToken);

    public Task CopyAsync(string value, Func<bool> canCopy, CancellationToken cancellationToken = default, bool clearAutomatically = true)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(canCopy);
        cancellationToken.ThrowIfCancellationRequested();
        if (!canCopy()) throw new OperationCanceledException();

        return RunOnUiThreadAsync(() =>
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            SetText(value, canCopy, cancellationToken);
            // Windows may add formats on close; capture the committed sequence.
            var sequence = GetClipboardSequenceNumber();

            lock (sync)
            {
                ownershipGeneration++;
                ClearOwnedHash();
                expiration?.Cancel();
                expiration?.Dispose();
                expiration = null;
                if (clearAutomatically)
                {
                    ownedValueHash = HashValue(value);
                    ownedSequence = sequence;
                    expiration = new CancellationTokenSource();
                    _ = ClearAfterDelayAsync(ClearDelay, expiration.Token);
                }
            }

            return Task.CompletedTask;
        });
    }

    public Task ClearOwnedValueAsync(CancellationToken cancellationToken = default)
    {
        if (disposed) return Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();
        return RunOnUiThreadAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[]? expected;
            long expectedGeneration;
            uint expectedSequence;
            lock (sync)
            {
                expected = ownedValueHash is null ? null : [.. ownedValueHash];
                expectedGeneration = ownershipGeneration;
                expectedSequence = ownedSequence;
            }

            if (expected is null) return;
            var releaseOwnership = false;
            try
            {
                // Each cleanup is bounded; transient contention retains ownership for a later retry.
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        if (GetClipboardSequenceNumber() != expectedSequence || GetClipboardOwner() != App.WindowHandle)
                        { releaseOwnership = true; return; }
                        var content = Clipboard.GetContent();
                        if (!content.Contains(StandardDataFormats.Text)) { releaseOwnership = true; return; }
                        var current = await content.GetTextAsync();
                        var currentHash = HashValue(current);
                        try
                        {
                            lock (sync)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                if (expectedGeneration != ownershipGeneration) return;
                                if (!CryptographicOperations.FixedTimeEquals(currentHash, expected)) { releaseOwnership = true; return; }
                                if (OpenClipboard(App.WindowHandle))
                                {
                                    try
                                    {
                                        cancellationToken.ThrowIfCancellationRequested();
                                        if (expectedSequence != GetClipboardSequenceNumber() || GetClipboardOwner() != App.WindowHandle)
                                        { releaseOwnership = true; return; }
                                        if (EmptyClipboard()) { releaseOwnership = true; return; }
                                    }
                                    finally { CloseClipboard(); }
                                }
                            }
                        }
                        finally { CryptographicOperations.ZeroMemory(currentHash); }
                    }
                    catch (COMException)
                    {
                        // A busy clipboard can also reject the WinRT read; retain ownership and retry.
                    }
                    if (attempt < 2) await Task.Delay(250, cancellationToken);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(expected);
                lock (sync)
                {
                    if (releaseOwnership && expectedGeneration == ownershipGeneration)
                    {
                        ClearOwnedHash();
                        expiration?.Cancel();
                        expiration?.Dispose();
                        expiration = null;
                    }
                    else if (expectedGeneration == ownershipGeneration && !disposed && !cancellationToken.IsCancellationRequested)
                    {
                        expiration?.Cancel();
                        expiration?.Dispose();
                        expiration = new CancellationTokenSource();
                        _ = ClearAfterDelayAsync(RetryDelay, expiration.Token);
                    }
                }
            }
        });
    }

    private async Task ClearAfterDelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
            await ClearOwnedValueAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static void SetText(string value, Func<bool> canCopy, CancellationToken cancellationToken)
    {
        // WinRT writes require foreground; native messaging must also copy while the browser is active.
        var bytes = new byte[checked(Encoding.Unicode.GetByteCount(value) + 2)];
        var exclude = RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
        if (exclude == 0 || !OpenClipboard(App.WindowHandle))
            throw new InvalidOperationException("The clipboard is unavailable.");
        var changed = false;
        try
        {
            Encoding.Unicode.GetBytes(value.AsSpan(), bytes);
            cancellationToken.ThrowIfCancellationRequested();
            if (!canCopy()) throw new OperationCanceledException();
            if (!EmptyClipboard()) throw new InvalidOperationException("The clipboard is unavailable.");
            changed = true;
            void ValidateCopy()
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!canCopy()) throw new OperationCanceledException();
            }
            SetClipboardBytes(exclude, [0], ValidateCopy);
            SetClipboardBytes(13, bytes, ValidateCopy); // CF_UNICODETEXT, including its zero terminator.
            ValidateCopy();
            // Immediate SetClipboardData updates the sequence; other writers are excluded until CloseClipboard.
        }
        catch
        {
            if (changed) EmptyClipboard();
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            CloseClipboard();
        }
    }

    private static void SetClipboardBytes(uint format, byte[] bytes, Action validateCopy)
    {
        var memory = GlobalAlloc(0x42, (nuint)bytes.Length); // GMEM_MOVEABLE | GMEM_ZEROINIT
        if (memory == 0) throw new InvalidOperationException("The clipboard is unavailable.");
        try
        {
            var pointer = GlobalLock(memory);
            if (pointer == 0) throw new InvalidOperationException("The clipboard is unavailable.");
            try { Marshal.Copy(bytes, 0, pointer, bytes.Length); }
            finally { GlobalUnlock(memory); }
            validateCopy();
            if (SetClipboardData(format, memory) == 0)
                throw new InvalidOperationException("The clipboard is unavailable.");
            memory = 0; // Windows owns the allocation after SetClipboardData succeeds.
        }
        finally
        {
            if (memory != 0)
            {
                var pointer = GlobalLock(memory);
                if (pointer != 0)
                {
                    CryptographicOperations.ZeroMemory(bytes);
                    Marshal.Copy(bytes, 0, pointer, bytes.Length);
                    GlobalUnlock(memory);
                }
                GlobalFree(memory);
            }
        }
    }

    private static Task RunOnUiThreadAsync(Func<Task> action)
    {
        if (App.DispatcherQueue.HasThreadAccess) return action();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!App.DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    await action();
                    completion.SetResult();
                }
                catch (Exception exception)
                {
                    completion.SetException(exception);
                }
            }))
        {
            completion.SetException(new InvalidOperationException("The PasswordTool window is no longer available."));
        }

        return completion.Task;
    }

    private void ClearOwnedHash()
    {
        if (ownedValueHash is null) return;
        CryptographicOperations.ZeroMemory(ownedValueHash);
        ownedValueHash = null;
    }

    private static byte[] HashValue(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        try { return SHA256.HashData(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lock (sync)
        {
            expiration?.Cancel();
            expiration?.Dispose();
            expiration = null;
            ClearOwnedHash();
        }
    }
}
