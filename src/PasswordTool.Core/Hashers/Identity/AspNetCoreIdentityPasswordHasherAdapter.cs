using System.Buffers.Binary;
using Microsoft.AspNetCore.Identity;
using PasswordTool.Core.Abstractions;
using PasswordTool.Core.Models;
using PasswordTool.Core.Utilities;

namespace PasswordTool.Core.Hashers.Identity;

public sealed class AspNetCoreIdentityPasswordHasherAdapter : IPasswordHasher
{
    private readonly PasswordHasher<object> passwordHasher = new();

    public string AlgorithmName => PasswordHasherNames.AspNetCoreIdentity;

    public bool IsRecommendedForPasswordStorage => true;

    public string HashPassword(string password)
    {
        PasswordHashLimits.ValidatePassword(password);

        return passwordHasher.HashPassword(new object(), password);
    }

    public bool VerifyPassword(string password, string storedHash)
    {
        PasswordHashLimits.ValidatePassword(password);
        if (!TryRead(storedHash, out _)) return false;

        try
        {
            var result = passwordHasher.VerifyHashedPassword(new object(), storedHash, password);
            return result is PasswordVerificationResult.Success or PasswordVerificationResult.SuccessRehashNeeded;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public PasswordHashInfo InspectHash(string storedHash)
    {
        if (!TryRead(storedHash, out var info))
        {
            return new PasswordHashInfo
            {
                AlgorithmName = PasswordHasherNames.AspNetCoreIdentity,
                IsSecureForPasswordStorage = false,
                Notes = "Invalid ASP.NET Core Identity password hash format."
            };
        }

        return info;
    }

    public static bool CanInspect(string storedHash)
    {
        return TryRead(storedHash, out _);
    }

    private static bool TryRead(string storedHash, out PasswordHashInfo info)
    {
        info = new PasswordHashInfo
        {
            AlgorithmName = PasswordHasherNames.AspNetCoreIdentity,
            IsSecureForPasswordStorage = false,
            Notes = "Invalid ASP.NET Core Identity password hash format."
        };

        if (string.IsNullOrWhiteSpace(storedHash) || storedHash.Length > PasswordHashLimits.MaxStoredHashCharacters)
        {
            return false;
        }

        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(storedHash);
        }
        catch (FormatException)
        {
            return false;
        }

        if (decoded.Length == 0)
        {
            return false;
        }

        return decoded[0] switch
        {
            0x00 => TryReadV2(decoded, out info),
            0x01 => TryReadV3(decoded, out info),
            _ => false
        };
    }

    private static bool TryReadV2(byte[] decoded, out PasswordHashInfo info)
    {
        info = new PasswordHashInfo();

        const int saltSize = 16;
        const int headerSize = 1;

        if (decoded.Length != headerSize + saltSize + 32)
        {
            return false;
        }

        var salt = decoded.AsSpan(headerSize, saltSize).ToArray();
        var hash = decoded.AsSpan(headerSize + saltSize).ToArray();

        info = new PasswordHashInfo
        {
            AlgorithmName = PasswordHasherNames.AspNetCoreIdentity,
            Version = 2,
            Salt = Convert.ToBase64String(salt),
            Hash = Convert.ToBase64String(hash),
            Iterations = 1000,
            HashSize = hash.Length,
            IsSecureForPasswordStorage = false,
            Notes = "ASP.NET Identity V2 format. Valid framework format, but lower iteration count than modern Identity V3 hashes."
        };

        return true;
    }

    private static bool TryReadV3(byte[] decoded, out PasswordHashInfo info)
    {
        info = new PasswordHashInfo();

        const int headerSize = 13;
        if (decoded.Length <= headerSize)
        {
            return false;
        }

        var prf = BinaryPrimitives.ReadUInt32BigEndian(decoded.AsSpan(1, 4));
        var iterations = BinaryPrimitives.ReadInt32BigEndian(decoded.AsSpan(5, 4));
        var saltLength = BinaryPrimitives.ReadInt32BigEndian(decoded.AsSpan(9, 4));

        if (prf > 2 || iterations is < 10000 or > PasswordHashLimits.MaxPbkdf2Iterations
            || !PasswordHashLimits.IsValidSaltSize(saltLength) || saltLength > decoded.Length - headerSize
            || !PasswordHashLimits.IsValidHashSize(decoded.Length - headerSize - saltLength))
        {
            return false;
        }

        var salt = decoded.AsSpan(headerSize, saltLength).ToArray();
        var hash = decoded.AsSpan(headerSize + saltLength).ToArray();

        info = new PasswordHashInfo
        {
            AlgorithmName = PasswordHasherNames.AspNetCoreIdentity,
            Version = 3,
            Salt = Convert.ToBase64String(salt),
            Hash = Convert.ToBase64String(hash),
            Iterations = iterations,
            HashSize = hash.Length,
            IsSecureForPasswordStorage = prf switch
            {
                1 => iterations >= PasswordHashLimits.RecommendedPbkdf2Sha256Iterations,
                2 => iterations >= PasswordHashLimits.RecommendedPbkdf2Sha512Iterations,
                _ => false
            },
            Notes = $"ASP.NET Core Identity V3 framework format using {GetPrfName(prf)}. A valid framework format can still need a cost upgrade."
        };

        return true;
    }

    private static string GetPrfName(uint prf)
    {
        return prf switch
        {
            0 => "PBKDF2-HMAC-SHA1",
            1 => "PBKDF2-HMAC-SHA256",
            2 => "PBKDF2-HMAC-SHA512",
            _ => $"unknown PRF value {prf}"
        };
    }
}
