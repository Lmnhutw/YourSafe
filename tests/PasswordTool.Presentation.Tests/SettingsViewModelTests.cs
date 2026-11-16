using PasswordTool.Core.Services;

namespace PasswordTool.Presentation.Tests;

public sealed class SettingsViewModelTests : IDisposable
{
    private const string Password = "a strong master password";
    private const string Replacement = "a replacement master password";
    private readonly string directory = Path.Combine(Path.GetTempPath(), "PasswordTool.Presentation.Tests", Guid.NewGuid().ToString("N"));
    private readonly TotpService totp = new();
    private readonly VaultService service;
    private readonly VaultOperationRunner runner = new();
    private readonly SettingsViewModel model;

    public SettingsViewModelTests()
    {
        var secret = totp.GenerateSecret();
        service = new VaultService(new VaultStorageService(directory), new EncryptionService(), totp);
        service.InitializeNewVault(Password, secret, totp.GetCurrentCode(secret).Code, RecoveryKeyService.Generate(), true);
        model = new SettingsViewModel(new AppFlowCoordinator(service, runner, totp), new UserErrorMapper());
    }

    [Fact]
    public async Task Settings_password_failure_is_field_local_and_corrected_retry_saves()
    {
        model.VaultDurationMinutes = 30;
        var original = File.ReadAllText(service.ConfigPath);

        Assert.False(await model.SaveAsync("incorrect password"));
        Assert.Equal(original, File.ReadAllText(service.ConfigPath));
        Assert.NotEmpty(model.SecuritySettingsPasswordError);
        Assert.False(model.IsStatusOpen);
        Assert.False(model.IsBusy);

        Assert.True(await model.SaveAsync(Password));
        Assert.Empty(model.SecuritySettingsPasswordError);
        Assert.Equal(30, service.SecuritySettings.VaultOpenDurationMinutes);
    }

    [Fact]
    public async Task Password_change_maps_each_invalid_input_before_mutation()
    {
        var original = File.ReadAllText(service.ConfigPath);
        Assert.False(await model.ChangeMasterPasswordAsync(Password, "short", "short"));
        Assert.NotEmpty(model.NewMasterPasswordError);
        Assert.Empty(model.CurrentMasterPasswordError);
        Assert.False(await model.ChangeMasterPasswordAsync(Password, Replacement, "mismatch"));
        Assert.NotEmpty(model.ConfirmMasterPasswordError);
        Assert.Empty(model.NewMasterPasswordError);
        Assert.False(await model.ChangeMasterPasswordAsync("incorrect password", Replacement, Replacement));
        Assert.NotEmpty(model.CurrentMasterPasswordError);
        Assert.Empty(model.ConfirmMasterPasswordError);
        Assert.Equal(original, File.ReadAllText(service.ConfigPath));

        Assert.True(await model.ChangeMasterPasswordAsync(Password, Replacement, Replacement));
        Assert.Empty(model.CurrentMasterPasswordError);
        Assert.True(service.ValidateMasterPassword(Replacement));
    }

    [Fact]
    public async Task Authenticator_rejects_password_and_code_separately_then_accepts_retry()
    {
        var setup = model.PrepareAuthenticator();
        var code = totp.GetCurrentCode(setup.SecretBase32).Code;
        var wrongCode = Enumerable.Range(0, 10).Select(i => i.ToString("D6"))
            .First(value => !totp.VerifyCode(setup.SecretBase32, value));
        var original = File.ReadAllText(service.ConfigPath);

        Assert.False(await model.ResetAuthenticatorAsync("incorrect password", setup, code));
        Assert.NotEmpty(model.AuthenticatorMasterPasswordError);
        Assert.Empty(model.AuthenticatorCodeError);
        Assert.False(await model.ResetAuthenticatorAsync(Password, setup, wrongCode));
        Assert.Empty(model.AuthenticatorMasterPasswordError);
        Assert.NotEmpty(model.AuthenticatorCodeError);
        Assert.Equal(original, File.ReadAllText(service.ConfigPath));

        Assert.True(await model.ResetAuthenticatorAsync(Password, setup, totp.GetCurrentCode(setup.SecretBase32).Code));
        Assert.Empty(model.AuthenticatorCodeError);
        Assert.NotEqual(original, File.ReadAllText(service.ConfigPath));
    }

    public void Dispose()
    {
        service.Dispose();
        runner.Dispose();
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
