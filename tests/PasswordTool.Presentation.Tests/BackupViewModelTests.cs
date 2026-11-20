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
        Assert.NotEmpty(model.BackupPasswordError + model.ConfirmBackupPasswordError);
        Assert.False(model.IsStatusOpen);
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
    public async Task Creation_keeps_fields_on_processing_failure_and_clears_after_successful_retry(bool fail)
    {
        picker.Path = fail ? Path.Combine(directory, "missing", "backup.json") : Path.Combine(directory, "backup.json");
        model.BackupPassword = model.ConfirmBackupPassword = Password;
        await model.ExportAsync();
        Assert.Equal(fail ? Password : string.Empty, model.BackupPassword);
        Assert.Equal(fail ? Password : string.Empty, model.ConfirmBackupPassword);
        Assert.False(model.IsBusy);
        Assert.Equal(!fail, File.Exists(picker.Path));
        if (!fail) Assert.Equal("Backup created and verified.", model.StatusMessage);
        else
        {
            Assert.DoesNotContain("created and verified", model.StatusMessage);
            picker.Path = Path.Combine(directory, "retry.json");
            await model.ExportAsync();
            Assert.True(File.Exists(picker.Path));
            Assert.Equal(string.Empty, model.BackupPassword);
            Assert.Equal(string.Empty, model.ConfirmBackupPassword);
            Assert.Equal("Backup created and verified.", model.StatusMessage);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Verification_keeps_rejected_password_and_allows_corrected_retry(bool fail)
    {
        picker.Path = Path.Combine(directory, "backup.json");
        File.WriteAllText(picker.Path, new VaultBackupService().CreateBackup([], Password, DateTimeOffset.UtcNow));
        model.BackupPassword = fail ? "incorrect backup password" : Password;
        model.ConfirmBackupPassword = Password;
        await model.VerifyAsync();
        Assert.Equal(fail ? "incorrect backup password" : string.Empty, model.BackupPassword);
        Assert.Equal(fail ? Password : string.Empty, model.ConfirmBackupPassword);
        Assert.False(model.CanImportBackup);
        Assert.False(model.IsBusy);
        Assert.Equal(!fail, service.LastVerifiedBackupAt.HasValue);
        if (fail)
        {
            Assert.NotEmpty(model.BackupPasswordError);
            model.BackupPassword = Password;
            Assert.Empty(model.BackupPasswordError);
            await model.VerifyAsync();
            Assert.True(service.LastVerifiedBackupAt.HasValue);
            Assert.Empty(model.BackupPassword);
            Assert.Empty(model.ConfirmBackupPassword);
        }
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
        Assert.Empty(model.BackupPassword);
        model.BackupPassword = Password;
        model.CanImportBackup = true;
        model.BackupPassword = string.Empty;
        Assert.False(model.CanImportBackup);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Selecting_an_invalid_backup_invalidates_the_previous_import_preview(bool verify)
    {
        var validPath = Path.Combine(directory, "valid-backup.json");
        File.WriteAllText(validPath, new VaultBackupService().CreateBackup(
            [new VaultItem { Title = "Previewed account", Password = "synthetic password" }], Password, DateTimeOffset.UtcNow));
        picker.Path = validPath;
        model.BackupPassword = Password;
        await model.PreviewImportAsync();
        Assert.True(model.CanImportBackup);
        var previousSummary = model.Summary;

        picker.Path = null;
        if (verify) await model.VerifyAsync();
        else await model.PreviewImportAsync();
        Assert.True(model.CanImportBackup);
        Assert.Equal(validPath, model.SelectedBackupPath);
        Assert.Equal(previousSummary, model.Summary);

        picker.Path = Path.Combine(directory, "invalid-backup.json");
        File.WriteAllText(picker.Path, "invalid backup contents");
        if (verify) await model.VerifyAsync();
        else await model.PreviewImportAsync();
        Assert.False(model.CanImportBackup);
        Assert.Empty(model.SelectedBackupPath);
        Assert.Empty(model.Summary);
        Assert.Equal(Password, model.BackupPassword);
        await model.ImportBackupAsync();
        Assert.Empty(service.GetItems());

        picker.Path = validPath;
        await model.PreviewImportAsync();
        await model.ImportBackupAsync();
        Assert.Equal("Previewed account", Assert.Single(service.GetItems()).Title);
    }

    [Fact]
    public async Task Selecting_an_invalid_csv_invalidates_the_previous_import_preview()
    {
        var validPath = Path.Combine(directory, "valid.csv");
        File.WriteAllText(validPath, "name,username,password\nPreviewed account,user,synthetic password");
        picker.Path = validPath;
        await model.PreviewCsvAsync();
        Assert.True(model.CanImportCsv);
        var previousSummary = model.Summary;

        picker.Path = null;
        await model.PreviewCsvAsync();
        Assert.True(model.CanImportCsv);
        Assert.Equal(validPath, model.SelectedCsvPath);
        Assert.Equal(previousSummary, model.Summary);

        picker.Path = Path.Combine(directory, "invalid.csv");
        File.WriteAllText(picker.Path, "name,password\n\"broken,password");
        await model.PreviewCsvAsync();
        Assert.False(model.CanImportCsv);
        Assert.Empty(model.SelectedCsvPath);
        Assert.Empty(model.Summary);
        await model.ImportCsvAsync();
        Assert.Empty(service.GetItems());

        picker.Path = validPath;
        await model.PreviewCsvAsync();
        await model.ImportCsvAsync();
        Assert.Equal("Previewed account", Assert.Single(service.GetItems()).Title);
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
        var errors = new UserErrorMapper();
        var shell = new ShellViewModel(flow, workspace, null!, new SettingsViewModel(flow, errors), model, null!, null!,
            new NavigationService(), null!, picker, errors, null!);
        Assert.False(shell.IsBackupReminderOpen);
        var input = new VaultItemEditorInput(null, "First item", "user", "saved password", "", "", "", "", null, false, false, false);
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
            Assert.Contains("snapshot's Master Password", model.SnapshotRestoreError);
            Assert.Empty(model.SnapshotMasterPasswordError);
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

    [Fact]
    public async Task Snapshot_current_password_error_is_separate_from_ambiguous_snapshot_failure()
    {
        const string original = "a strong master password";
        const string replacement = "a replacement strong master password";
        service.AddItem(new VaultItem { Title = "Historical item", Password = "synthetic password" });
        Assert.True(service.TryChangeMasterPassword(original, replacement, out var error), error);
        model.SelectedSnapshot = service.GetSnapshots().First();
        var config = File.ReadAllText(service.ConfigPath);

        Assert.False(await model.RestoreSnapshotAsync("incorrect password", original));
        Assert.NotEmpty(model.SnapshotMasterPasswordError);
        Assert.Empty(model.SnapshotRestoreError);
        Assert.Equal(config, File.ReadAllText(service.ConfigPath));
        Assert.False(await model.RestoreSnapshotAsync(replacement, "incorrect historical password"));
        Assert.Empty(model.SnapshotMasterPasswordError);
        Assert.Contains("could not be verified or restored", model.SnapshotRestoreError);
        Assert.Equal(config, File.ReadAllText(service.ConfigPath));

        Assert.True(await model.RestoreSnapshotAsync(replacement, original));
        Assert.Empty(model.SnapshotMasterPasswordError);
        Assert.Empty(model.SnapshotRestoreError);
        Assert.False(service.IsSignInSessionActive);
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
