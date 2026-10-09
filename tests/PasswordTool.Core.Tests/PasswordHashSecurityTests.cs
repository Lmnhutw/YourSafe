using System.Buffers.Binary;
using PasswordTool.Core.Hashers;
using PasswordTool.Core.Hashers.Identity;
using PasswordTool.Core.Hashers.Secure;
using PasswordTool.Core.Options;
using PasswordTool.Core.Registry;
using PasswordTool.Core.Utilities;

namespace PasswordTool.Core.Tests;

public sealed class PasswordHashSecurityTests
{
    private const string Salt = "AAAAAAAAAAAAAAAAAAAAAA==";
    private const string Hash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

    public static IEnumerable<object[]> InvalidCosts()
    {
        foreach (var name in new[] { PasswordHasherNames.Pbkdf2Sha256, PasswordHasherNames.Pbkdf2Sha512 })
        {
            var prefix = name == PasswordHasherNames.Pbkdf2Sha256 ? "PBKDF2-SHA256" : "PBKDF2-SHA512";
            foreach (var cost in new[] { "2147483647", "2147483648", "1000001", "-1", "1" })
                yield return [name, $"{prefix}$v=1$iter={cost}$salt={Salt}$hash={Hash}"];
            yield return [name, $"{prefix}$iter=210000$salt={Salt}$hash={Hash}"];
            yield return [name, $"{prefix}$v=2$iter=210000$salt={Salt}$hash={Hash}"];
            yield return [name, $"{prefix}$v=1$v=1$iter=210000$salt={Salt}$hash={Hash}"];
            yield return [name, $"{prefix}$v=1$iter=210000$salt={Salt}$hash={Convert.ToBase64String(new byte[65])}"];
        }
        foreach (var cost in new[] { "m=2147483647$t=3$p=2", "m=65536$t=2147483647$p=2", "m=65536$t=3$p=2147483647", "m=131072$t=10$p=8", "m=1$t=1$p=1" })
            yield return [PasswordHasherNames.Argon2id, $"ARGON2ID$v=1${cost}$salt={Salt}$hash={Hash}"];
        foreach (var cost in new[] { "N=1073741824$r=8$p=1", "N=16384$r=2147483647$p=1", "N=16384$r=8$p=2147483647", "N=131072$r=32$p=16", "N=3$r=8$p=1" })
            yield return [PasswordHasherNames.Scrypt, $"SCRYPT$v=1${cost}$salt={Salt}$hash={Hash}"];
        yield return [PasswordHasherNames.Bcrypt, "$2a$31$" + new string('A', 53)];
        yield return [PasswordHasherNames.Bcrypt, "$2a$15$" + new string('A', 53)];
        yield return [PasswordHasherNames.Bcrypt, "$2a$12$" + new string('!', 53)];
    }

    [Theory]
    [MemberData(nameof(InvalidCosts))]
    public void Malformed_or_unbounded_hashes_are_rejected_before_running_a_kdf(string algorithm, string storedHash)
    {
        var hasher = new PasswordHasherRegistry().GetHasher(algorithm);
        Assert.False(hasher.VerifyPassword("test password", storedHash));
        Assert.False(hasher.InspectHash(storedHash).IsSecureForPasswordStorage);
    }

    [Theory]
    [InlineData(int.MaxValue, 16, 2)]
    [InlineData(1000001, 16, 2)]
    [InlineData(100000, int.MaxValue, 2)]
    [InlineData(100000, 16, 99)]
    public void Identity_rejects_cost_and_header_overflow_before_framework_verification(int iterations, int saltLength, int prf)
    {
        var decoded = new byte[61];
        decoded[0] = 1;
        BinaryPrimitives.WriteInt32BigEndian(decoded.AsSpan(1, 4), prf);
        BinaryPrimitives.WriteInt32BigEndian(decoded.AsSpan(5, 4), iterations);
        BinaryPrimitives.WriteInt32BigEndian(decoded.AsSpan(9, 4), saltLength);
        var storedHash = Convert.ToBase64String(decoded);
        var hasher = new AspNetCoreIdentityPasswordHasherAdapter();
        Assert.False(hasher.VerifyPassword("test password", storedHash));
        Assert.False(hasher.InspectHash(storedHash).IsSecureForPasswordStorage);
    }

    [Fact]
    public void Parser_rejects_duplicate_fields_empty_fields_and_oversized_hashes()
    {
        Assert.False(HashParser.TryParseKeyValueFormat("PBKDF2-SHA256$v=1$V=1", out _, out _));
        Assert.False(HashParser.TryParseKeyValueFormat("PBKDF2-SHA256$$v=1", out _, out _));
        Assert.False(HashParser.TryParseKeyValueFormat(new string('a', 2049), out _, out _));
    }

    [Theory]
    [InlineData(PasswordHasherNames.Pbkdf2Sha256, "PBKDF2-SHA256$v=1$iter=210000", false)]
    [InlineData(PasswordHasherNames.Pbkdf2Sha256, "PBKDF2-SHA256$v=1$iter=600000", true)]
    [InlineData(PasswordHasherNames.Pbkdf2Sha512, "PBKDF2-SHA512$v=1$iter=210000", false)]
    [InlineData(PasswordHasherNames.Pbkdf2Sha512, "PBKDF2-SHA512$v=1$iter=220000", true)]
    [InlineData(PasswordHasherNames.Scrypt, "SCRYPT$v=1$N=16384$r=8$p=1", false)]
    [InlineData(PasswordHasherNames.Scrypt, "SCRYPT$v=1$N=16384$r=8$p=5", true)]
    [InlineData(PasswordHasherNames.Argon2id, "ARGON2ID$v=1$m=8192$t=1$p=1", false)]
    [InlineData(PasswordHasherNames.Argon2id, "ARGON2ID$v=1$m=65536$t=3$p=2", true)]
    public void Inspection_distinguishes_compatible_costs_from_recommended_strength(string algorithm, string prefix, bool recommended)
    {
        var hasher = new PasswordHasherRegistry().GetHasher(algorithm);
        Assert.Equal(recommended, hasher.InspectHash($"{prefix}$salt={Salt}$hash={Hash}").IsSecureForPasswordStorage);
    }

    [Fact]
    public void Bcrypt_never_silently_truncates_long_passwords()
    {
        var hasher = new BcryptPasswordHasher(new BcryptOptions());
        var password = new string('a', 72);
        var storedHash = hasher.HashPassword(password);
        Assert.True(hasher.VerifyPassword(password, storedHash));
        Assert.False(hasher.VerifyPassword(password + "different suffix", storedHash));
        Assert.Throws<ArgumentException>(() => hasher.HashPassword(new string('é', 37)));
    }

    [Fact]
    public void Kdf_options_and_direct_scrypt_calls_cannot_exceed_resource_bounds()
    {
        Assert.Throws<ArgumentException>(() => new Pbkdf2Sha256PasswordHasher(new Pbkdf2Options { Iterations = int.MaxValue }));
        Assert.Throws<ArgumentException>(() => new Argon2idPasswordHasher(new Argon2idOptions { MemoryCost = int.MaxValue }));
        Assert.Throws<ArgumentException>(() => new ScryptPasswordHasher(new ScryptOptions { BlockSize = int.MaxValue }));
        Assert.Throws<ArgumentException>(() => new BcryptPasswordHasher(new BcryptOptions { WorkFactor = 31 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => ScryptKeyDerivation.DeriveKey([], [], 16384, int.MaxValue, int.MaxValue, 32));
        Assert.Throws<ArgumentOutOfRangeException>(() => ScryptKeyDerivation.DeriveKey([], [], 16384, 8, 1, int.MaxValue));
        Assert.Throws<ArgumentException>(() => new PasswordHasherRegistry().GetHasher(PasswordHasherNames.Argon2id).HashPassword(new string('x', 4097)));
    }
}
