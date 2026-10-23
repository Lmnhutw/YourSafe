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
    bool IsFavorite,
    bool HideUrl,
    bool HideNotes,
    TotpConfiguration? TotpConfiguration = null)
{
    public override string ToString() => nameof(VaultItemEditorInput);

    public IReadOnlyDictionary<string, string> Validate()
    {
        var errors = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(Title)) errors.Add(nameof(Title), "Enter a title.");
        if (string.IsNullOrWhiteSpace(Username)) errors.Add(nameof(Username), "Enter a username.");
        if (string.IsNullOrWhiteSpace(Password)) errors.Add(nameof(Password), "Enter a password.");
        if (!string.IsNullOrWhiteSpace(RecoveryCodesText))
        {
            var parsed = RecoveryCodeParser.Parse(RecoveryCodesText);
            if (!parsed.IsValid) errors.Add(nameof(RecoveryCodesText), parsed.ErrorMessage ?? "Enter valid recovery codes.");
        }
        return errors;
    }

    public VaultItem ToVaultItem()
    {
        var errors = Validate();
        if (errors.Count > 0)
        {
            var error = errors.First();
            throw new ArgumentException(error.Value, error.Key);
        }
        var recoveryCodes = new List<string>();
        if (!string.IsNullOrWhiteSpace(RecoveryCodesText))
        {
            var parsed = RecoveryCodeParser.Parse(RecoveryCodesText);
            recoveryCodes = [.. parsed.Codes];
        }

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
            IsFavorite = IsFavorite,
            HideUrl = HideUrl,
            HideNotes = HideNotes
        };
        if (TotpConfiguration is not null) item.SetTotpConfiguration(TotpConfiguration);
        return item;
    }
}
