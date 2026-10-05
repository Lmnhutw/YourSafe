using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using PasswordTool.Autofill;

namespace PasswordTool.Autofill.Transport;

public static class Framing
{
    public static async Task<byte[]?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        var first = await stream.ReadAsync(header.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
        if (first == 0) return null;
        await stream.ReadExactlyAsync(header.AsMemory(1), cancellationToken).ConfigureAwait(false);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (size == 0 || size > WireProtocol.MaxFrameBytes) throw new InvalidDataException("Invalid frame length.");
        var bytes = new byte[(int)size];
        try
        {
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            _ = new UTF8Encoding(false, true).GetCharCount(bytes);
            return bytes;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw;
        }
    }

    public static async Task WriteAsync(Stream stream, byte[] bytes, CancellationToken cancellationToken, Func<bool>? canDeliver = null)
    {
        if (bytes.Length == 0 || bytes.Length > WireProtocol.MaxFrameBytes) throw new InvalidDataException("Invalid frame length.");
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)bytes.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        // A lock can begin while the prefix write is pending; close the incomplete frame rather than deliver stale data.
        if (canDeliver is not null && !canDeliver()) throw new OperationCanceledException();
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
