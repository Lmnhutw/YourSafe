using PasswordTool.Core.Models;
using PasswordTool.Core.Services;

namespace PasswordTool.Presentation;

public sealed record VaultItemEditorInput(
    Guid? Id,
    string Title,
    string Username,
    string Password,
    string RecoveryCodesText,
    string TotpSecretBase32,
    string Url,
    string Notes,
    Guid? GroupId,
    string TagsText,
    bool IsFavorite,
    bool HideUrl,
    bool HideNotes,
    TotpConfiguration? TotpConfiguration = null)
{
    public override string ToString() => nameof(VaultItemEditorInput);

    public VaultItem ToVaultItem()
    {
        var recoveryCodes = new List<string>();
        if (!string.IsNullOrWhiteSpace(RecoveryCodesText))
        {
            var parsed = RecoveryCodeParser.Parse(RecoveryCodesText);
            if (!parsed.IsValid) throw new ArgumentException(parsed.ErrorMessage, nameof(RecoveryCodesText));
            recoveryCodes = [.. parsed.Codes];
        }

        var tags = TagsText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var item = new VaultItem
        {
            Id = Id ?? Guid.NewGuid(),
            Type = VaultItemType.Password,
            Title = Title,
            Username = Username,
            Password = Password,
            RecoveryCodes = recoveryCodes,
            TotpSecretBase32 = TotpSecretBase32,
            Url = Url,
            Notes = Notes,
            GroupId = GroupId,
            Tags = tags,
            IsFavorite = IsFavorite,
            HideUrl = HideUrl,
            HideNotes = HideNotes
        };
        if (TotpConfiguration is not null) item.SetTotpConfiguration(TotpConfiguration);
        return item;
    }
}
