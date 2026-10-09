using System.Security.Cryptography;
using PasswordTool.Core.Models;
using PasswordTool.Core.Services;

namespace PasswordTool.Presentation.Tests;

public sealed class RecoveryAndActionFlowTests
{
    [Theory]
    [InlineData(false, "lock")]
    [InlineData(true, "lock")]
    [InlineData(false, "tableLock")]
    [InlineData(true, "tableLock")]
    [InlineData(false, "logout")]
    [InlineData(true, "logout")]
    public async Task Queued_manual_clipboard_write_is_discarded_after_session_transition(bool username, string transition)
    {
        var clipboard = new DeferredClipboard();
        using var context = new Context(clipboard: clipboard);
        var item = context.Vault.AddItem(new VaultItem { Title = "Synthetic account", Username = "synthetic-user", Password = "synthetic-password" });
        var copying = username ? context.Shell.CopyUsernameAsync(item.Id) : context.Shell.CopyPasswordAsync(item.Id);
        await clipboard.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (transition == "tableLock") await context.Shell.LockTableAsync();
        else if (transition == "logout") await context.Shell.LogoutCommand.ExecuteAsync(null);
        else await context.Shell.LockCommand.ExecuteAsync(null);
        await context.Shell.UnlockAsync(Context.Password, context.Totp.GetCurrentCode(context.Secret).Code);
        Assert.True(context.Shell.IsUnlocked);
        clipboard.Release.SetResult();
        await copying;
        Assert.Equal(0, clipboard.Writes);
        Assert.Null(clipboard.Value);
        Assert.False(context.Shell.IsStatusOpen);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Manual_clipboard_write_from_current_session_is_cleared_on_lock(bool username)
    {
        var clipboard = new DeferredClipboard();
        using var context = new Context(clipboard: clipboard);
        var item = context.Vault.AddItem(new VaultItem { Title = "Synthetic account", Username = "synthetic-user", Password = "synthetic-password" });
        var copying = username ? context.Shell.CopyUsernameAsync(item.Id) : context.Shell.CopyPasswordAsync(item.Id);
        await clipboard.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        clipboard.Release.SetResult();
        await copying;
        Assert.Equal(username ? "synthetic-user" : "synthetic-password", clipboard.Value);
        Assert.Equal(1, clipboard.Writes);
        await context.Shell.LockCommand.ExecuteAsync(null);
        Assert.Null(clipboard.Value);
    }

    [Fact]
    public async Task Saved_vault_timeout_is_preserved_when_settings_are_reopened_after_table_lock()
    {
        using var context = new Context();
        await context.Shell.Vault.RefreshAsync();
        await context.Shell.Settings.LoadAsync();
        Assert.Equal(1, context.Shell.Settings.VaultDurationMinutes);
        context.Shell.Settings.VaultDurationMinutes = 30;
        Assert.True(await context.Shell.Settings.SaveAsync(Context.Password));
        Assert.Equal(30, context.Vault.SecuritySettings.VaultOpenDurationMinutes);
        await context.Shell.LockTableAsync();
        await context.Shell.Settings.LoadAsync();
        Assert.Equal(30, context.Shell.Settings.VaultDurationMinutes);
        Assert.Equal(0, context.Dialogs.PasswordPrompts);
        Assert.True(context.Shell.IsTableLocked);
        Assert.False(context.Vault.IsVaultUnlocked);
    }

    [Fact]
    public async Task Editing_and_saving_with_action_password_keeps_the_vault_locked()
    {
        using var context = new Context();
        var item = context.Vault.AddItem(new VaultItem { Title = "Original", Password = "synthetic-password" });
        await context.Shell.Vault.RefreshAsync();
        await context.Shell.LockTableAsync();
        context.Dialogs.ActionPassword = "wrong";
        Assert.Null(await context.Shell.GetItemForEditingAsync(item.Id));
        context.Dialogs.ActionPassword = Context.Password;
        var editable = await context.Shell.GetItemForEditingAsync(item.Id);
        Assert.NotNull(editable);
        Assert.True(context.Shell.IsTableLocked);
        Assert.False(context.Vault.IsVaultUnlocked);
        context.Shell.Navigate(AppRoute.ItemEditor);
        var input = new VaultItemEditorInput(item.Id, "Edited", editable.Username, editable.Password,
            "", "", editable.Url, editable.Notes, editable.GroupId, "", false, false, false);
        context.Dialogs.ActionPassword = "wrong";
        Assert.False(await context.Shell.SaveItemAsync(input));
        Assert.Equal(AppRoute.ItemEditor, context.Shell.CurrentRoute);
        context.Dialogs.ActionPassword = Context.Password;
        Assert.True(await context.Shell.SaveItemAsync(input));
        Assert.Equal("Edited", Assert.Single(context.Shell.Vault.Items).Title);
        Assert.Equal(AppRoute.Vault, context.Shell.CurrentRoute);
        Assert.True(context.Shell.IsTableLocked);
        Assert.False(context.Vault.IsVaultUnlocked);
        Assert.Equal(4, context.Dialogs.PasswordPrompts);
    }

    [Fact]
    public async Task Table_lock_keeps_login_and_metadata_but_requires_password_for_each_action()
    {
        using var context = new Context();
        var item = context.Vault.AddItem(new VaultItem { Title = "Test", Password = "synthetic-password", Url = "https://example.com", Notes = "synthetic-note" });
        await context.Shell.Vault.RefreshAsync();
        var loginDeadline = context.Vault.LoginExpiresAt;
        context.Now = context.Now.AddMinutes(1);
        await context.Shell.LockTableAsync();
        Assert.True(context.Shell.IsSignedIn);
        Assert.True(context.Shell.IsTableLocked);
        Assert.Single(context.Shell.Vault.Items);
        Assert.False(context.Vault.IsVaultUnlocked);
        context.Dialogs.ActionPassword = "wrong";
        await context.Shell.RevealPasswordAsync(item.Id);
        Assert.Empty(context.Dialogs.Secrets);
        context.Dialogs.ActionPassword = Context.Password;
        await context.Shell.RevealPasswordAsync(item.Id);
        await context.Shell.RevealPasswordAsync(item.Id);
        Assert.Equal(3, context.Dialogs.PasswordPrompts);
        Assert.Equal(2, context.Dialogs.Secrets.Count);
        context.Dialogs.ActionPassword = null;
        await context.Shell.RevealNotesAsync(item.Id);
        Assert.Equal(2, context.Dialogs.Secrets.Count);
        Assert.False(context.Vault.IsVaultUnlocked);
        context.Dialogs.ActionPassword = Context.Password;
        var promptsBeforeMove = context.Dialogs.PasswordPrompts;
        await context.Shell.MoveItemToGroupAsync(item.Id, null);
        Assert.Equal(promptsBeforeMove + 1, context.Dialogs.PasswordPrompts);
        Assert.False(context.Vault.IsVaultUnlocked);
        Assert.Equal(loginDeadline, context.Vault.LoginExpiresAt);
        await Assert.ThrowsAsync<OperationCanceledException>(() => context.Flow.FindAutofillCredentialsAsync("https://example.com"));
        await context.Shell.UnlockTableAsync();
        Assert.False(context.Shell.IsTableLocked);
        var prompts = context.Dialogs.PasswordPrompts;
        await context.Shell.RevealPasswordAsync(item.Id);
        Assert.Equal(prompts, context.Dialogs.PasswordPrompts);
        context.Now = loginDeadline!.Value;
        await context.Shell.LockTableAsync();
        Assert.False(context.Shell.IsSignedIn);
        Assert.Empty(context.Shell.Vault.Items);
    }

    [Fact]
    public async Task Expiring_login_while_action_password_is_pending_cannot_release_a_secret()
    {
        using var context = new Context();
        var item = context.Vault.AddItem(new VaultItem { Title = "Test", Password = "synthetic-password" });
        await context.Shell.LockTableAsync();
        var pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Flow.RequestActionPasswordAsync = _ => pending.Task;
        var reveal = context.Shell.RevealPasswordAsync(item.Id);
        context.Now = context.Vault.LoginExpiresAt!.Value;
        pending.SetResult(Context.Password);
        await reveal;
        Assert.Empty(context.Dialogs.Secrets);
        Assert.False(context.Vault.IsVaultUnlocked);
    }

    [Fact]
    public async Task Adding_an_item_to_an_unlocked_vault_does_not_prompt_again()
    {
        using var context = new Context();
        Assert.True(await context.Shell.BeginAddItemAsync());
        Assert.Equal(AppRoute.ItemEditor, context.Shell.CurrentRoute);
        Assert.Equal(0, context.Dialogs.PasswordPrompts);
        Assert.True(context.Vault.IsVaultUnlocked);
    }

    [Fact]
    public async Task Adding_an_item_unlocks_the_vault_before_opening_an_editable_editor()
    {
        using var context = new Context();
        await context.Shell.LockTableAsync();
        context.Dialogs.ActionPassword = Context.Password;
        Assert.True(await context.Shell.BeginAddItemAsync());
        Assert.Equal(1, context.Dialogs.PasswordPrompts);
        Assert.Equal(AppRoute.ItemEditor, context.Shell.CurrentRoute);
        Assert.False(context.Shell.IsTableLocked);
        Assert.True(context.Vault.IsVaultUnlocked);
        Assert.Empty(context.Vault.GetItems());
        Assert.True(await context.Shell.SaveItemAsync(new VaultItemEditorInput(null, "New account", "user", "saved password", "", "", "", "", null, "", false, false, false)));
        Assert.Single(context.Vault.GetItems());
        Assert.Equal(1, context.Dialogs.PasswordPrompts);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong")]
    public async Task Cancelled_or_incorrect_unlock_cannot_open_the_new_item_editor(string? password)
    {
        using var context = new Context();
        await context.Shell.LockTableAsync();
        context.Dialogs.ActionPassword = password;
        Assert.False(await context.Shell.BeginAddItemAsync());
        Assert.Equal(AppRoute.Vault, context.Shell.CurrentRoute);
        Assert.True(context.Shell.IsTableLocked);
        Assert.False(context.Vault.IsVaultUnlocked);
        Assert.Equal(password is not null, context.Shell.IsStatusOpen);
    }

    [Fact]
    public async Task Adding_after_vault_timeout_prompts_even_before_the_ui_timer_locks_the_table()
    {
        using var context = new Context();
        context.Now = context.Vault.VaultExpiresAt!.Value;
        context.Dialogs.ActionPassword = Context.Password;
        Assert.Equal(AppFlowState.Unlocked, context.Shell.FlowState);
        Assert.True(await context.Shell.BeginAddItemAsync());
        Assert.Equal(1, context.Dialogs.PasswordPrompts);
        Assert.True(context.Vault.IsVaultUnlocked);
        Assert.Equal(AppRoute.ItemEditor, context.Shell.CurrentRoute);
    }

    [Fact]
    public async Task Expiring_login_while_new_item_unlock_is_pending_returns_to_sign_in()
    {
        using var context = new Context();
        await context.Shell.LockTableAsync();
        context.Dialogs.PendingPassword = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var adding = context.Shell.BeginAddItemAsync();
        context.Now = context.Vault.LoginExpiresAt!.Value;
        context.Dialogs.PendingPassword.SetResult(Context.Password);
        Assert.False(await adding);
        Assert.Equal(AppFlowState.Unlock, context.Shell.FlowState);
        Assert.Equal(AppRoute.Vault, context.Shell.CurrentRoute);
        Assert.False(context.Shell.IsSignedIn);
        Assert.False(context.Vault.IsVaultUnlocked);
    }

    [Fact]
    public async Task New_item_unlock_from_an_earlier_session_is_discarded_and_overlapping_requests_do_not_prompt()
    {
        using var context = new Context();
        await context.Shell.LockTableAsync();
        context.Dialogs.PendingPassword = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var adding = context.Shell.BeginAddItemAsync();
        Assert.False(await context.Shell.BeginAddItemAsync());
        Assert.Equal(1, context.Dialogs.PasswordPrompts);
        await context.Shell.LockCommand.ExecuteAsync(null);
        await context.Shell.UnlockAsync(Context.Password, "");
        context.Dialogs.PendingPassword.SetResult(Context.Password);
        Assert.False(await adding);
        Assert.Equal(AppRoute.Vault, context.Shell.CurrentRoute);
        Assert.True(await context.Shell.BeginAddItemAsync());
        Assert.Equal(AppRoute.ItemEditor, context.Shell.CurrentRoute);
        Assert.Equal(1, context.Dialogs.PasswordPrompts);
    }

    [Fact]
    public async Task New_setup_requires_saved_recovery_key_before_creating_storage()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PasswordTool.Presentation.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var totp = new TotpService();
            using var vault = new VaultService(new VaultStorageService(directory), new EncryptionService(), totp);
            using var runner = new VaultOperationRunner();
            var flow = new AppFlowCoordinator(vault, runner, totp);
            flow.BeginNewVault();
            var setup = flow.PrepareAuthenticator(Context.Password, Context.Password);
            var code = totp.GetCurrentCode(setup.SecretBase32).Code;
            Assert.True(await flow.ValidateAuthenticatorSetupAsync(setup, code));
            var key = RecoveryKeyService.Generate();
            await Assert.ThrowsAsync<InvalidOperationException>(() => flow.CompleteNewVaultAsync(Context.Password, setup, code, key, false));
            Assert.False(File.Exists(vault.ConfigPath));
            Assert.False(File.Exists(vault.VaultPath));
            Assert.Equal(AppFlowState.SetupAuthenticator, flow.FlowState);
            flow.ReturnToFirstLaunch();
            await Assert.ThrowsAsync<InvalidOperationException>(() => flow.CompleteNewVaultAsync(Context.Password, setup, code, key, true));
            flow.BeginNewVault();
            setup = flow.PrepareAuthenticator(Context.Password, Context.Password);
            await flow.CompleteNewVaultAsync(Context.Password, setup, totp.GetCurrentCode(setup.SecretBase32).Code, key, true);
            Assert.Equal(AppFlowState.Unlocked, flow.FlowState);
            Assert.False(flow.NeedsRecoveryKey);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Recovery_requires_each_step_and_never_opens_the_workspace()
    {
        using var context = new Context();
        await context.Flow.LogoutAsync();
        var config = File.ReadAllBytes(context.Vault.ConfigPath);
        var payload = File.ReadAllBytes(context.Vault.VaultPath);
        var setup = context.Flow.CreateAuthenticatorSetup();
        var request = new RecoveryKeyResetRequest(context.RecoveryKey, Context.ReplacementPassword,
            RecoveryKeyService.Generate(), true, setup.SecretBase32, "");

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Flow.ResetWithRecoveryKeyAsync(request));
        context.Flow.BeginRecoveryKeyReset();
        Assert.Equal(AppFlowState.RecoveryKeyValidation, context.Flow.FlowState);
        Assert.Throws<InvalidOperationException>(() => context.Flow.PrepareRecoveryKeyReset(Context.ReplacementPassword, Context.ReplacementPassword));
        await Assert.ThrowsAnyAsync<CryptographicException>(() => context.Flow.ValidateRecoveryKeyAsync(RecoveryKeyService.Generate()));
        Assert.Equal(AppFlowState.RecoveryKeyValidation, context.Flow.FlowState);
        await context.Flow.ValidateRecoveryKeyAsync(context.RecoveryKey);
        Assert.Equal(AppFlowState.RecoveryMasterPassword, context.Flow.FlowState);
        await Assert.ThrowsAsync<OperationCanceledException>(() => context.Flow.GetListItemsAsync());
        Assert.False(context.Flow.IsSignedIn);
        Assert.Throws<ArgumentException>(() => context.Flow.PrepareRecoveryKeyReset(Context.ReplacementPassword, "mismatch"));
        Assert.Equal(AppFlowState.RecoveryMasterPassword, context.Flow.FlowState);
        context.Flow.PrepareRecoveryKeyReset(Context.ReplacementPassword, Context.ReplacementPassword);
        Assert.Equal(AppFlowState.RecoveryKeySave, context.Flow.FlowState);
        Assert.Throws<InvalidOperationException>(() => context.Flow.ConfirmRecoveryKeySaved(false));
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Flow.ResetWithRecoveryKeyAsync(request));
        context.Flow.ConfirmRecoveryKeySaved(true);
        Assert.Equal(AppFlowState.RecoveryAuthenticator, context.Flow.FlowState);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => context.Flow.ResetWithRecoveryKeyAsync(request));
        Assert.Equal(config, File.ReadAllBytes(context.Vault.ConfigPath));
        Assert.Equal(payload, File.ReadAllBytes(context.Vault.VaultPath));
        context.Flow.CancelRecoveryKeyReset();
        Assert.Equal(AppFlowState.Unlock, context.Flow.FlowState);
        context.Flow.BeginRecoveryKeyReset();
        await context.Flow.ValidateRecoveryKeyAsync(context.RecoveryKey);
        context.Flow.PrepareRecoveryKeyReset(Context.ReplacementPassword, Context.ReplacementPassword);
        context.Flow.ConfirmRecoveryKeySaved(true);
        await context.Flow.ResetWithRecoveryKeyAsync(request with { TotpConfirmationCode = context.Totp.GetCurrentCode(setup.SecretBase32).Code });
        Assert.Equal(AppFlowState.Unlock, context.Flow.FlowState);
        Assert.False(context.Flow.IsSignedIn);
        Assert.False(context.Flow.IsVaultSessionActive);
        Assert.Equal(payload, File.ReadAllBytes(context.Vault.VaultPath));
        Assert.False((await context.Flow.UnlockAsync(Context.ReplacementPassword, "")).Success);
        Assert.True((await context.Flow.UnlockAsync(Context.ReplacementPassword, context.Totp.GetCurrentCode(setup.SecretBase32).Code)).Success);
    }

    [Fact]
    public async Task Cancelled_recovery_validation_cannot_advance_a_new_wizard()
    {
        using var context = new Context();
        context.Vault.ClearSession();
        var runner = new HoldFirstResultRunner();
        var flow = new AppFlowCoordinator(context.Vault, runner, context.Totp);
        flow.BeginRecoveryKeyReset();
        var validation = flow.ValidateRecoveryKeyAsync(context.RecoveryKey);
        await runner.Completed.Task;
        flow.CancelRecoveryKeyReset();
        flow.BeginRecoveryKeyReset();
        runner.Release.SetResult();
        await Assert.ThrowsAsync<OperationCanceledException>(() => validation);
        Assert.Equal(AppFlowState.RecoveryKeyValidation, flow.FlowState);
        Assert.False(flow.IsSignedIn);
    }

    [Fact]
    public async Task V3_enrollment_withholds_workspace_until_key_confirmation_and_cancels_on_expiry()
    {
        using var context = new Context();
        var storage = new VaultStorageService(context.Directory);
        var config = storage.LoadConfig();
        config.Version = 3;
        config.RecoveryKeySlot = null;
        storage.SaveConfig(config);
        context.Vault.ClearSession();
        var payload = File.ReadAllBytes(context.Vault.VaultPath);
        var flow = new AppFlowCoordinator(context.Vault, context.Runner, context.Totp);
        Assert.True((await flow.UnlockAsync(Context.Password, context.Totp.GetCurrentCode(context.Secret).Code)).Success);
        Assert.Equal(AppFlowState.SaveRecoveryKey, flow.FlowState);
        Assert.True(flow.IsVaultSessionActive);
        await Assert.ThrowsAsync<OperationCanceledException>(() => flow.GetListItemsAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => flow.SaveRecoveryKeyAsync(Context.Password, RecoveryKeyService.Generate(), false));
        Assert.Equal(AppFlowState.SaveRecoveryKey, flow.FlowState);
        await flow.SaveRecoveryKeyAsync(Context.Password, RecoveryKeyService.Generate(), true);
        Assert.Equal(AppFlowState.Unlocked, flow.FlowState);
        Assert.Equal(payload, File.ReadAllBytes(context.Vault.VaultPath));

        await flow.LockAsync();
        Assert.True((await flow.UnlockAsync(Context.Password, "")).Success);
        var before = File.ReadAllBytes(context.Vault.ConfigPath);
        context.Now = context.Now.AddMinutes(1);
        await Assert.ThrowsAsync<OperationCanceledException>(() => flow.SaveRecoveryKeyAsync(Context.Password, RecoveryKeyService.Generate(), true));
        Assert.Equal(before, File.ReadAllBytes(context.Vault.ConfigPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Expiry_discards_completed_reads_and_unlocks_before_their_results_are_delivered(bool unlock)
    {
        using var context = new Context();
        if (unlock) context.Vault.LockVault();
        var runner = new HoldFirstResultRunner();
        var flow = new AppFlowCoordinator(context.Vault, runner, context.Totp);
        if (unlock)
        {
            var operation = flow.UnlockAsync(Context.Password, "");
            await runner.Completed.Task;
            context.Now = context.Now.AddHours(5);
            runner.Release.SetResult();
            Assert.False((await operation).Success);
            Assert.Equal(AppFlowState.Unlock, flow.FlowState);
        }
        else
        {
            var operation = flow.GetListItemsAsync();
            await runner.Completed.Task;
            context.Now = context.Now.AddMinutes(1);
            runner.Release.SetResult();
            await Assert.ThrowsAsync<OperationCanceledException>(() => operation);
        }
    }

    [Fact]
    public async Task Duplicate_save_uses_a_new_id_without_history_and_move_trash_restore_preserve_data()
    {
        using var context = new Context();
        var group = context.Vault.AddGroup("Destination");
        var source = new VaultItem { Title = "Source", Username = "user", Password = "first password", RecoveryCodes = ["account-code-123", "account-code-456"], Tags = ["work"], Notes = "notes", HideNotes = true };
        source.Id = context.Vault.AddItem(source).Id;
        source.Password = "second password";
        context.Vault.UpdateItem(source);
        await context.Shell.Vault.RefreshAsync();
        var editorSource = await context.Shell.GetItemForEditingAsync(source.Id);
        Assert.NotNull(editorSource);
        var input = new VaultItemEditorInput(null, editorSource.Title + " (copy)", editorSource.Username,
            editorSource.Password, string.Join('\n', editorSource.RecoveryCodes), editorSource.TotpSecretBase32,
            editorSource.Url, editorSource.Notes, editorSource.GroupId, string.Join(',', editorSource.Tags),
            editorSource.IsFavorite, editorSource.HideUrl, editorSource.HideNotes);
        Assert.Single(context.Vault.GetItems()); // Opening duplicate data does not create an item.
        Assert.DoesNotContain(editorSource.Password, input.ToString());
        Assert.True(await context.Shell.SaveItemAsync(input));
        var duplicate = context.Vault.GetItems().Single(item => item.Title == "Source (copy)");
        Assert.NotEqual(source.Id, duplicate.Id);
        Assert.Empty(context.Vault.GetPasswordHistory(duplicate.Id, ""));
        Assert.Single(context.Vault.GetPasswordHistory(source.Id, ""));
        await context.Shell.MoveItemToGroupAsync(source.Id, group.Id);
        var moved = context.Vault.GetItemForEditing(source.Id, "");
        Assert.Equal(group.Id, moved.GroupId);
        Assert.Equal("second password", moved.Password);
        Assert.Equal(["account-code-123", "account-code-456"], moved.RecoveryCodes);
        Assert.Equal("notes", moved.Notes);
        Assert.Single(moved.PasswordHistory);
        await context.Shell.ViewPasswordHistoryAsync(source.Id);
        Assert.Contains("first password", Assert.Single(context.Dialogs.Secrets));
        await context.Shell.DeleteItemAsync(source.Id);
        Assert.Contains(context.Vault.GetDeletedItems(), item => item.Id == source.Id);
        await context.Shell.Trash.LoadAsync();
        context.Shell.Trash.SelectedItem = Assert.Single(context.Shell.Trash.Items);
        await context.Shell.Trash.RestoreSelectedAsync();
        var restored = context.Vault.GetItemForEditing(source.Id, "");
        Assert.Equal(group.Id, restored.GroupId);
        Assert.Single(restored.PasswordHistory);
        Assert.False(restored.IsDeleted);
        await context.Shell.DeleteItemAsync(source.Id);
        await context.Shell.Trash.LoadAsync();
        context.Shell.Trash.SelectedItem = Assert.Single(context.Shell.Trash.Items);
        context.Dialogs.Confirmed = false;
        await context.Shell.Trash.PermanentlyDeleteSelectedAsync();
        Assert.Single(context.Vault.GetDeletedItems());
        context.Dialogs.Confirmed = true;
        await context.Shell.Trash.PermanentlyDeleteSelectedAsync();
        Assert.Empty(context.Vault.GetDeletedItems());
        Assert.Contains("Delete permanently", context.Dialogs.ConfirmationTitles);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Overlapping_item_saves_add_one_record_and_release_busy_state_after_completion_or_lock(bool lockBeforeCompletion)
    {
        var runner = new HoldFirstResultRunner();
        using var context = new Context(runner);
        var input = new VaultItemEditorInput(null, "First item", "", "saved password", "", "", "", "", null, "", false, false, false);
        var saving = context.Shell.SaveItemAsync(input);
        await runner.Completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(context.Shell.IsSavingItem);
        Assert.False(await context.Shell.SaveItemAsync(input));
        Assert.Single(context.Vault.GetItems());
        if (lockBeforeCompletion) await context.Shell.LockCommand.ExecuteAsync(null);
        runner.Release.SetResult();
        Assert.Equal(!lockBeforeCompletion, await saving);
        Assert.False(context.Shell.IsSavingItem);

        if (lockBeforeCompletion) await context.Shell.UnlockAsync(Context.Password, "");
        Assert.Single(context.Vault.GetItems());
        Assert.False(await context.Shell.SaveItemAsync(input with { Title = "" }));
        Assert.False(context.Shell.IsSavingItem);
        Assert.Single(context.Vault.GetItems());
        Assert.True(await context.Shell.SaveItemAsync(input with { Title = "Second item" }));
        Assert.False(context.Shell.IsSavingItem);
        Assert.Equal(2, context.Vault.GetItems().Count);
    }

    [Fact]
    public async Task Permanent_delete_confirmation_from_a_previous_unlock_is_discarded()
    {
        using var context = new Context();
        var item = context.Vault.AddItem(new VaultItem { Title = "Trashed", Password = "password" });
        context.Vault.DeleteItem(item.Id);
        await context.Shell.Trash.LoadAsync();
        context.Shell.Trash.SelectedItem = Assert.Single(context.Shell.Trash.Items);
        context.Dialogs.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var deletion = context.Shell.Trash.PermanentlyDeleteSelectedAsync();
        await context.Shell.LockCommand.ExecuteAsync(null);
        await context.Shell.UnlockAsync(Context.Password, "");
        context.Dialogs.Pending.SetResult(true);
        await deletion;
        Assert.Single(context.Vault.GetDeletedItems());
        Assert.False(context.Shell.Trash.IsErrorOpen);
    }

    private sealed class Context : IDisposable
    {
        public const string Password = "correct horse battery staple";
        public const string ReplacementPassword = "a replacement strong master password";
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "PasswordTool.Presentation.Tests", Guid.NewGuid().ToString("N"));
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public TotpService Totp { get; } = new();
        public string Secret { get; }
        public string RecoveryKey { get; } = RecoveryKeyService.Generate();
        public VaultOperationRunner Runner { get; } = new();
        public Dialogs Dialogs { get; } = new();
        public VaultService Vault { get; }
        public AppFlowCoordinator Flow { get; }
        public ShellViewModel Shell { get; }

        public Context(IVaultOperationRunner? operationRunner = null, ISensitiveClipboardService? clipboard = null)
        {
            Secret = Totp.GenerateSecret();
            Vault = new(new VaultStorageService(Directory), new EncryptionService(), Totp, utcNow: () => Now);
            Vault.InitializeNewVault(Password, Secret, Totp.GetCurrentCode(Secret).Code, RecoveryKey, true);
            Flow = new(Vault, operationRunner ?? Runner, Totp);
            var workspace = new VaultWorkspaceViewModel(Flow);
            var picker = new Picker();
            var mapper = new UserErrorMapper();
            Shell = new(Flow, workspace, null!, new SettingsViewModel(Flow, mapper),
                new BackupViewModel(Flow, picker, Dialogs, mapper, workspace), new SecurityCheckViewModel(Flow, mapper),
                new TrashViewModel(Flow, Dialogs, mapper, workspace), new NavigationService(), clipboard ?? new Clipboard(), picker, mapper, Dialogs);
        }

        public void Dispose()
        {
            Vault.Dispose();
            Runner.Dispose();
            if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true);
        }
    }

    private sealed class HoldFirstResultRunner : IVaultOperationRunner
    {
        private int first = 1;
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<T> RunAsync<T>(Func<T> operation, CancellationToken cancellationToken = default)
        {
            var result = operation();
            if (Interlocked.Exchange(ref first, 0) == 1)
            {
                Completed.SetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
        public Task RunAsync(Action operation, CancellationToken cancellationToken = default) =>
            RunAsync(() => { operation(); return true; }, cancellationToken);
    }

    private sealed class Dialogs : IUserDialogService
    {
        public string? ActionPassword { get; set; }
        public TaskCompletionSource<string?>? PendingPassword { get; set; }
        public int PasswordPrompts { get; private set; }
        public Task<string?> PromptMasterPasswordAsync(CancellationToken cancellationToken = default)
        {
            PasswordPrompts++;
            return PendingPassword?.Task ?? Task.FromResult(ActionPassword);
        }
        public bool Confirmed { get; set; } = true;
        public TaskCompletionSource<bool>? Pending { get; set; }
        public List<string> Secrets { get; } = [];
        public List<string> ConfirmationTitles { get; } = [];
        public Task<bool> ConfirmAsync(string title, string message, string confirmText, CancellationToken cancellationToken = default)
        {
            ConfirmationTitles.Add(title);
            return Pending?.Task ?? Task.FromResult(Confirmed);
        }
        public Task ShowSecretAsync(string title, string value, bool multiline, CancellationToken cancellationToken = default) { Secrets.Add(value); return Task.CompletedTask; }
        public Task<(string Confirmation, string TotpCode)?> ConfirmGroupDeletionAsync(string groupName, CancellationToken cancellationToken = default) => Task.FromResult<(string, string)?>(null);
        public Task ShowErrorAsync(string title, string message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string?> PromptTotpAsync(string title, string message, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task<string?> PromptBackupPassphraseAsync(string title, string message, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }

    private sealed class Clipboard : ISensitiveClipboardService
    {
        public Task CopyAsync(string value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ClearOwnedValueAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class DeferredClipboard : ISensitiveClipboardService
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Writes { get; private set; }
        public string? Value { get; private set; }
        public Task CopyAsync(string value, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Manual secrets must use the guarded clipboard overload.");
        public async Task CopyAsync(string value, Func<bool> canCopy, CancellationToken cancellationToken = default, bool clearAutomatically = true)
        {
            Entered.SetResult();
            await Release.Task.WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!canCopy()) throw new OperationCanceledException();
            Value = value;
            Writes++;
        }
        public Task ClearOwnedValueAsync(CancellationToken cancellationToken = default) { Value = null; return Task.CompletedTask; }
    }

    private sealed class Picker : IFilePickerService
    {
        public Task<string?> PickOpenPathAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task<string?> PickSavePathAsync(string suggestedFileName, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }
}
