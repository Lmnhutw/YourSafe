using System.Text;
using PasswordTool.Core.Abstractions;
using PasswordTool.Core.Models;
using PasswordTool.Core.Options;
using PasswordTool.Core.Utilities;

namespace PasswordTool.Core.Hashers.Secure;

public sealed class BcryptPasswordHasher : IPasswordHasher
{
    public BcryptPasswordHasher(BcryptOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.WorkFactor is < 4 or > PasswordHashLimits.MaxBcryptWorkFactor)
            throw new ArgumentException("Bcrypt work factor exceeds the supported limits.", nameof(options));
        Options = options;
    }

    public string AlgorithmName => PasswordHasherNames.Bcrypt;

    public bool IsRecommendedForPasswordStorage => true;

    public BcryptOptions Options { get; }

    public string HashPassword(string password)
    {
        PasswordHashLimits.ValidatePassword(password);
        if (Encoding.UTF8.GetByteCount(password) > 72)
            throw new ArgumentException("Bcrypt passwords cannot exceed 72 UTF-8 bytes.", nameof(password));
        return BCrypt.Net.BCrypt.HashPassword(password, Options.WorkFactor);
    }

    public bool VerifyPassword(string password, string storedHash)
    {
        PasswordHashLimits.ValidatePassword(password);
        if (Encoding.UTF8.GetByteCount(password) > 72 || !TryRead(storedHash, out _)) return false;

        try
        {
            return BCrypt.Net.BCrypt.Verify(password, storedHash);
        }
        catch (BCrypt.Net.SaltParseException)
        {
            return false;
        }
    }

    public PasswordHashInfo InspectHash(string storedHash)
    {
        if (!TryRead(storedHash, out var cost))
        {
            return new PasswordHashInfo
            {
                AlgorithmName = "bcrypt",
                IsSecureForPasswordStorage = false,
                Notes = "Invalid bcrypt hash format."
            };
        }

        var salt = storedHash.Substring(7, 22);
        var hash = storedHash[29..];

        return new PasswordHashInfo
        {
            AlgorithmName = $"bcrypt {storedHash[..4]}",
            Salt = salt,
            Hash = hash,
            WorkFactor = cost,
            HashSize = hash.Length,
            IsSecureForPasswordStorage = cost >= 10,
            Notes = "Production-safe when configured with an appropriate work factor."
        };
    }

    private static bool TryRead(string storedHash, out int cost)
    {
        cost = 0;
        return storedHash is { Length: 60 } && storedHash[0] == '$' && storedHash[1] == '2'
            && storedHash[2] is 'a' or 'b' or 'x' or 'y' && storedHash[3] == '$' && storedHash[6] == '$'
            && int.TryParse(storedHash.AsSpan(4, 2), out cost)
            && cost is >= 4 and <= PasswordHashLimits.MaxBcryptWorkFactor
            && storedHash.AsSpan(7).IndexOfAnyExcept("./ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789") < 0;
    }
}
