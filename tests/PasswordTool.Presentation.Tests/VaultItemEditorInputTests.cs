using PasswordTool.Core.Models;
using PasswordTool.Presentation;

namespace PasswordTool.Presentation.Tests;

public sealed class VaultItemEditorInputTests
{
    [Fact]
    public void ToVaultItem_Credential_KeepsPasswordAndRecoveryCodes()
    {
        var input = CreateInput() with
        {
            Password = "secret",
            TotpSecretBase32 = "JBSWY3DPEHPK3PXP",
            RecoveryCodesText = "alpha-1234\nbeta-5678"
        };

        var item = input.ToVaultItem();

        Assert.Equal("secret", item.Password);
        Assert.Equal("JBSWY3DPEHPK3PXP", item.TotpSecretBase32);
        Assert.Equal(["alpha-1234", "beta-5678"], item.RecoveryCodes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Form_requires_password_even_with_authenticator_and_recovery_codes(string password)
    {
        var input = CreateInput() with
        {
            Password = password,
            TotpConfiguration = new TotpConfiguration("JBSWY3DPEHPK3PXP", "Issuer", "user"),
            RecoveryCodesText = "alpha-1234\nbeta-5678"
        };

        Assert.Equal([nameof(VaultItemEditorInput.Password)], input.Validate().Keys);
        var exception = Assert.Throws<ArgumentException>(() => input.ToVaultItem());
        Assert.Equal(nameof(VaultItemEditorInput.Password), exception.ParamName);
    }

    [Fact]
    public void Validation_returns_required_fields_in_focus_order_and_optional_recovery_errors()
    {
        var input = CreateInput() with { Title = " ", Username = "", Password = "\t", RecoveryCodesText = "only-one-code" };
        Assert.Equal(["Title", "Username", "Password", "RecoveryCodesText"], input.Validate().Keys);
        Assert.Equal(nameof(VaultItemEditorInput.Title), Assert.Throws<ArgumentException>(() => input.ToVaultItem()).ParamName);
        Assert.Empty(CreateInput().Validate());
        Assert.Empty(CreateInput().ToVaultItem().RecoveryCodes);
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
        "password",
        string.Empty,
        string.Empty,
        "https://example.com",
        string.Empty,
        null,
        false,
        false,
        false);
}
