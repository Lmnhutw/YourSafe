using System.Security.Cryptography;
using PasswordTool.Core.Models;
using PasswordTool.Core.Services;

namespace PasswordTool.Core.Tests;

public sealed class VaultKdfBoundarySecurityTests
{
    [Theory]
    [InlineData("pbkdf2-cost")]
    [InlineData("argon-memory")]
    [InlineData("argon-total-work")]
    [InlineData("argon-parallelism")]
    [InlineData("key-size")]
    [InlineData("salt-size")]
    [InlineData("decoded-salt-size")]
    [InlineData("password-size")]
    public void Unauthenticated_config_cannot_choose_unbounded_kdf_inputs(string mutation)
    {
        var config = new AppConfig
        {
            Version = 2,
            SaltBase64 = Convert.ToBase64String(new byte[32])
        };
        var password = "test-only master password";
        switch (mutation)
        {
            case "pbkdf2-cost": config.KdfAlgorithm = MasterPasswordService.Pbkdf2Algorithm; config.KdfIterations = int.MaxValue; break;
            case "argon-memory": config.KdfMemorySizeKb = int.MaxValue; break;
            case "argon-total-work": config.KdfMemorySizeKb = 131072; config.KdfIterations = 4; break;
            case "argon-parallelism": config.KdfParallelism = 16; break;
            case "key-size": config.KeySizeBytes = int.MaxValue; break;
            case "salt-size": config.SaltBase64 = new string('A', 129); break;
            case "decoded-salt-size": config.SaltBase64 = Convert.ToBase64String(new byte[65]); break;
            case "password-size": password = new string('x', 4097); break;
        }
        Assert.False(new MasterPasswordService().TryUnlockConfig(password, config, out var key, out var secret));
        Assert.Empty(key);
        Assert.Empty(secret);

        config.Version = 4;
        config.MasterKeySlot = new MasterKeySlot
        {
            KdfAlgorithm = config.KdfAlgorithm,
            KdfIterations = config.KdfIterations,
            KdfMemorySizeKb = config.KdfMemorySizeKb,
            KdfParallelism = config.KdfParallelism,
            KeySizeBytes = config.KeySizeBytes,
            SaltBase64 = config.SaltBase64
        };
        Assert.False(new MasterPasswordService().TryUnlockConfig(password, config, out key, out secret));
        Assert.Empty(key);
        Assert.Empty(secret);
    }

    [Fact]
    public void Wrapped_key_input_is_bounded_and_payload_errors_are_cryptographic_failures()
    {
        var encryption = new EncryptionService();
        Assert.Throws<CryptographicException>(() => encryption.DecryptKey(new string('x', 4097), new byte[32], "test-purpose"));
        Assert.Throws<CryptographicException>(() => encryption.DecryptKey("{", new byte[32], "test-purpose"));
        Assert.Throws<ArgumentException>(() => encryption.EncryptKey(new byte[33], new byte[32], "test-purpose"));
        Assert.Throws<CryptographicException>(() => encryption.DecryptKey(
            "{\"Version\":2,\"Algorithm\":\"AES-256-GCM\",\"NonceBase64\":null,\"TagBase64\":\"AAAAAAAAAAAAAAAAAAAAAA==\",\"CipherTextBase64\":\"\"}",
            new byte[32], "test-purpose"));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"" + "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" + "\"")]
    public void Backup_rejects_malformed_salt_before_its_fixed_cost_kdf(string saltJson)
    {
        var json = "{\"Format\":\"PasswordToolBackup\",\"Version\":1,\"KdfAlgorithm\":\"PBKDF2-SHA256\",\"KdfIterations\":600000,\"SaltBase64\":"
            + saltJson + ",\"EncryptedPayload\":{}}";
        Assert.Throws<InvalidDataException>(() => new VaultBackupService().ReadBackup(json, "test-only backup password"));
        Assert.Throws<ArgumentException>(() => VaultBackupService.ValidatePassphrase(new string('x', 4097)));
    }

    [Fact]
    public void Backup_writer_cannot_emit_a_file_its_reader_would_reject_for_size()
    {
        var notes = new string('x', 100000);
        var items = Enumerable.Range(0, 100)
            .Select(index => new VaultItem { Title = $"Item {index}", Password = "test-only password", Notes = notes }).ToList();
        Assert.Throws<InvalidDataException>(() => new VaultBackupService().CreateBackup(items, "test-only backup password", DateTimeOffset.UtcNow));
    }
}
