using PasswordTool.Core.Abstractions;
using PasswordTool.Core.Hashers;
using PasswordTool.Core.Hashers.Identity;
using PasswordTool.Core.Models;
using PasswordTool.Core.Registry;
using PasswordTool.Core.Utilities;

namespace PasswordTool.Core.Inspection;

public sealed class PasswordHashInspector : IPasswordHashInspector
{
    private readonly IPasswordHasherRegistry registry;

    public PasswordHashInspector() : this(new PasswordHasherRegistry())
    {
    }

    public PasswordHashInspector(IPasswordHasherRegistry registry)
    {
        this.registry = registry;
    }

    public PasswordHashInfo Inspect(string storedHash)
    {
        if (string.IsNullOrWhiteSpace(storedHash) || storedHash.Length > PasswordHashLimits.MaxStoredHashCharacters)
        {
            return Unknown("Stored hash is required.");
        }

        try
        {
            var algorithmName = DetectAlgorithm(storedHash);
            if (algorithmName is null)
            {
                return Unknown("Unsupported or invalid hash format.");
            }

            return registry.GetHasher(algorithmName).InspectHash(storedHash);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException)
        {
            return Unknown("Could not inspect hash. The format is invalid or unsupported.");
        }
    }

    private static string? DetectAlgorithm(string storedHash)
    {
        if (storedHash.StartsWith("$2a$", StringComparison.Ordinal)
            || storedHash.StartsWith("$2b$", StringComparison.Ordinal)
            || storedHash.StartsWith("$2x$", StringComparison.Ordinal)
            || storedHash.StartsWith("$2y$", StringComparison.Ordinal))
        {
            return PasswordHasherNames.Bcrypt;
        }

        if (AspNetCoreIdentityPasswordHasherAdapter.CanInspect(storedHash))
        {
            return PasswordHasherNames.AspNetCoreIdentity;
        }

        if (!HashParser.TryParseKeyValueFormat(storedHash, out var formatName, out _))
        {
            return null;
        }

        return formatName.ToUpperInvariant() switch
        {
            "ARGON2ID" => PasswordHasherNames.Argon2id,
            "PBKDF2-SHA256" => PasswordHasherNames.Pbkdf2Sha256,
            "PBKDF2-SHA512" => PasswordHasherNames.Pbkdf2Sha512,
            "SCRYPT" => PasswordHasherNames.Scrypt,
            "LEGACY-MD5" => PasswordHasherNames.Md5,
            "LEGACY-SHA1" => PasswordHasherNames.Sha1,
            "LEGACY-SHA256" => PasswordHasherNames.Sha256Unsalted,
            "LEGACY-SHA512" => PasswordHasherNames.Sha512Unsalted,
            "LEGACY-SHA256-SALTED" => PasswordHasherNames.Sha256Salted,
            "LEGACY-SHA512-SALTED" => PasswordHasherNames.Sha512Salted,
            _ => null
        };
    }

    private static PasswordHashInfo Unknown(string notes)
    {
        return new PasswordHashInfo
        {
            AlgorithmName = "Unknown",
            IsSecureForPasswordStorage = false,
            Notes = notes
        };
    }
}
