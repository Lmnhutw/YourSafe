using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PasswordTool.Core.Models;

namespace PasswordTool.Core.Services;

public sealed class VaultBackupService
{
    public const int MaxBackupJsonCharacters = 10 * 1024 * 1024;

    private const string BackupFormat = "PasswordToolBackup";
    private const string KdfAlgorithm = "PBKDF2-SHA256";
    private const int BackupVersion = 1;
    private const int KdfIterations = 600_000;
    private const int SaltSizeBytes = 16;
    private const int KeySizeBytes = 32;
    private const int MaxItems = 10_000;
    private const int MaxTitleLength = 200;
    private const int MaxUsernameLength = 500;
    private const int MaxPasswordLength = 4_096;
    private const int MaxUrlLength = 2_048;
    private const int MaxNotesLength = 100_000;
    private const int MaxRecoveryCodes = 100;
    private const int MaxRecoveryCodeLength = 128;
    private const int MaxTags = 20;
    private const int MaxTagLength = 100;
    private const int MaxTotpSecretLength = 512;
    private const int MaxPasswordHistoryEntries = 10;
    private static readonly TotpService TotpService = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        MaxDepth = 32,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly EncryptionService encryptionService;

    public VaultBackupService(EncryptionService? encryptionService = null)
    {
        this.encryptionService = encryptionService ?? new EncryptionService();
    }

    public string CreateBackup(IReadOnlyList<VaultItem> items, string passphrase, DateTimeOffset createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(items);
        ValidatePassphrase(passphrase);
        ValidateItems(items);

        var payload = new BackupPayload
        {
            CreatedAtUtc = createdAtUtc.ToUniversalTime(),
            Items = items.Select(Clone).ToList()
        };

        var salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
        var key = DeriveKey(passphrase, salt, KdfIterations);
        try
        {
            var encryptedPayloadJson = encryptionService.EncryptObject(payload, key);
            using var encryptedPayload = JsonDocument.Parse(encryptedPayloadJson);
            var envelope = new BackupEnvelope
            {
                Format = BackupFormat,
                Version = BackupVersion,
                KdfAlgorithm = KdfAlgorithm,
                KdfIterations = KdfIterations,
                SaltBase64 = Convert.ToBase64String(salt),
                EncryptedPayload = encryptedPayload.RootElement.Clone()
            };

            return JsonSerializer.Serialize(envelope, JsonOptions);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public IReadOnlyList<VaultItem> ReadBackup(string backupJson, string passphrase)
    {
        return ReadAndValidateBackup(backupJson, passphrase).Payload.Items.Select(Clone).ToList();
    }

    public VaultBackupInspection InspectBackup(string backupJson, string passphrase)
    {
        var backup = ReadAndValidateBackup(backupJson, passphrase);
        var items = backup.Payload.Items;
        return new VaultBackupInspection(
            backup.Envelope.Format,
            backup.Envelope.Version,
            backup.Payload.CreatedAtUtc == default ? null : backup.Payload.CreatedAtUtc,
            items.Count,
            items.Count(item => item.Type == VaultItemType.Password),
            items.Count(item => item.Type == VaultItemType.RecoveryCodes),
            items.Count(item => !item.IsDeleted),
            items.Count(item => item.IsDeleted));
    }

    private BackupContents ReadAndValidateBackup(string backupJson, string passphrase)
    {
        ValidatePassphrase(passphrase);
        if (string.IsNullOrWhiteSpace(backupJson) || backupJson.Length > MaxBackupJsonCharacters)
        {
            throw new InvalidDataException("The backup file is empty or exceeds the 10 MB limit.");
        }

        BackupEnvelope envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<BackupEnvelope>(backupJson, JsonOptions)
                ?? throw new InvalidDataException("The backup file is empty or invalid.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The backup file does not contain valid PasswordTool JSON.", ex);
        }

        ValidateEnvelope(envelope);
        var salt = ReadSalt(envelope.SaltBase64);
        var key = DeriveKey(passphrase, salt, envelope.KdfIterations);

        try
        {
            BackupPayload payload;
            try
            {
                payload = encryptionService.DecryptObject<BackupPayload>(envelope.EncryptedPayload.GetRawText(), key);
            }
            catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException)
            {
                throw new CryptographicException("The backup password is incorrect, or the backup file was modified.", ex);
            }

            payload.Items ??= [];
            ValidateItems(payload.Items);
            return new BackupContents(envelope, payload);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public VaultBackupImportPlan CreateImportPlan(
        IReadOnlyList<VaultItem> importedItems,
        IReadOnlyList<VaultItem> existingItems)
    {
        ArgumentNullException.ThrowIfNull(importedItems);
        ArgumentNullException.ThrowIfNull(existingItems);
        ValidateItems(importedItems);

        var existingById = existingItems.ToDictionary(item => item.Id);
        var planItems = importedItems.Select(item =>
        {
            var status = !existingById.TryGetValue(item.Id, out var existing)
                ? VaultBackupImportStatus.New
                : AreEquivalent(item, existing)
                    ? VaultBackupImportStatus.Duplicate
                    : VaultBackupImportStatus.Conflict;

            return new VaultBackupImportPlanItem(item.Id, item.Title, item.Type, status);
        }).ToList();

        return new VaultBackupImportPlan(planItems);
    }

    public static void ValidateItems(IReadOnlyList<VaultItem> items)
    {
        if (items.Count > MaxItems)
        {
            throw new InvalidDataException($"A backup may contain at most {MaxItems:N0} items.");
        }

        var ids = new HashSet<Guid>();
        foreach (var item in items)
        {
            if (item is null || item.Id == Guid.Empty || !ids.Add(item.Id))
            {
                throw new InvalidDataException("Every backup item must have a unique, non-empty ID.");
            }

            ValidateLength(item.Title, MaxTitleLength, "title", required: true);
            ValidateLength(item.Username, MaxUsernameLength, "username");
            ValidateLength(item.Url, MaxUrlLength, "URL");
            ValidateLength(item.Notes, MaxNotesLength, "notes");
            ValidateLength(item.TotpSecretBase32, MaxTotpSecretLength, "TOTP secret");
            item.Tags ??= [];
            item.PasswordHistory ??= [];
            if (item.Tags.Count > MaxTags
                || item.Tags.Any(tag => string.IsNullOrWhiteSpace(tag) || tag.Length > MaxTagLength)
                || item.Tags.Distinct(StringComparer.OrdinalIgnoreCase).Count() != item.Tags.Count)
            {
                throw new InvalidDataException($"Item '{item.Title}' contains an invalid tag list.");
            }

            if (!Enum.IsDefined(item.Type))
            {
                throw new InvalidDataException($"Item '{item.Title}' has an unsupported type.");
            }

            item.RecoveryCodes ??= [];
            if (item.Type == VaultItemType.Password)
            {
                ValidateLength(item.Password, MaxPasswordLength, "password");
                if (string.IsNullOrWhiteSpace(item.Password) && item.RecoveryCodes.Count < 2
                    && string.IsNullOrWhiteSpace(item.TotpSecretBase32))
                {
                    throw new InvalidDataException($"Item '{item.Title}' requires a password, TOTP configuration, or at least two recovery codes.");
                }
                if (item.PasswordHistory.Count > MaxPasswordHistoryEntries
                    || item.PasswordHistory.Any(entry => entry is null || string.IsNullOrEmpty(entry.Password)
                        || entry.Password.Length > MaxPasswordLength))
                {
                    throw new InvalidDataException($"Password item '{item.Title}' contains invalid password history.");
                }
                if (string.IsNullOrEmpty(item.Password)
                    && (item.PasswordHistory.Count != 0 || item.PasswordChangedAt.HasValue))
                {
                    throw new InvalidDataException($"Item '{item.Title}' contains password metadata without a password.");
                }

                if (!string.IsNullOrEmpty(item.TotpSecretBase32))
                {
                    try { _ = TotpService.NormalizeConfiguration(item.GetTotpConfiguration()!); }
                    catch (Exception ex) when (ex is ArgumentException or FormatException)
                    {
                        throw new InvalidDataException("A backup item contains an invalid TOTP configuration.");
                    }
                }
                ValidateRecoveryCodes(item);
            }
            else
            {
                if (!string.IsNullOrEmpty(item.Password)
                    || !string.IsNullOrEmpty(item.TotpSecretBase32)
                    || item.PasswordHistory.Count != 0
                    || item.PasswordChangedAt.HasValue)
                {
                    throw new InvalidDataException($"Recovery-code item '{item.Title}' contains password or TOTP data.");
                }

                ValidateRecoveryCodes(item);
            }

            if (item.IsDeleted != item.DeletedAt.HasValue)
            {
                throw new InvalidDataException($"Item '{item.Title}' has inconsistent trash metadata.");
            }
        }
    }

    private static void ValidateRecoveryCodes(VaultItem item)
    {
        if (item.RecoveryCodes.Count == 0) return;
        var parsedCodes = RecoveryCodeParser.Parse(string.Join('\n', item.RecoveryCodes));
        if (item.RecoveryCodes.Count < 2
            || item.RecoveryCodes.Count > MaxRecoveryCodes
            || item.RecoveryCodes.Any(code => code.Length > MaxRecoveryCodeLength)
            || !parsedCodes.IsValid)
        {
            throw new InvalidDataException($"Item '{item.Title}' contains an invalid code list.");
        }
    }

    private static void ValidateEnvelope(BackupEnvelope envelope)
    {
        if (!string.Equals(envelope.Format, BackupFormat, StringComparison.Ordinal)
            || envelope.Version != BackupVersion
            || !string.Equals(envelope.KdfAlgorithm, KdfAlgorithm, StringComparison.Ordinal)
            || envelope.KdfIterations != KdfIterations
            || envelope.EncryptedPayload.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The backup format or version is not supported.");
        }
    }

    private static byte[] ReadSalt(string saltBase64)
    {
        try
        {
            var salt = Convert.FromBase64String(saltBase64);
            if (salt.Length != SaltSizeBytes)
            {
                throw new InvalidDataException("The backup KDF salt is malformed.");
            }

            return salt;
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("The backup KDF salt is malformed.", ex);
        }
    }

    private static byte[] DeriveKey(string passphrase, byte[] salt, int iterations)
    {
        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(passphrase),
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            KeySizeBytes);
    }

    public static void ValidatePassphrase(string passphrase)
    {
        if (string.IsNullOrWhiteSpace(passphrase) || passphrase.Length < 12)
        {
            throw new ArgumentException("Use a backup password with at least 12 characters.", nameof(passphrase));
        }
    }

    private static void ValidateLength(string? value, int maximum, string fieldName, bool required = false)
    {
        if (required && string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"Every backup item requires a {fieldName}.");
        }

        if ((value?.Length ?? 0) > maximum)
        {
            throw new InvalidDataException($"A backup item {fieldName} exceeds the {maximum:N0}-character limit.");
        }
    }

    private static bool AreEquivalent(VaultItem left, VaultItem right)
    {
        return left.Id == right.Id
            && left.Type == right.Type
            && left.Title == right.Title
            && left.Username == right.Username
            && left.Password == right.Password
            && left.PasswordHistory.Count == right.PasswordHistory.Count
            && left.PasswordHistory.Zip(right.PasswordHistory).All(pair =>
                pair.First.Password == pair.Second.Password && pair.First.ChangedAt == pair.Second.ChangedAt)
            && left.TotpSecretBase32 == right.TotpSecretBase32
            && left.GetTotpConfiguration() == right.GetTotpConfiguration()
            && left.Url == right.Url
            && left.HideUrl == right.HideUrl
            && left.Notes == right.Notes
            && left.HideNotes == right.HideNotes
            && left.IsFavorite == right.IsFavorite
            && left.GroupId == right.GroupId
            && left.IsDeleted == right.IsDeleted
            && left.DeletedAt == right.DeletedAt
            && left.CreatedAt == right.CreatedAt
            && left.UpdatedAt == right.UpdatedAt
            && left.PasswordChangedAt == right.PasswordChangedAt
            && left.RecoveryCodes.SequenceEqual(right.RecoveryCodes, StringComparer.Ordinal)
            && left.Tags.SequenceEqual(right.Tags, StringComparer.Ordinal);
    }

    private static VaultItem Clone(VaultItem item)
    {
        return new VaultItem
        {
            Id = item.Id,
            Type = item.Type,
            Title = item.Title,
            Username = item.Username,
            Password = item.Password,
            PasswordHistory = item.PasswordHistory.Select(entry => new PasswordHistoryEntry
            {
                Password = entry.Password,
                ChangedAt = entry.ChangedAt
            }).ToList(),
            TotpSecretBase32 = item.TotpSecretBase32,
            TotpIssuer = item.TotpIssuer,
            TotpAccountName = item.TotpAccountName,
            TotpAlgorithm = item.TotpAlgorithm,
            TotpDigits = item.TotpDigits,
            TotpPeriod = item.TotpPeriod,
            RecoveryCodes = [.. item.RecoveryCodes],
            Url = item.Url,
            HideUrl = item.HideUrl,
            Notes = item.Notes,
            HideNotes = item.HideNotes,
            IsFavorite = item.IsFavorite,
            GroupId = item.GroupId,
            LegacyFolder = item.LegacyFolder,
            Tags = [.. item.Tags],
            IsDeleted = item.IsDeleted,
            DeletedAt = item.DeletedAt,
            CreatedAt = item.CreatedAt,
            UpdatedAt = item.UpdatedAt,
            PasswordChangedAt = item.PasswordChangedAt
        };
    }

    private sealed class BackupEnvelope
    {
        public string Format { get; set; } = string.Empty;

        public int Version { get; set; }

        public string KdfAlgorithm { get; set; } = string.Empty;

        public int KdfIterations { get; set; }

        public string SaltBase64 { get; set; } = string.Empty;

        public JsonElement EncryptedPayload { get; set; }
    }

    private sealed class BackupPayload
    {
        public DateTimeOffset CreatedAtUtc { get; set; }

        public List<VaultItem> Items { get; set; } = [];
    }

    private sealed record BackupContents(BackupEnvelope Envelope, BackupPayload Payload);
}
