using PasswordTool.Core.Models;

namespace PasswordTool.Presentation;

/// <summary>Secret-free representation used by vault list and search UI.</summary>
public sealed record VaultItemListItem(
    Guid Id,
    string Title,
    string Username,
    VaultItemType Type,
    Guid? GroupId,
    string Url,
    string Notes,
    bool HideNotes,
    bool HasTotp,
    bool HasPassword,
    int RecoveryCodeCount,
    bool IsFavorite,
    DateTimeOffset UpdatedAt)
{
    public bool IsLastInGroup { get; init; }
    public string RecoveryCodesDisplay => RecoveryCodeCount == 0 ? string.Empty : $"{RecoveryCodeCount} codes";
    public bool HasRecoveryCodes => RecoveryCodeCount > 0;
    public bool HasNotes => HideNotes || !string.IsNullOrWhiteSpace(Notes);
    public string UpdatedDisplay => UpdatedAt.ToLocalTime().ToString("g");

    public static VaultItemListItem FromVaultItem(VaultItem item) => new(
        item.Id,
        item.Title,
        item.Username,
        item.Type,
        item.GroupId,
        item.HideUrl ? string.Empty : item.Url,
        item.HideNotes ? string.Empty : item.Notes,
        item.HideNotes,
        item.HasTotp,
        item.HasPassword || !string.IsNullOrWhiteSpace(item.Password),
        item.RecoveryCodes is { Count: > 0 } ? item.RecoveryCodes.Count : item.RecoveryCodeCount,
        item.IsFavorite,
        item.UpdatedAt);
}
