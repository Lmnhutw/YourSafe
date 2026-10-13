using PasswordTool.Core.Models;
using PasswordTool.Core.Services;

namespace PasswordTool.Presentation.Tests;

public sealed class BackupViewModelTests : IDisposable
{
    private const string Password = "a separate backup password";
    private readonly string directory = Path.Combine(Path.GetTempPath(), "PasswordTool.Presentation.Tests", Guid.NewGuid().ToString("N"));
    private readonly VaultService service;
    private readonly VaultOperationRunner runner = new();
    private readonly Picker picker = new();
    private readonly Dialogs dialogs = new();
    private readonly AppFlowCoordinator flow;
    private readonly VaultWorkspaceViewModel workspace;
    private readonly BackupViewModel model;

    public BackupViewModelTests()
    {
        var totp = new TotpService();
        var secret = totp.GenerateSecret();
        service = new VaultService(new VaultStorageService(directory), new EncryptionService(), totp);
        service.InitializeNewVault("a strong master password", secret, totp.GetCurrentCode(secret).Code, RecoveryKeyService.Generate(), true);
        flow = new AppFlowCoordinator(service, runner, totp);
        workspace = new VaultWorkspaceViewModel(flow);
        model = new BackupViewModel(flow, picker, dialogs, new UserErrorMapper(), workspace);
    }

    [Theory]
    [InlineData("short", "short")]
    [InlineData(Password, "different password")]
    [InlineData("            ", "            ")]
    public async Task Validation_keeps_input_and_does_not_open_picker(string password, string confirmation)
    {
        model.BackupPassword = password;
        model.ConfirmBackupPassword = confirmation;
        await model.ExportAsync();
        Assert.Equal(0, picker.SaveCalls);
        Assert.Equal(password, model.BackupPassword);
        Assert.Equal(confirmation, model.ConfirmBackupPassword);
        Assert.True(model.IsStatusOpen);
        Assert.False(model.IsBusy);
    }

    [Fact]
    public async Task Picker_cancellation_keeps_input_and_blocks_duplicate_submissions()
    {
        model.BackupPassword = model.ConfirmBackupPassword = Password;
        picker.Pending = new TaskCompletionSource<string?>();
        var pending = model.ExportAsync();
        Assert.True(model.IsBusy);
        await model.ExportAsync();
        await model.VerifyAsync();
        await model.PreviewImportAsync();
        Assert.Equal(1, picker.SaveCalls);
        Assert.Equal(0, picker.OpenCalls);
        picker.Pending.SetResult(null);
        await pending;
        Assert.Equal(Password, model.BackupPassword);
        Assert.Equal(Password, model.ConfirmBackupPassword);
        Assert.False(model.IsBusy);
        Assert.Null(service.LastExternalBackupAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Creation_clears_both_fields_after_success_or_processing_failure(bool fail)
    {
        picker.Path = fail ? Path.Combine(directory, "missing", "backup.json") : Path.Combine(directory, "backup.json");
        model.BackupPassword = model.ConfirmBackupPassword = Password;
        await model.ExportAsync();
        Assert.Equal(string.Empty, model.BackupPassword);
        Assert.Equal(string.Empty, model.ConfirmBackupPassword);
        Assert.False(model.IsBusy);
        Assert.Equal(!fail, File.Exists(picker.Path));
        if (!fail) Assert.Equal("Backup created and verified.", model.StatusMessage);
        else Assert.DoesNotContain("created and verified", model.StatusMessage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Verification_clears_password_on_success_and_failure(bool fail)
    {
        picker.Path = Path.Combine(directory, "backup.json");
        File.WriteAllText(picker.Path, new VaultBackupService().CreateBackup([], Password, DateTimeOffset.UtcNow));
        model.BackupPassword = fail ? "incorrect backup password" : Password;
        model.ConfirmBackupPassword = Password;
        await model.VerifyAsync();
        Assert.Equal(string.Empty, model.BackupPassword);
        Assert.Equal(string.Empty, model.ConfirmBackupPassword);
        Assert.False(model.CanImportBackup);
        Assert.False(model.IsBusy);
        Assert.Equal(!fail, service.LastVerifiedBackupAt.HasValue);
    }

    [Fact]
    public async Task Preview_keeps_password_for_import_and_password_changes_invalidate_preview()
    {
        picker.Path = Path.Combine(directory, "backup.json");
        File.WriteAllText(picker.Path, new VaultBackupService().CreateBackup(
            [new VaultItem { Title = "Imported", Password = "saved password" }], Password, DateTimeOffset.UtcNow));
        model.BackupPassword = Password;
        await model.PreviewImportAsync();
        Assert.True(model.CanImportBackup);
        Assert.Equal(Password, model.BackupPassword);
        await model.ImportBackupAsync();
        Assert.Single(service.GetItems());
        Assert.False(model.CanImportBackup);
        model.CanImportBackup = true;
        model.BackupPassword = string.Empty;
        Assert.False(model.CanImportBackup);
    }

    [Fact]
    public async Task Picker_from_previous_unlock_cannot_export_or_clear_a_new_action()
    {
        model.BackupPassword = model.ConfirmBackupPassword = Password;
        var previousPicker = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        picker.Pending = previousPicker;
        var previousExport = model.ExportAsync();
        await flow.LockAsync();
        model.Clear();
        Assert.True((await flow.UnlockAsync("a strong master password", string.Empty)).Success);
        model.BackupPassword = model.ConfirmBackupPassword = Password;
        var currentPicker = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        picker.Pending = currentPicker;
        var currentExport = model.ExportAsync();
        var previousPath = Path.Combine(directory, "previous-unlock.json");
        previousPicker.SetResult(previousPath);
        await previousExport;
        Assert.False(File.Exists(previousPath));
        Assert.True(model.IsBusy);
        Assert.Equal(Password, model.BackupPassword);
        Assert.False(model.IsStatusOpen);
        var currentPath = Path.Combine(directory, "current-unlock.json");
        currentPicker.SetResult(currentPath);
        await currentExport;
        Assert.True(File.Exists(currentPath));
        Assert.False(model.IsBusy);
        Assert.Equal("Backup created and verified.", model.StatusMessage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task First_item_offers_backup_once_unless_backup_already_exists(bool hasBackup)
    {
        if (hasBackup) service.CreateExternalBackupFile(Path.Combine(directory, "backup.json"), Password, string.Empty);
        var shell = new ShellViewModel(flow, workspace, null!, null!, model, null!, null!,
            new NavigationService(), null!, picker, new UserErrorMapper(), null!);
        Assert.False(shell.IsBackupReminderOpen);
        var input = new VaultItemEditorInput(null, "First item", "", "saved password", "", "", "", "", null, "", false, false, false);
        Assert.True(await shell.SaveItemAsync(input));
        Assert.Equal(!hasBackup, shell.IsBackupReminderOpen);
        shell.IsBackupReminderOpen = false; // Later
        Assert.True(await shell.SaveItemAsync(input with { Title = "Second item" }));
        Assert.False(shell.IsBackupReminderOpen);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Snapshot_restore_requires_current_authorization_and_passes_historical_password_to_core(bool provideHistoricalPassword)
    {
        const string original = "a strong master password";
        const string replacement = "a replacement strong master password";
        service.AddItem(new VaultItem { Title = "Historical item", Password = "synthetic password" });
        Assert.True(service.TryChangeMasterPassword(original, replacement, out var error), error);
        model.SelectedSnapshot = service.GetSnapshots().First();
        var config = File.ReadAllText(service.ConfigPath);
        Assert.Equal(provideHistoricalPassword, await model.RestoreSnapshotAsync(replacement, provideHistoricalPassword ? original : null));
        if (provideHistoricalPassword)
        {
            Assert.Equal(AppFlowState.Unlock, flow.FlowState);
            Assert.False(service.IsSignInSessionActive);
            Assert.NotEqual(config, File.ReadAllText(service.ConfigPath));
        }
        else
        {
            Assert.Equal(config, File.ReadAllText(service.ConfigPath));
            Assert.Equal(AppFlowState.Unlocked, flow.FlowState);
            Assert.True(model.IsStatusOpen);
            Assert.Contains("snapshot's Master Password", model.StatusMessage);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancelled_or_stale_snapshot_confirmation_cannot_replace_live_state(bool stale)
    {
        service.AddItem(new VaultItem { Title = "Snapshot item", Password = "synthetic password" });
        service.AddItem(new VaultItem { Title = "Live item", Password = "synthetic password" });
        model.SelectedSnapshot = service.GetSnapshots().First();
        var config = File.ReadAllText(service.ConfigPath);
        var payload = File.ReadAllText(service.VaultPath);
        dialogs.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var restore = model.RestoreSnapshotAsync("a strong master password");
        if (stale)
        {
            await flow.LockAsync();
            model.Clear();
            Assert.True((await flow.UnlockAsync("a strong master password", "")).Success);
        }
        dialogs.Pending.SetResult(stale);
        Assert.False(await restore);
        Assert.Equal(config, File.ReadAllText(service.ConfigPath));
        Assert.Equal(payload, File.ReadAllText(service.VaultPath));
        Assert.Equal(2, service.GetItems().Count);
    }

    public void Dispose()
    {
        service.Dispose();
        runner.Dispose();
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private sealed class Dialogs : IUserDialogService
    {
        public TaskCompletionSource<bool>? Pending { get; set; }
        public Task<bool> ConfirmAsync(string title, string message, string confirmText, CancellationToken cancellationToken = default) =>
            Pending?.Task ?? Task.FromResult(true);
        public Task<(string Confirmation, string TotpCode)?> ConfirmGroupDeletionAsync(string groupName, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task ShowErrorAsync(string title, string message, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<string?> PromptTotpAsync(string title, string message, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<string?> PromptBackupPassphraseAsync(string title, string message, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task ShowSecretAsync(string title, string value, bool multiline, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    private sealed class Picker : IFilePickerService
    {
        public string? Path { get; set; }
        public TaskCompletionSource<string?>? Pending { get; set; }
        public int SaveCalls { get; private set; }
        public int OpenCalls { get; private set; }
        public Task<string?> PickSavePathAsync(string suggestedFileName, CancellationToken cancellationToken = default)
        {
            SaveCalls++;
            Assert.EndsWith(".json", suggestedFileName);
            return Pending?.Task ?? Task.FromResult(Path);
        }
        public Task<string?> PickOpenPathAsync(CancellationToken cancellationToken = default)
        {
            OpenCalls++;
            return Task.FromResult(Path);
        }
    }
}
