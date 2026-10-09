using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using PasswordTool.Core.Models;
using PasswordTool.Core.Utilities;

namespace PasswordTool.Core.Services;

public sealed class MasterPasswordService
{
    public const int SaltSizeBytes = 32;
    public const int DefaultKeySizeBytes = 32;
    public const int DefaultPbkdf2Iterations = 600_000;
    public const int DefaultArgon2Iterations = 3;
    public const int DefaultArgon2MemorySizeKb = 65_536;
    public const int DefaultArgon2Parallelism = 2;
    public const string Pbkdf2Algorithm = "PBKDF2-HMACSHA256";
    public const string Argon2idAlgorithm = "ARGON2ID";
    public const string MasterKeyContext = "PasswordTool/v3/master-dek";
    public const string VaultContext = "PasswordTool/v3/vault";
    public const string TotpSecretContext = "PasswordTool/v3/totp-secret";

    private readonly EncryptionService encryptionService;
    public MasterPasswordService() : this(new EncryptionService()) { }
    public MasterPasswordService(EncryptionService encryptionService) => this.encryptionService = encryptionService;

    // Retained to read and construct the legacy v2 format during migration tests.
    public AppConfig CreateConfig(string masterPassword, string totpSecretBase32)
    {
        ValidateNewMasterPassword(masterPassword);
        var now = DateTimeOffset.UtcNow;
        var config = CreateLegacyKdfConfig(now);
        var key = DeriveKey(masterPassword, config);
        try
        {
            config.EncryptedTotpSecret = string.IsNullOrWhiteSpace(totpSecretBase32)
                ? string.Empty : encryptionService.EncryptString(totpSecretBase32, key);
            return config;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public AppConfig CreateEnvelopeConfig(string masterPassword, string totpSecretBase32, byte[] vaultKey)
    {
        ValidateNewMasterPassword(masterPassword);
        ArgumentNullException.ThrowIfNull(vaultKey);
        if (vaultKey.Length != DefaultKeySizeBytes) throw new ArgumentException("A 256-bit vault key is required.", nameof(vaultKey));

        var now = DateTimeOffset.UtcNow;
        var slot = CreateMasterKeySlot();
        var config = new AppConfig
        {
            Version = 3,
            MasterKeySlot = slot,
            KdfAlgorithm = slot.KdfAlgorithm,
            KdfIterations = slot.KdfIterations,
            KdfMemorySizeKb = slot.KdfMemorySizeKb,
            KdfParallelism = slot.KdfParallelism,
            KeySizeBytes = slot.KeySizeBytes,
            SaltBase64 = slot.SaltBase64,
            CreatedAt = now,
            UpdatedAt = now
        };

        var kek = DeriveKey(masterPassword, slot);
        try
        {
            slot.WrappedVaultKey = encryptionService.EncryptKey(vaultKey, kek, MasterKeyContext);
            config.EncryptedTotpSecret = string.IsNullOrWhiteSpace(totpSecretBase32)
                ? string.Empty : encryptionService.EncryptString(totpSecretBase32, vaultKey, TotpSecretContext);
            return config;
        }
        finally { CryptographicOperations.ZeroMemory(kek); }
    }

    public void RewrapVaultKey(string newMasterPassword, AppConfig config, byte[] vaultKey)
    {
        ValidateNewMasterPassword(newMasterPassword);
        var slot = CreateMasterKeySlot();
        var kek = DeriveKey(newMasterPassword, slot);
        try
        {
            slot.WrappedVaultKey = encryptionService.EncryptKey(vaultKey, kek, MasterKeyContext);
            config.Version = Math.Max(3, config.Version);
            config.MasterKeySlot = slot;
            config.KdfAlgorithm = slot.KdfAlgorithm;
            config.KdfIterations = slot.KdfIterations;
            config.KdfMemorySizeKb = slot.KdfMemorySizeKb;
            config.KdfParallelism = slot.KdfParallelism;
            config.KeySizeBytes = slot.KeySizeBytes;
            config.SaltBase64 = slot.SaltBase64;
        }
        finally { CryptographicOperations.ZeroMemory(kek); }
    }

    public byte[] DeriveKey(string masterPassword, AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.Version >= 3 && config.MasterKeySlot is not null
            ? DeriveKey(masterPassword, config.MasterKeySlot)
            : DeriveKey(masterPassword, config.KdfAlgorithm, config.KdfIterations, config.KdfMemorySizeKb,
                config.KdfParallelism, config.KeySizeBytes, config.SaltBase64);
    }

    public byte[] DeriveKey(string masterPassword, MasterKeySlot slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        if (slot.Version != 1 || !string.Equals(slot.WrapAlgorithm, "AES-256-GCM", StringComparison.Ordinal))
            throw new NotSupportedException("The Master Password key slot is not supported.");
        return DeriveKey(masterPassword, slot.KdfAlgorithm, slot.KdfIterations, slot.KdfMemorySizeKb,
            slot.KdfParallelism, slot.KeySizeBytes, slot.SaltBase64);
    }

    private static byte[] DeriveKey(string masterPassword, string algorithm, int iterations, int memorySizeKb,
        int parallelism, int keySizeBytes, string saltBase64)
    {
        PasswordHashLimits.ValidatePassword(masterPassword);
        if (string.IsNullOrEmpty(saltBase64) || saltBase64.Length > 128)
            throw new InvalidOperationException("The configured salt exceeds the supported size.");
        if (keySizeBytes != DefaultKeySizeBytes) throw new InvalidOperationException("The configured key size is not supported.");

        var isPbkdf2 = string.Equals(algorithm, Pbkdf2Algorithm, StringComparison.Ordinal);
        if (isPbkdf2)
        {
            if (iterations is < 100_000 or > PasswordHashLimits.MaxPbkdf2Iterations)
                throw new InvalidOperationException("The configured PBKDF2 iteration count exceeds the supported range.");
        }
        else if (!string.Equals(algorithm, Argon2idAlgorithm, StringComparison.Ordinal))
            throw new NotSupportedException("The key derivation algorithm is not supported.");
        else if (iterations < 2 || memorySizeKb < 32_768
            || !PasswordHashLimits.IsValidArgon2Cost(memorySizeKb, iterations, parallelism))
            throw new InvalidOperationException("The configured Argon2id parameters are outside the supported range.");

        var salt = Convert.FromBase64String(saltBase64);
        if (salt.Length is < SaltSizeBytes or > 64)
            throw new InvalidOperationException("The configured salt is outside the supported size range.");
        var passwordBytes = Encoding.UTF8.GetBytes(masterPassword);
        try
        {
            if (isPbkdf2)
                return Rfc2898DeriveBytes.Pbkdf2(passwordBytes, salt, iterations, HashAlgorithmName.SHA256, keySizeBytes);
            using var argon2 = new Argon2id(passwordBytes)
            {
                Salt = salt,
                Iterations = iterations,
                MemorySize = memorySizeKb,
                DegreeOfParallelism = parallelism
            };
            return argon2.GetBytes(keySizeBytes);
        }
        finally { CryptographicOperations.ZeroMemory(passwordBytes); }
    }

    public static bool NeedsKdfUpgrade(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var algorithm = config.MasterKeySlot?.KdfAlgorithm ?? config.KdfAlgorithm;
        var iterations = config.MasterKeySlot?.KdfIterations ?? config.KdfIterations;
        var memory = config.MasterKeySlot?.KdfMemorySizeKb ?? config.KdfMemorySizeKb;
        var parallelism = config.MasterKeySlot?.KdfParallelism ?? config.KdfParallelism;
        return !string.Equals(algorithm, Argon2idAlgorithm, StringComparison.Ordinal)
            || iterations < DefaultArgon2Iterations || memory < DefaultArgon2MemorySizeKb || parallelism < DefaultArgon2Parallelism;
    }

    public bool TryUnlockConfig(string masterPassword, AppConfig config, out byte[] encryptionKey, out string totpSecretBase32)
    {
        encryptionKey = [];
        totpSecretBase32 = string.Empty;
        byte[]? kek = null;
        try
        {
            if (config.Version is < 1 or > 4) throw new NotSupportedException("Unsupported vault configuration version.");
            if (config.Version >= 3)
            {
                var slot = config.MasterKeySlot ?? throw new InvalidOperationException("The Master Password key slot is missing.");
                kek = DeriveKey(masterPassword, slot);
                encryptionKey = encryptionService.DecryptKey(slot.WrappedVaultKey, kek, MasterKeyContext);
                if (encryptionKey.Length != DefaultKeySizeBytes) throw new CryptographicException("The wrapped vault key is invalid.");
                if (!string.IsNullOrWhiteSpace(config.EncryptedTotpSecret))
                    totpSecretBase32 = encryptionService.DecryptString(config.EncryptedTotpSecret, encryptionKey, TotpSecretContext);
            }
            else
            {
                encryptionKey = DeriveKey(masterPassword, config);
                if (!string.IsNullOrWhiteSpace(config.EncryptedTotpSecret))
                    totpSecretBase32 = encryptionService.DecryptString(config.EncryptedTotpSecret, encryptionKey);
            }
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or CryptographicException
            or InvalidOperationException or NotSupportedException)
        {
            if (encryptionKey.Length > 0) CryptographicOperations.ZeroMemory(encryptionKey);
            encryptionKey = [];
            totpSecretBase32 = string.Empty;
            return false;
        }
        finally { if (kek is { Length: > 0 }) CryptographicOperations.ZeroMemory(kek); }
    }

    public static void ValidateNewMasterPassword(string masterPassword)
    {
        PasswordHashLimits.ValidatePassword(masterPassword);
        if (masterPassword.Length < 12) throw new ArgumentException("Use a Master Password with at least 12 characters.", nameof(masterPassword));
    }

    private static MasterKeySlot CreateMasterKeySlot() => new()
    {
        Version = 1,
        WrapAlgorithm = "AES-256-GCM",
        KdfAlgorithm = Argon2idAlgorithm,
        KdfIterations = DefaultArgon2Iterations,
        KdfMemorySizeKb = DefaultArgon2MemorySizeKb,
        KdfParallelism = DefaultArgon2Parallelism,
        KeySizeBytes = DefaultKeySizeBytes,
        SaltBase64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(SaltSizeBytes))
    };

    private static AppConfig CreateLegacyKdfConfig(DateTimeOffset now) => new()
    {
        Version = 2,
        KdfAlgorithm = Argon2idAlgorithm,
        KdfIterations = DefaultArgon2Iterations,
        KdfMemorySizeKb = DefaultArgon2MemorySizeKb,
        KdfParallelism = DefaultArgon2Parallelism,
        KeySizeBytes = DefaultKeySizeBytes,
        SaltBase64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(SaltSizeBytes)),
        CreatedAt = now,
        UpdatedAt = now
    };
}
