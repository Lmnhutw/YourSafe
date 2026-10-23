using PasswordTool.Core.Models;
using PasswordTool.Presentation;

namespace PasswordTool.Presentation.Tests;

public sealed class VaultItemListItemTests
{
    [Fact]
    public void FromVaultItem_ProjectsOnlyListMetadata()
    {
        var source = new VaultItem
        {
            Title = "Example",
            Username = "user",
            Password = "secret",
            TotpSecretBase32 = "totp-secret",
            RecoveryCodes = ["code-one", "code-two"],
            HasTotp = true
        };

        var projected = VaultItemListItem.FromVaultItem(source);

        Assert.Equal("Example", projected.Title);
        Assert.True(projected.HasTotp);
        Assert.True(projected.HasPassword);
        Assert.Equal(2, projected.RecoveryCodeCount);
        Assert.Equal("2 codes", projected.RecoveryCodesDisplay);
        var representation = projected.ToString();
        Assert.DoesNotContain("secret", representation, StringComparison.Ordinal);
        Assert.DoesNotContain("code-one", representation, StringComparison.Ordinal);
    }
}
