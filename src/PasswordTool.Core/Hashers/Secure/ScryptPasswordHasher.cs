using System.Security.Cryptography;
using System.Text;
using PasswordTool.Core.Abstractions;
using PasswordTool.Core.Models;
using PasswordTool.Core.Options;
using PasswordTool.Core.Utilities;

namespace PasswordTool.Core.Hashers.Secure;

public sealed class ScryptPasswordHasher : IPasswordHasher
{
    public ScryptPasswordHasher(ScryptOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!PasswordHashLimits.IsValidScryptCost(options.Cost, options.BlockSize, options.Parallelization)
            || !PasswordHashLimits.IsValidSaltSize(options.SaltSizeBytes)
            || !PasswordHashLimits.IsValidHashSize(options.HashSizeBytes))
            throw new ArgumentException("Scrypt options exceed the supported cost or size limits.", nameof(options));
        Options = options;
    }

    public string AlgorithmName => PasswordHasherNames.Scrypt;

    public bool IsRecommendedForPasswordStorage => true;

    public ScryptOptions Options { get; }

    public string HashPassword(string password)
    {
        PasswordHashLimits.ValidatePassword(password);

        var salt = RandomNumberGenerator.GetBytes(Options.SaltSizeBytes);
        var hash = Derive(password, salt, Options.Cost, Options.BlockSize, Options.Parallelization, Options.HashSizeBytes);

        return $"SCRYPT$v=1$N={Options.Cost}$r={Options.BlockSize}$p={Options.Parallelization}$salt={Convert.ToBase64String(salt)}$hash={Convert.ToBase64String(hash)}";
    }

    public bool VerifyPassword(string password, string storedHash)
    {
        PasswordHashLimits.ValidatePassword(password);

        if (!TryRead(storedHash, out var cost, out var blockSize, out var parallelization, out var salt, out var expectedHash))
        {
            return false;
        }

        var actualHash = Derive(password, salt, cost, blockSize, parallelization, expectedHash.Length);
        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }

    public PasswordHashInfo InspectHash(string storedHash)
    {
        if (!TryRead(storedHash, out var cost, out var blockSize, out var parallelization, out var salt, out var hash))
        {
            return new PasswordHashInfo
            {
                AlgorithmName = "SCRYPT",
                IsSecureForPasswordStorage = false,
                Notes = "Invalid scrypt hash format or Base64 value."
            };
        }

        return new PasswordHashInfo
        {
            AlgorithmName = "SCRYPT",
            Version = 1,
            Salt = Convert.ToBase64String(salt),
            Hash = Convert.ToBase64String(hash),
            WorkFactor = cost,
            MemoryCost = blockSize,
            Parallelism = parallelization,
            HashSize = hash.Length,
            IsSecureForPasswordStorage = PasswordHashLimits.IsRecommendedScryptCost(cost, blockSize, parallelization),
            Notes = "Scrypt compatibility format. Current recommendations require N=131072/r=8/p=1 or equivalent memory/parallelization costs."
        };
    }

    private static byte[] Derive(string password, byte[] salt, int cost, int blockSize, int parallelization, int hashSize)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        try { return ScryptKeyDerivation.DeriveKey(passwordBytes, salt, cost, blockSize, parallelization, hashSize); }
        finally { CryptographicOperations.ZeroMemory(passwordBytes); }
    }

    private static bool TryRead(
        string storedHash,
        out int cost,
        out int blockSize,
        out int parallelization,
        out byte[] salt,
        out byte[] hash)
    {
        cost = 0;
        blockSize = 0;
        parallelization = 0;
        salt = [];
        hash = [];

        return HashParser.TryParseKeyValueFormat(storedHash, out var algorithmName, out var values)
            && string.Equals(algorithmName, "SCRYPT", StringComparison.OrdinalIgnoreCase)
            && values.Count == 6
            && HashParser.TryGetInt(values, "v", out var version) && version == 1
            && HashParser.TryGetInt(values, "N", out cost)
            && HashParser.TryGetInt(values, "r", out blockSize)
            && HashParser.TryGetInt(values, "p", out parallelization)
            && PasswordHashLimits.IsValidScryptCost(cost, blockSize, parallelization)
            && HashParser.TryGetBase64(values, "salt", out salt)
            && PasswordHashLimits.IsValidSaltSize(salt.Length)
            && HashParser.TryGetBase64(values, "hash", out hash)
            && PasswordHashLimits.IsValidHashSize(hash.Length);
    }
}
