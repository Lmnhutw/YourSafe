using System.Security.Cryptography;
using System.Text;
using PasswordTool.Core.Abstractions;
using PasswordTool.Core.Models;
using PasswordTool.Core.Utilities;

namespace PasswordTool.Core.Hashers.Educational;

public abstract class LegacyEducationalHasherBase : IPasswordHasher
{
    private const string Warning = "Educational only - do not use for real password storage.";

    protected LegacyEducationalHasherBase(string formatName, bool usesSalt)
    {
        FormatName = formatName;
        UsesSalt = usesSalt;
    }

    public abstract string AlgorithmName { get; }

    public bool IsRecommendedForPasswordStorage => false;

    protected string FormatName { get; }

    protected bool UsesSalt { get; }

    public string HashPassword(string password)
    {
        PasswordHashLimits.ValidatePassword(password);

        var salt = UsesSalt ? RandomNumberGenerator.GetBytes(16) : [];
        var hash = ComputeHash(password, salt);

        return UsesSalt
            ? $"{FormatName}$salt={Convert.ToBase64String(salt)}$hash={Convert.ToBase64String(hash)}"
            : $"{FormatName}$hash={Convert.ToBase64String(hash)}";
    }

    public bool VerifyPassword(string password, string storedHash)
    {
        PasswordHashLimits.ValidatePassword(password);

        if (!TryRead(storedHash, out var salt, out var expectedHash))
        {
            return false;
        }

        var actualHash = ComputeHash(password, salt);
        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }

    public PasswordHashInfo InspectHash(string storedHash)
    {
        if (!TryRead(storedHash, out var salt, out var hash))
        {
            return new PasswordHashInfo
            {
                AlgorithmName = FormatName,
                IsSecureForPasswordStorage = false,
                Notes = $"Invalid legacy hash format. {Warning}"
            };
        }

        return new PasswordHashInfo
        {
            AlgorithmName = FormatName,
            Salt = UsesSalt ? Convert.ToBase64String(salt) : null,
            Hash = Convert.ToBase64String(hash),
            HashSize = hash.Length,
            IsSecureForPasswordStorage = false,
            Notes = Warning
        };
    }

    private byte[] ComputeHash(string password, byte[] salt)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        var input = UsesSalt ? [.. salt, .. passwordBytes] : passwordBytes;

        try
        {
            return FormatName switch
            {
                "LEGACY-MD5" => MD5.HashData(input),
                "LEGACY-SHA1" => SHA1.HashData(input),
                "LEGACY-SHA256" or "LEGACY-SHA256-SALTED" => SHA256.HashData(input),
                "LEGACY-SHA512" or "LEGACY-SHA512-SALTED" => SHA512.HashData(input),
                _ => throw new InvalidOperationException($"Unsupported legacy format '{FormatName}'.")
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
            if (UsesSalt) CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    private bool TryRead(string storedHash, out byte[] salt, out byte[] hash)
    {
        salt = [];
        hash = [];

        if (!HashParser.TryParseKeyValueFormat(storedHash, out var algorithmName, out var values)
            || !string.Equals(algorithmName, FormatName, StringComparison.OrdinalIgnoreCase)
            || !HashParser.TryGetBase64(values, "hash", out hash)
            || hash.Length == 0)
        {
            return false;
        }

        return !UsesSalt
            || (HashParser.TryGetBase64(values, "salt", out salt) && salt.Length > 0);
    }
}
