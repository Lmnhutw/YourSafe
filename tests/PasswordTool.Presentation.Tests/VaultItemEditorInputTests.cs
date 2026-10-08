using PasswordTool.Core.Models;
using PasswordTool.Presentation;

namespace PasswordTool.Presentation.Tests;

public sealed class VaultItemEditorInputTests
{
    [Fact]
    public void ToVaultItem_Credential_KeepsPasswordAndRecoveryCodesAndNormalizesTags()
    {
        var input = CreateInput() with
        {
            Password = "secret",
            TotpSecretBase32 = "JBSWY3DPEHPK3PXP",
            RecoveryCodesText = "alpha-1234\nbeta-5678",
            TagsText = "Work, work, Admin"
        };

        var item = input.ToVaultItem();

        Assert.Equal("secret", item.Password);
        Assert.Equal("JBSWY3DPEHPK3PXP", item.TotpSecretBase32);
        Assert.Equal(["alpha-1234", "beta-5678"], item.RecoveryCodes);
        Assert.Equal(["Work", "Admin"], item.Tags);
    }

    [Fact]
    public void ToVaultItem_RecoveryCodesCanExistWithoutPassword()
    {
        var input = CreateInput() with
        {
            Password = string.Empty,
            TotpSecretBase32 = string.Empty,
            RecoveryCodesText = "alpha-1234\nbeta-5678"
        };

        var item = input.ToVaultItem();

        Assert.Equal(VaultItemType.Password, item.Type);
        Assert.Empty(item.Password);
        Assert.Empty(item.TotpSecretBase32);
        Assert.Equal(["alpha-1234", "beta-5678"], item.RecoveryCodes);
    }

    [Fact]
    public void Full_totp_configuration_is_a_value_draft_and_preserves_credential_fields()
    {
        var configuration = new TotpConfiguration("JBSWY3DPEHPK3PXP", "Issuer", "Account", TotpAlgorithm.Sha512, 8, 60);
        var input = CreateInput() with { Password = "password", RecoveryCodesText = "alpha-1234\nbeta-5678", TotpConfiguration = configuration };
        var same = input with { TotpConfiguration = configuration with { } };
        Assert.Equal(input, same);
        Assert.NotEqual(input, input with { TotpConfiguration = configuration with { Period = 30 } });
        Assert.Equal(configuration, input.ToVaultItem().GetTotpConfiguration());
        var removed = input with { TotpConfiguration = null };
        Assert.Null(removed.ToVaultItem().GetTotpConfiguration());
        Assert.Equal("password", removed.ToVaultItem().Password);
        Assert.Equal(["alpha-1234", "beta-5678"], removed.ToVaultItem().RecoveryCodes);
        Assert.DoesNotContain(configuration.Secret, input.ToString());
    }

    private static VaultItemEditorInput CreateInput() => new(
        null,
        "Example",
        "user",
        string.Empty,
        string.Empty,
        string.Empty,
        "https://example.com",
        string.Empty,
        null,
        string.Empty,
        false,
        false,
        false);
}
