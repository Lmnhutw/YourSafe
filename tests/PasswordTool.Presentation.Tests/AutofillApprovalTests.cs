using PasswordTool.Core.Models;
using PasswordTool.Core.Services;
using PasswordTool.Presentation.Autofill;

namespace PasswordTool.Presentation.Tests;

public sealed class AutofillApprovalTests
{
    [Fact]
    public async Task Disabled_integration_and_absent_handler_deny_all_secret_operations()
    {
        using var fixture = new Fixture();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Flow.FindAutofillCredentialsAsync(Fixture.Origin));
        foreach (var action in Enum.GetValues<AutofillAction>())
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Request(action));
        fixture.Flow.SetBrowserIntegrationEnabled(true);
        foreach (var action in Enum.GetValues<AutofillAction>())
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Request(action));
        Assert.Equal(0, fixture.Clipboard.Writes);
    }

    [Theory]
    [InlineData(AutofillAction.Password)]
    [InlineData(AutofillAction.ViewTotp)]
    [InlineData(AutofillAction.CopyTotp)]
    public async Task Each_action_requires_its_own_vault_derived_desktop_approval(AutofillAction action)
    {
        using var fixture = new Fixture();
        fixture.Flow.SetBrowserIntegrationEnabled(true);
        var approvals = new List<AutofillApprovalRequest>();
        fixture.Flow.RequestAutofillApprovalAsync = (request, _) => { approvals.Add(request); return Task.FromResult(false); };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Request(action));
        var denied = Assert.Single(approvals);
        Assert.Equal(fixture.Item.Id, denied.Credential.Id);
        Assert.Equal("Synthetic account", denied.Credential.Title);
        Assert.Equal(Fixture.Origin, denied.Origin);
        Assert.Equal(action, denied.Action);
        Assert.Equal(0, fixture.Clipboard.Writes);
        fixture.Flow.RequestAutofillApprovalAsync = (request, _) => { approvals.Add(request); return Task.FromResult(true); };
        await fixture.Request(action);
        Assert.Equal(2, approvals.Count);
        Assert.Equal(action == AutofillAction.CopyTotp ? 1 : 0, fixture.Clipboard.Writes);
        if (action == AutofillAction.CopyTotp) Assert.True(fixture.Clipboard.ClearAutomatically);
        // A subsequent request cannot reuse the previous decision.
        fixture.Flow.RequestAutofillApprovalAsync = (_, _) => Task.FromResult(false);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Request(action));
    }

    [Theory]
    [InlineData("lock")]
    [InlineData("logout")]
    [InlineData("disable")]
    [InlineData("edit")]
    [InlineData("delete")]
    [InlineData("settings")]
    [InlineData("deadline")]
    [InlineData("disconnect")]
    public async Task Pending_approval_is_invalidated_without_holding_vault_runner(string transition)
    {
        var clock = new ApprovalClock();
        using var fixture = new Fixture(clock);
        fixture.Flow.SetBrowserIntegrationEnabled(true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var consent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Flow.RequestAutofillApprovalAsync = (_, _) => { entered.TrySetResult(); return consent.Task; };
        using var client = new CancellationTokenSource();
        var reading = fixture.Flow.GetAutofillCredentialSecretAsync(Fixture.Origin, fixture.Item.Id, client.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // A second action or client cannot share the pending approval.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Request(AutofillAction.ViewTotp));
        switch (transition)
        {
            case "lock":
                await fixture.Flow.LockAsync().WaitAsync(TimeSpan.FromSeconds(5));
                await fixture.Flow.UnlockAsync(Fixture.Password, fixture.Totp.GetCurrentCode(Fixture.Seed).Code);
                break;
            case "logout": await fixture.Flow.LogoutAsync().WaitAsync(TimeSpan.FromSeconds(5)); break;
            case "disable": fixture.Flow.SetBrowserIntegrationEnabled(false); break;
            case "edit":
                var edit = fixture.Vault.GetItemForEditing(fixture.Item.Id, "");
                edit.Password = "changed-synthetic-password";
                await fixture.Flow.UpdateItemAsync(edit).WaitAsync(TimeSpan.FromSeconds(5));
                break;
            case "delete": await fixture.Flow.DeleteItemAsync(fixture.Item.Id).WaitAsync(TimeSpan.FromSeconds(5)); break;
            case "settings": Assert.True((await fixture.Flow.UpdateSettingsAsync(Fixture.Password, 2)).Success); break;
            case "deadline": clock.Fire(); break;
            case "disconnect": client.Cancel(); break;
        }
        consent.TrySetResult(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
        Assert.Equal(0, fixture.Clipboard.Writes);
    }

    [Fact]
    public async Task Global_desktop_rate_limit_applies_to_discovery_and_new_clients()
    {
        using var fixture = new Fixture();
        fixture.Flow.SetBrowserIntegrationEnabled(true);
        for (var i = 0; i < 30; i++) Assert.Single(await fixture.Flow.FindAutofillCredentialsAsync(Fixture.Origin));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Flow.FindAutofillCredentialsAsync(Fixture.Origin));
        fixture.Flow.RequestAutofillApprovalAsync = (_, _) => throw new InvalidOperationException("Must not prompt past rate limit");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Request(AutofillAction.Password));
        fixture.Flow.SetBrowserIntegrationEnabled(false);
        fixture.Flow.SetBrowserIntegrationEnabled(true);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Request(AutofillAction.CopyTotp));
    }

    [Fact]
    public async Task Disabling_integration_invalidates_a_queued_approved_clipboard_write()
    {
        using var fixture = new Fixture();
        fixture.Flow.SetBrowserIntegrationEnabled(true);
        fixture.Flow.RequestAutofillApprovalAsync = (_, _) => Task.FromResult(true);
        fixture.Clipboard.Delay = true;
        var copying = fixture.Flow.CopyAutofillCredentialTotpAsync(Fixture.Origin, fixture.Item.Id);
        await fixture.Clipboard.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Flow.SetBrowserIntegrationEnabled(false);
        fixture.Clipboard.Release.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => copying);
        Assert.Equal(0, fixture.Clipboard.Writes);
    }

    [Fact]
    public async Task Original_pipe_guard_is_rechecked_at_the_approved_clipboard_write()
    {
        using var fixture = new Fixture();
        fixture.Flow.SetBrowserIntegrationEnabled(true);
        fixture.Flow.RequestAutofillApprovalAsync = (_, _) => Task.FromResult(true);
        fixture.Clipboard.Delay = true;
        var requestValid = true;
        var copying = fixture.Flow.CopyAutofillCredentialTotpAsync(Fixture.Origin, fixture.Item.Id,
            canCopy: () => requestValid);
        await fixture.Clipboard.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        requestValid = false;
        fixture.Clipboard.Release.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => copying);
        Assert.Equal(0, fixture.Clipboard.Writes);
    }

    private sealed class Fixture : IDisposable
    {
        public const string Password = "correct horse battery staple", Seed = "JBSWY3DPEHPK3PXP", Origin = "https://example.com";
        private readonly string directory = Path.Combine(Path.GetTempPath(), "YourSafe.Approval.Tests", Guid.NewGuid().ToString("N"));
        private readonly VaultOperationRunner runner = new();
        public TotpService Totp { get; } = new();
        public VaultService Vault { get; }
        public AppFlowCoordinator Flow { get; }
        public VaultItem Item { get; }
        public DeferredClipboard Clipboard { get; } = new();
        public Fixture(TimeProvider? timeProvider = null)
        {
            Vault = new VaultService(new VaultStorageService(directory), new EncryptionService(), Totp);
            Vault.InitializeNewVault(Password, Seed, Totp.GetCurrentCode(Seed).Code, RecoveryKeyService.Generate(), true);
            Item = Vault.AddItem(new VaultItem { Title = "Synthetic account", Username = "test@example.test", Url = Origin, Password = "synthetic-test-password", TotpSecretBase32 = Seed });
            Flow = new AppFlowCoordinator(Vault, runner, Totp, Clipboard, timeProvider);
        }
        public Task Request(AutofillAction action) => action switch
        {
            AutofillAction.Password => Flow.GetAutofillCredentialSecretAsync(Origin, Item.Id),
            AutofillAction.ViewTotp => Flow.GetAutofillCredentialTotpAsync(Origin, Item.Id),
            AutofillAction.CopyTotp => Flow.CopyAutofillCredentialTotpAsync(Origin, Item.Id),
            _ => throw new InvalidOperationException()
        };
        public void Dispose() { Vault.Dispose(); runner.Dispose(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private sealed class DeferredClipboard : ISensitiveClipboardService
    {
        public bool Delay;
        public int Writes;
        public bool ClearAutomatically;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task CopyAsync(string value, CancellationToken token = default) { Writes++; return Task.CompletedTask; }
        public async Task CopyAsync(string value, Func<bool> canCopy, CancellationToken token = default, bool clearAutomatically = true)
        {
            Entered.TrySetResult();
            if (Delay) await Release.Task.WaitAsync(token);
            token.ThrowIfCancellationRequested();
            if (!canCopy()) throw new OperationCanceledException();
            ClearAutomatically = clearAutomatically;
            Writes++;
        }
        public Task ClearOwnedValueAsync(CancellationToken token = default) => Task.CompletedTask;
    }

    private sealed class ApprovalClock : TimeProvider
    {
        private readonly List<Timer> timers = [];
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(TimeSpan.FromSeconds(60), dueTime);
            var timer = new Timer(callback, state);
            timers.Add(timer);
            return timer;
        }
        public void Fire()
        {
            foreach (var timer in timers.ToArray()) timer.Fire();
        }
        private sealed class Timer(TimerCallback callback, object? state) : ITimer
        {
            private bool disposed;
            public void Fire() { if (!disposed) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() => disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
