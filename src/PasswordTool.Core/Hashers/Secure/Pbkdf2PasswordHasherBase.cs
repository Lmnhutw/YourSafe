using System.Security.Cryptography;
using System.Text;
using PasswordTool.Core.Abstractions;
using PasswordTool.Core.Models;
using PasswordTool.Core.Options;
using PasswordTool.Core.Utilities;

namespace PasswordTool.Core.Hashers.Secure;

public abstract class Pbkdf2PasswordHasherBase : IPasswordHasher
{
    protected Pbkdf2PasswordHasherBase(Pbkdf2Options options, HashAlgorithmName hashAlgorithmName, string formatName)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Iterations is < 10000 or > PasswordHashLimits.MaxPbkdf2Iterations
            || !PasswordHashLimits.IsValidSaltSize(options.SaltSizeBytes)
            || !PasswordHashLimits.IsValidHashSize(options.HashSizeBytes))
            throw new ArgumentException("PBKDF2 options exceed the supported cost or size limits.", nameof(options));
        Options = options;
        HashAlgorithmName = hashAlgorithmName;
        FormatName = formatName;
    }

    public abstract string AlgorithmName { get; }

    public bool IsRecommendedForPasswordStorage => true;

    protected Pbkdf2Options Options { get; }

    protected HashAlgorithmName HashAlgorithmName { get; }

    protected string FormatName { get; }

    public string HashPassword(string password)
    {
        PasswordHashLimits.ValidatePassword(password);

        var salt = RandomNumberGenerator.GetBytes(Options.SaltSizeBytes);
        var hash = Derive(password, salt, Options.Iterations, Options.HashSizeBytes);

        return $"{FormatName}$v=1$iter={Options.Iterations}$salt={Convert.ToBase64String(salt)}$hash={Convert.ToBase64String(hash)}";
    }

    public bool VerifyPassword(string password, string storedHash)
    {
        PasswordHashLimits.ValidatePassword(password);

        if (!TryRead(storedHash, out var iterations, out var salt, out var expectedHash))
        {
            return false;
        }

        var actualHash = Derive(password, salt, iterations, expectedHash.Length);

        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }

    public PasswordHashInfo InspectHash(string storedHash)
    {
        if (!TryRead(storedHash, out var iterations, out var salt, out var hash))
        {
            return InvalidInfo("Invalid PBKDF2 hash format or Base64 value.");
        }

        return new PasswordHashInfo
        {
            AlgorithmName = FormatName,
            Version = 1,
            Salt = Convert.ToBase64String(salt),
            Hash = Convert.ToBase64String(hash),
            Iterations = iterations,
            HashSize = hash.Length,
            IsSecureForPasswordStorage = iterations >= (HashAlgorithmName == System.Security.Cryptography.HashAlgorithmName.SHA256
                ? PasswordHashLimits.RecommendedPbkdf2Sha256Iterations : PasswordHashLimits.RecommendedPbkdf2Sha512Iterations),
            Notes = "PBKDF2 compatibility format. Current recommendations require at least 600000 SHA256 or 220000 SHA512 iterations."
        };
    }

    protected PasswordHashInfo InvalidInfo(string notes)
    {
        return new PasswordHashInfo
        {
            AlgorithmName = FormatName,
            IsSecureForPasswordStorage = false,
            Notes = notes
        };
    }

    private byte[] Derive(string password, byte[] salt, int iterations, int hashSize)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        try { return Rfc2898DeriveBytes.Pbkdf2(passwordBytes, salt, iterations, HashAlgorithmName, hashSize); }
        finally { CryptographicOperations.ZeroMemory(passwordBytes); }
    }

    private bool TryRead(string storedHash, out int iterations, out byte[] salt, out byte[] hash)
    {
        iterations = 0;
        salt = [];
        hash = [];

        return HashParser.TryParseKeyValueFormat(storedHash, out var algorithmName, out var values)
            && string.Equals(algorithmName, FormatName, StringComparison.OrdinalIgnoreCase)
            && values.Count == 4
            && HashParser.TryGetInt(values, "v", out var version) && version == 1
            && HashParser.TryGetInt(values, "iter", out iterations)
            && iterations is >= 10000 and <= PasswordHashLimits.MaxPbkdf2Iterations
            && HashParser.TryGetBase64(values, "salt", out salt)
            && PasswordHashLimits.IsValidSaltSize(salt.Length)
            && HashParser.TryGetBase64(values, "hash", out hash)
            && PasswordHashLimits.IsValidHashSize(hash.Length);
    }
}
