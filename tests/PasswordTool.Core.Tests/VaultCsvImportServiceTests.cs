using PasswordTool.Core.Models;
using PasswordTool.Core.Services;

namespace PasswordTool.Core.Tests;

public sealed class VaultCsvImportServiceTests
{
    [Fact]
    public void Browser_csv_supports_quoted_fields_and_common_headers()
    {
        const string csv = "name,url,username,password,note\r\n\"Example, Personal\",https://example.com,person@example.com,secret,\"line 1\nline 2\"";
        var item = Assert.Single(new VaultCsvImportService().Parse(csv));

        Assert.Equal("Example, Personal", item.Title);
        Assert.Equal("person@example.com", item.Username);
        Assert.Equal("secret", item.Password);
        Assert.Equal("line 1\nline 2", item.Notes);
    }

    [Fact]
    public void Bitwarden_csv_maps_metadata_and_otpauth_secret()
    {
        const string csv = "folder,favorite,type,name,notes,login_uri,login_username,login_password,login_totp,tags\nPersonal,1,login,Email,note,https://mail.example,user@example.com,password,otpauth://totp/Email?secret=JBSWY3DPEHPK3PXP,mail;important";
        var item = Assert.Single(new VaultCsvImportService().Parse(csv));

        Assert.Equal("Personal", item.LegacyFolder);
        Assert.True(item.IsFavorite);
        Assert.Equal("JBSWY3DPEHPK3PXP", item.TotpSecretBase32);
        Assert.Equal(["mail", "important"], item.Tags);
    }

    [Fact]
    public void Import_plan_marks_matching_accounts_as_duplicates()
    {
        var service = new VaultCsvImportService();
        var imported = service.Parse("name,url,username,password\nEmail,https://example.com,user,password");
        var existing = new[]
        {
            new VaultItem { Title = "email", Url = "https://example.com", Username = "user", Password = "password" }
        };

        var plan = service.CreateImportPlan(imported, existing);
        Assert.Equal(0, plan.NewItemCount);
        Assert.Equal(1, plan.DuplicateCount);
    }

    [Fact]
    public void Csv_parser_rejects_unterminated_quotes()
    {
        Assert.Throws<InvalidDataException>(() => new VaultCsvImportService().Parse("name,password\n\"broken,password"));
    }

    [Fact]
    public void Csv_preserves_uri_parameters_and_totp_only_credentials_and_duplicate_comparison()
    {
        var service = new VaultCsvImportService();
        var item = Assert.Single(service.Parse("title,totp\nExample,otpauth://totp/Example%3Auser?secret=JBSWY3DPEHPK3PXP&issuer=Example&algorithm=SHA256&digits=8&period=60"));
        Assert.Empty(item.Password);
        Assert.Equal(new TotpConfiguration("JBSWY3DPEHPK3PXP", "Example", "user", TotpAlgorithm.Sha256, 8, 60), item.GetTotpConfiguration());
        var same = new VaultItem { Title = item.Title };
        same.SetTotpConfiguration(item.GetTotpConfiguration());
        Assert.Equal(1, service.CreateImportPlan([item], [same]).DuplicateCount);
        same.TotpPeriod = 30;
        Assert.Equal(1, service.CreateImportPlan([item], [same]).NewItemCount);
    }
}
