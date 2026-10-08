using System.Text.Json.Serialization;

namespace PasswordTool.Core.Models;

public sealed class VaultItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Title { get; set; } = string.Empty;

    public VaultItemType Type { get; set; } = VaultItemType.Password;

    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public List<PasswordHistoryEntry> PasswordHistory { get; set; } = [];

    public string TotpSecretBase32 { get; set; } = string.Empty;

    public string TotpIssuer { get; set; } = string.Empty;

    public string TotpAccountName { get; set; } = string.Empty;

    public TotpAlgorithm TotpAlgorithm { get; set; } = TotpAlgorithm.Sha1;

    public int TotpDigits { get; set; } = 6;

    public int TotpPeriod { get; set; } = 30;

    public TotpConfiguration? GetTotpConfiguration() => string.IsNullOrWhiteSpace(TotpSecretBase32)
        ? null
        : new(TotpSecretBase32, TotpIssuer, TotpAccountName, TotpAlgorithm, TotpDigits, TotpPeriod);

    public void SetTotpConfiguration(TotpConfiguration? configuration)
    {
        TotpSecretBase32 = configuration?.Secret ?? string.Empty;
        TotpIssuer = configuration?.Issuer ?? string.Empty;
        TotpAccountName = configuration?.AccountName ?? string.Empty;
        TotpAlgorithm = configuration?.Algorithm ?? TotpAlgorithm.Sha1;
        TotpDigits = configuration?.Digits ?? 6;
        TotpPeriod = configuration?.Period ?? 30;
    }

    public List<string> RecoveryCodes { get; set; } = [];

    [JsonIgnore]
    public int RecoveryCodeCount { get; set; }

    [JsonIgnore]
    public bool HasPassword { get; set; }

    [JsonIgnore]
    public bool HasTotp { get; set; }

    public string Url { get; set; } = string.Empty;

    public bool HideUrl { get; set; }

    public string Notes { get; set; } = string.Empty;

    public bool HideNotes { get; set; }

    public bool IsFavorite { get; set; }

    public Guid? GroupId { get; set; }

    // Read only while migrating vaults/imports created before Groups existed.
    [JsonPropertyName("Folder")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? LegacyFolder { get; set; }

    public List<string> Tags { get; set; } = [];

    public bool IsDeleted { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When the current password became active; null is supported for legacy vaults.</summary>
    public DateTimeOffset? PasswordChangedAt { get; set; }
}
