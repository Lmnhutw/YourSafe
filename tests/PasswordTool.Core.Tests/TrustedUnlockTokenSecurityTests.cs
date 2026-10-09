using System.Security.Cryptography;
using PasswordTool.Core.Models;
using PasswordTool.Core.Services;

namespace PasswordTool.Core.Tests;

public sealed class TrustedUnlockTokenSecurityTests
{
    private const string Secret = "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP";

    [Fact]
    public void Protected_token_roundtrips_both_secrets_and_expires_at_the_deadline()
    {
        if (!OperatingSystem.IsWindows()) return;
        var service = new TrustedUnlockTokenService();
        var fingerprint = RandomNumberGenerator.GetBytes(32);
        var key = RandomNumberGenerator.GetBytes(32);
        var now = DateTimeOffset.UtcNow;
        var token = service.CreateToken(key, Secret, fingerprint, now, now.AddDays(1));
        Assert.Equal(3, token.Version);
        Assert.True(service.TryUnprotectVaultKey(token, fingerprint, now, out var actualKey, out _));
        Assert.Equal(key, actualKey);
        CryptographicOperations.ZeroMemory(actualKey);
        Assert.True(service.TryUnprotectAuthenticatorSecret(token, fingerprint, now, out var actualSecret, out _));
        Assert.Equal(OtpNet.Base32Encoding.ToBytes(Secret), actualSecret);
        CryptographicOperations.ZeroMemory(actualSecret);
        Assert.False(service.IsTokenUsable(token, fingerprint, token.ExpiresAt));
    }

    [Theory]
    [InlineData("created")]
    [InlineData("expires")]
    [InlineData("extend-expired")]
    [InlineData("fingerprint")]
    [InlineData("old-version")]
    [InlineData("ciphertext")]
    [InlineData("null-fingerprint")]
    public void Unauthenticated_metadata_changes_cannot_release_either_secret(string mutation)
    {
        if (!OperatingSystem.IsWindows()) return;
        var service = new TrustedUnlockTokenService();
        var fingerprint = RandomNumberGenerator.GetBytes(32);
        var now = DateTimeOffset.UtcNow;
        var created = now.AddHours(-2);
        var token = service.CreateToken(RandomNumberGenerator.GetBytes(32), Secret, fingerprint, created, created.AddDays(1));
        switch (mutation)
        {
            case "created": token.CreatedAt = token.CreatedAt.AddMinutes(-1); break;
            case "expires": token.ExpiresAt = token.ExpiresAt.AddMinutes(-1); break;
            case "extend-expired":
                now = now.AddDays(2);
                token.CreatedAt = token.CreatedAt.AddDays(2);
                token.ExpiresAt = token.ExpiresAt.AddDays(2);
                break;
            case "fingerprint": token.ConfigFingerprintBase64 = Convert.ToBase64String(new byte[32]); break;
            case "old-version": token.Version = 2; break;
            case "ciphertext":
                token.ProtectedAuthenticatorSecretBase64 = Convert.ToBase64String(new byte[64]);
                token.ProtectedVaultKeyBase64 = Convert.ToBase64String(new byte[64]);
                break;
            case "null-fingerprint": token.ConfigFingerprintBase64 = null!; break;
        }
        Assert.False(service.TryUnprotectAuthenticatorSecret(token, fingerprint, now, out var secret, out var error));
        Assert.Empty(secret);
        Assert.Contains("Master Password", error);
        Assert.False(service.TryUnprotectVaultKey(token, fingerprint, now, out var key, out _));
        Assert.Empty(key);
    }

    [Fact]
    public void Token_creation_rejects_invalid_key_fingerprint_and_lifetime()
    {
        var service = new TrustedUnlockTokenService();
        var now = DateTimeOffset.UtcNow;
        Assert.Throws<ArgumentException>(() => service.CreateToken(new byte[31], Secret, new byte[32], now, now.AddDays(1)));
        Assert.Throws<ArgumentException>(() => service.CreateToken(new byte[32], Secret, new byte[31], now, now.AddDays(1)));
        Assert.Throws<ArgumentException>(() => service.CreateToken(new byte[32], Secret, new byte[32], now, now));
        Assert.Throws<ArgumentException>(() => service.CreateToken(new byte[32], Secret, new byte[32], now, now.AddDays(1).AddTicks(1)));
        Assert.Throws<ArgumentException>(() => service.CreateToken(new byte[32], new string('A', 257), new byte[32], now, now.AddDays(1)));
    }
}
