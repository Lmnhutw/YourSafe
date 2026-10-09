using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using PasswordTool.Core.Abstractions;
using PasswordTool.Core.Models;
using PasswordTool.Core.Options;
using PasswordTool.Core.Utilities;

namespace PasswordTool.Core.Hashers.Secure;

public sealed class Argon2idPasswordHasher : IPasswordHasher
{
    public Argon2idPasswordHasher(Argon2idOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!PasswordHashLimits.IsValidArgon2Cost(options.MemoryCost, options.Iterations, options.Parallelism)
            || !PasswordHashLimits.IsValidSaltSize(options.SaltSizeBytes)
            || !PasswordHashLimits.IsValidHashSize(options.HashSizeBytes))
            throw new ArgumentException("Argon2id options exceed the supported cost or size limits.", nameof(options));
        Options = options;
    }

    public string AlgorithmName => PasswordHasherNames.Argon2id;

    public bool IsRecommendedForPasswordStorage => true;

    public Argon2idOptions Options { get; }

    public string HashPassword(string password)
    {
        PasswordHashLimits.ValidatePassword(password);

        var salt = RandomNumberGenerator.GetBytes(Options.SaltSizeBytes);
        var hash = Derive(password, salt, Options.MemoryCost, Options.Iterations, Options.Parallelism, Options.HashSizeBytes);

        return $"ARGON2ID$v=1$m={Options.MemoryCost}$t={Options.Iterations}$p={Options.Parallelism}$salt={Convert.ToBase64String(salt)}$hash={Convert.ToBase64String(hash)}";
    }

    public bool VerifyPassword(string password, string storedHash)
    {
        PasswordHashLimits.ValidatePassword(password);

        if (!TryRead(storedHash, out var memoryCost, out var iterations, out var parallelism, out var salt, out var expectedHash))
        {
            return false;
        }

        var actualHash = Derive(password, salt, memoryCost, iterations, parallelism, expectedHash.Length);
        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }

    public PasswordHashInfo InspectHash(string storedHash)
    {
        if (!TryRead(storedHash, out var memoryCost, out var iterations, out var parallelism, out var salt, out var hash))
        {
            return new PasswordHashInfo
            {
                AlgorithmName = "ARGON2ID",
                IsSecureForPasswordStorage = false,
                Notes = "Invalid Argon2id hash format or Base64 value."
            };
        }

        return new PasswordHashInfo
        {
            AlgorithmName = "ARGON2ID",
            Version = 1,
            Salt = Convert.ToBase64String(salt),
            Hash = Convert.ToBase64String(hash),
            Iterations = iterations,
            MemoryCost = memoryCost,
            Parallelism = parallelism,
            HashSize = hash.Length,
            IsSecureForPasswordStorage = PasswordHashLimits.IsRecommendedArgon2Cost(memoryCost, iterations),
            Notes = "Argon2id memory-hard password hash. The security recommendation depends on the stored memory and iteration costs."
        };
    }

    private static byte[] Derive(string password, byte[] salt, int memoryCost, int iterations, int parallelism, int hashSize)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        try
        {
            using var argon2 = new Argon2id(passwordBytes)
            {
                Salt = salt,
                MemorySize = memoryCost,
                Iterations = iterations,
                DegreeOfParallelism = parallelism
            };
            return argon2.GetBytes(hashSize);
        }
        finally { CryptographicOperations.ZeroMemory(passwordBytes); }
    }

    private static bool TryRead(
        string storedHash,
        out int memoryCost,
        out int iterations,
        out int parallelism,
        out byte[] salt,
        out byte[] hash)
    {
        memoryCost = 0;
        iterations = 0;
        parallelism = 0;
        salt = [];
        hash = [];

        return HashParser.TryParseKeyValueFormat(storedHash, out var algorithmName, out var values)
            && string.Equals(algorithmName, "ARGON2ID", StringComparison.OrdinalIgnoreCase)
            && values.Count == 6
            && HashParser.TryGetInt(values, "v", out var version) && version == 1
            && HashParser.TryGetInt(values, "m", out memoryCost)
            && HashParser.TryGetInt(values, "t", out iterations)
            && HashParser.TryGetInt(values, "p", out parallelism)
            && PasswordHashLimits.IsValidArgon2Cost(memoryCost, iterations, parallelism)
            && HashParser.TryGetBase64(values, "salt", out salt)
            && PasswordHashLimits.IsValidSaltSize(salt.Length)
            && HashParser.TryGetBase64(values, "hash", out hash)
            && PasswordHashLimits.IsValidHashSize(hash.Length);
    }
}
