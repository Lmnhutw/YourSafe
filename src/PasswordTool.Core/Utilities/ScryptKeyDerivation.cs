using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace PasswordTool.Core.Utilities;

public static class ScryptKeyDerivation
{
    public static byte[] DeriveKey(byte[] password, byte[] salt, int cost, int blockSize, int parallelization, int outputBytes)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(salt);
        if (!PasswordHashLimits.IsValidScryptCost(cost, blockSize, parallelization))
        {
            throw new ArgumentOutOfRangeException(nameof(cost), "Scrypt parameters exceed the supported CPU or memory limits.");
        }

        if (outputBytes is < 1 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(outputBytes), "Output size must be between 1 and 64 bytes.");
        }

        var blockLength = 128 * blockSize;
        var b = Pbkdf2(password, salt, 1, parallelization * blockLength);

        try
        {
            for (var i = 0; i < parallelization; i++)
            {
                var block = b.AsSpan(i * blockLength, blockLength);
                ScryptRomix(block, cost, blockSize);
            }
            return Pbkdf2(password, b, 1, outputBytes);
        }
        finally { CryptographicOperations.ZeroMemory(b); }
    }

    private static byte[] Pbkdf2(byte[] password, byte[] salt, int iterations, int outputBytes)
    {
        return Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, outputBytes);
    }

    private static void ScryptRomix(Span<byte> block, int cost, int blockSize)
    {
        var v = new byte[cost * block.Length];
        var x = block.ToArray();

        try
        {
            for (var i = 0; i < cost; i++)
            {
                x.CopyTo(v.AsSpan(i * block.Length));
                BlockMix(x, blockSize);
            }
            for (var i = 0; i < cost; i++)
            {
                var j = Integerify(x, blockSize) & (ulong)(cost - 1);
                Xor(x, v.AsSpan((int)j * block.Length, block.Length));
                BlockMix(x, blockSize);
            }
            x.CopyTo(block);
        }
        finally { CryptographicOperations.ZeroMemory(v); CryptographicOperations.ZeroMemory(x); }
    }

    private static ulong Integerify(byte[] block, int blockSize)
    {
        var offset = (2 * blockSize - 1) * 64;
        return BinaryPrimitives.ReadUInt64LittleEndian(block.AsSpan(offset, 8));
    }

    private static void BlockMix(byte[] block, int blockSize)
    {
        Span<byte> x = stackalloc byte[64];
        block.AsSpan((2 * blockSize - 1) * 64, 64).CopyTo(x);

        var y = new byte[block.Length];
        try
        {
            for (var i = 0; i < 2 * blockSize; i++)
            {
                Xor(x, block.AsSpan(i * 64, 64));
                Salsa208(x);
                var destinationOffset = i % 2 == 0
                    ? (i / 2) * 64
                    : (blockSize + i / 2) * 64;
                x.CopyTo(y.AsSpan(destinationOffset, 64));
            }
            y.CopyTo(block.AsSpan());
        }
        finally { CryptographicOperations.ZeroMemory(y); CryptographicOperations.ZeroMemory(x); }
    }

    private static void Xor(byte[] destination, ReadOnlySpan<byte> source)
    {
        for (var i = 0; i < destination.Length; i++)
        {
            destination[i] ^= source[i];
        }
    }

    private static void Xor(Span<byte> destination, ReadOnlySpan<byte> source)
    {
        for (var i = 0; i < destination.Length; i++)
        {
            destination[i] ^= source[i];
        }
    }

    private static void Salsa208(Span<byte> block)
    {
        Span<uint> x = stackalloc uint[16];
        Span<uint> original = stackalloc uint[16];

        for (var i = 0; i < 16; i++)
        {
            x[i] = BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(i * 4, 4));
            original[i] = x[i];
        }

        for (var i = 0; i < 8; i += 2)
        {
            x[4] ^= RotateLeft(x[0] + x[12], 7);
            x[8] ^= RotateLeft(x[4] + x[0], 9);
            x[12] ^= RotateLeft(x[8] + x[4], 13);
            x[0] ^= RotateLeft(x[12] + x[8], 18);

            x[9] ^= RotateLeft(x[5] + x[1], 7);
            x[13] ^= RotateLeft(x[9] + x[5], 9);
            x[1] ^= RotateLeft(x[13] + x[9], 13);
            x[5] ^= RotateLeft(x[1] + x[13], 18);

            x[14] ^= RotateLeft(x[10] + x[6], 7);
            x[2] ^= RotateLeft(x[14] + x[10], 9);
            x[6] ^= RotateLeft(x[2] + x[14], 13);
            x[10] ^= RotateLeft(x[6] + x[2], 18);

            x[3] ^= RotateLeft(x[15] + x[11], 7);
            x[7] ^= RotateLeft(x[3] + x[15], 9);
            x[11] ^= RotateLeft(x[7] + x[3], 13);
            x[15] ^= RotateLeft(x[11] + x[7], 18);

            x[1] ^= RotateLeft(x[0] + x[3], 7);
            x[2] ^= RotateLeft(x[1] + x[0], 9);
            x[3] ^= RotateLeft(x[2] + x[1], 13);
            x[0] ^= RotateLeft(x[3] + x[2], 18);

            x[6] ^= RotateLeft(x[5] + x[4], 7);
            x[7] ^= RotateLeft(x[6] + x[5], 9);
            x[4] ^= RotateLeft(x[7] + x[6], 13);
            x[5] ^= RotateLeft(x[4] + x[7], 18);

            x[11] ^= RotateLeft(x[10] + x[9], 7);
            x[8] ^= RotateLeft(x[11] + x[10], 9);
            x[9] ^= RotateLeft(x[8] + x[11], 13);
            x[10] ^= RotateLeft(x[9] + x[8], 18);

            x[12] ^= RotateLeft(x[15] + x[14], 7);
            x[13] ^= RotateLeft(x[12] + x[15], 9);
            x[14] ^= RotateLeft(x[13] + x[12], 13);
            x[15] ^= RotateLeft(x[14] + x[13], 18);
        }

        for (var i = 0; i < 16; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(i * 4, 4), x[i] + original[i]);
        }
        CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(x));
        CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(original));
    }

    private static uint RotateLeft(uint value, int count)
    {
        return (value << count) | (value >> (32 - count));
    }
}
