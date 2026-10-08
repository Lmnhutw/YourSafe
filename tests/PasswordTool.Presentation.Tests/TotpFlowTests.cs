using System.Text;
using System.Text.Json;
using PasswordTool.Autofill;
using PasswordTool.Core.Models;
using PasswordTool.Core.Services;

namespace PasswordTool.Presentation.Tests;

public sealed class TotpFlowTests
{
    [Theory]
    [InlineData("% A")]
    [InlineData("%0 ")]
    [InlineData("%\tA")]
    public void URI_percent_triplets_require_two_hexadecimal_characters(string invalidEncoding)
    {
        var input = "otpauth://totp/Account?secret=JBSWY3DPEHPK3PXP&issuer=" + invalidEncoding;
        Assert.False(new TotpService().TryParseWebsiteConfiguration(input, out _, out _));
    }

    [Fact]
    public void Version_two_totp_contract_is_strict_and_secret_free()
    {
        var request = new AutofillRequest(WireProtocol.Version, Guid.NewGuid().ToString(), "getCredentialTotp",
            new AutofillPayload("https://example.com", Guid.NewGuid()));
        WireProtocol.Validate(request);
        WireProtocol.Validate(request with { Action = "copyCredentialTotp" });
        Assert.Throws<JsonException>(() => WireProtocol.Validate(request with { Version = 1 }));
        Assert.Throws<JsonException>(() => WireProtocol.Validate(request with { Payload = new AutofillPayload("https://example.com", Guid.Empty) }));
        Assert.Throws<JsonException>(() => WireProtocol.Validate(request with { Payload = new AutofillPayload("https://example.com/path", Guid.NewGuid()) }));
        var result = new CredentialTotp("001234", 60, DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds());
        var response = JsonSerializer.SerializeToUtf8Bytes(WireProtocol.Success(request.RequestId, result), WireProtocol.Json);
        Assert.True(WireProtocol.ParseResponse(response, request).Ok);
        Assert.DoesNotContain(result.Code, result.ToString());
        foreach (var invalid in new object[]
        {
            new { code = "001234", periodSeconds = 60, expiresAtUnixMs = result.ExpiresAtUnixMs, secret = "forbidden" },
            new { code = "12345", periodSeconds = 60, expiresAtUnixMs = result.ExpiresAtUnixMs },
            new { code = "123 456", periodSeconds = 60, expiresAtUnixMs = result.ExpiresAtUnixMs },
            new { code = "001234", periodSeconds = 0, expiresAtUnixMs = result.ExpiresAtUnixMs },
            new { code = "001234", periodSeconds = 60, expiresAtUnixMs = 0 },
            new { code = "001234", periodSeconds = 60, expiresAtUnixMs = 253402300800000L }
        })
            Assert.Throws<JsonException>(() => WireProtocol.ParseResponse(
                JsonSerializer.SerializeToUtf8Bytes(WireProtocol.Success(request.RequestId, invalid), WireProtocol.Json), request));
        request = request with { Action = "copyCredentialTotp" };
        Assert.True(WireProtocol.ParseResponse(JsonSerializer.SerializeToUtf8Bytes(
            WireProtocol.Success(request.RequestId, new CopyTotpResult(true)), WireProtocol.Json), request).Ok);
        Assert.Throws<JsonException>(() => WireProtocol.ParseResponse(JsonSerializer.SerializeToUtf8Bytes(
            WireProtocol.Success(request.RequestId, new CopyTotpResult(false)), WireProtocol.Json), request));
        request = request with { Action = "findCredentials", Payload = new AutofillPayload("https://example.com") };
        var missingMetadata = new { credentials = new[] { new { id = Guid.NewGuid(), title = "Account", username = "user" } }, truncated = false };
        Assert.Throws<JsonException>(() => WireProtocol.ParseResponse(JsonSerializer.SerializeToUtf8Bytes(
            WireProtocol.Success(request.RequestId, missingMetadata), WireProtocol.Json), request));
        var duplicate = Encoding.UTF8.GetBytes($$$"""{"version":2,"requestId":"{{{request.RequestId}}}","ok":true,"result":{"credentials":[{"id":"{{{Guid.NewGuid()}}}","title":"Account","title":"Again","username":"user","hasPassword":true,"hasTotp":false}],"truncated":false}}""");
        Assert.Throws<JsonException>(() => WireProtocol.ParseResponse(duplicate, request));
    }

    [Fact]
    public async Task Totp_only_discovery_and_retrieval_recheck_URL_and_eligibility()
    {
        using var fixture = new VaultFixture();
        var item = fixture.Vault.AddItem(new VaultItem { Title = "TOTP only", Username = "user", Url = "https://example.com", TotpSecretBase32 = fixture.Secret });
        var metadata = Assert.Single(await fixture.Flow.FindAutofillCredentialsAsync("https://example.com"));
        Assert.False(metadata.HasPassword);
        Assert.True(metadata.HasTotp);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Flow.GetAutofillCredentialSecretAsync("https://example.com", item.Id));
        var code = await fixture.Flow.GetAutofillCredentialTotpAsync("https://example.com", item.Id);
        var wire = JsonSerializer.Serialize(new CredentialTotp(code.Code, code.PeriodSeconds, code.ExpiresAtUtc.ToUnixTimeMilliseconds()), WireProtocol.Json);
        Assert.DoesNotContain(fixture.Secret, wire);
        Assert.DoesNotContain("otpauth", wire);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Flow.GetAutofillCredentialTotpAsync("https://evil.example", item.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Flow.GetAutofillCredentialTotpAsync("https://example.com", Guid.NewGuid()));
        var edited = fixture.Vault.GetItemForEditing(item.Id, "");
        edited.Url = "https://changed.example";
        await fixture.Flow.UpdateItemAsync(edited);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Flow.GetAutofillCredentialTotpAsync("https://example.com", item.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Flow.CopyAutofillCredentialTotpAsync("https://example.com", item.Id));
        edited.Url = "";
        await fixture.Flow.UpdateItemAsync(edited);
        Assert.Single(await fixture.Flow.FindAutofillCredentialsAsync("https://example.com"));
        Assert.NotEmpty((await fixture.Flow.GetAutofillCredentialTotpAsync("https://example.com", item.Id)).Code);
        edited.SetTotpConfiguration(null);
        edited.RecoveryCodes = ["alpha-1234", "beta-5678"];
        await fixture.Flow.UpdateItemAsync(edited);
        Assert.Empty(await fixture.Flow.FindAutofillCredentialsAsync("https://example.com"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Flow.GetAutofillCredentialTotpAsync("https://example.com", item.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Copy_regenerates_after_window_rotation_and_preserves_numeric_code(bool nativeCopy)
    {
        using var fixture = new VaultFixture();
        var item = fixture.Vault.AddItem(new VaultItem { Title = "Account", TotpSecretBase32 = fixture.Secret });
        fixture.Now = DateTimeOffset.UtcNow.AddSeconds(-30);
        var displayed = await fixture.Flow.GetWebsiteTotpCodeAsync(item.Id);
        fixture.Now = DateTimeOffset.UtcNow;
        var expected = fixture.Vault.GetWebsiteTotpCode(item.Id);
        Assert.True(expected.ExpiresAtUtc > displayed.ExpiresAtUtc);
        if (nativeCopy) await fixture.Flow.CopyAutofillCredentialTotpAsync("https://example.com", item.Id);
        else await fixture.Flow.CopyWebsiteTotpAsync(item.Id);
        Assert.Equal(expected.Code, fixture.Clipboard.Value);
        Assert.Matches("^[0-9]{6}$", fixture.Clipboard.Value!);
        Assert.Equal(1, fixture.Clipboard.Writes);
        Assert.False(fixture.Clipboard.ClearAutomatically);
    }

    [Fact]
    public async Task Table_lock_blocks_live_reads_and_copy_without_password_prompt()
    {
        using var fixture = new VaultFixture();
        var item = fixture.Vault.AddItem(new VaultItem { Title = "Account", TotpSecretBase32 = fixture.Secret });
        await fixture.Flow.GetListItemsAsync();
        await fixture.Flow.LockTableAsync();
        Assert.True(fixture.Flow.IsCurrentUnlock(fixture.Flow.LifecycleVersion));
        Assert.False(fixture.Flow.IsCurrentNormalUnlock(fixture.Flow.LifecycleVersion));
        fixture.Flow.RequestActionPasswordAsync = _ => throw new InvalidOperationException("Automatic refresh must never prompt.");
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Flow.GetWebsiteTotpCodeAsync(item.Id));
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Flow.CopyWebsiteTotpAsync(item.Id));
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Flow.FindAutofillCredentialsAsync("https://example.com"));
        Assert.Equal(0, fixture.Clipboard.Writes);
    }

    [Theory]
    [InlineData(-30)]
    [InlineData(30)]
    public async Task A_code_from_another_time_window_cannot_be_written_to_clipboard(int clockOffsetSeconds)
    {
        using var fixture = new VaultFixture();
        var item = fixture.Vault.AddItem(new VaultItem { Title = "Account", TotpSecretBase32 = fixture.Secret });
        fixture.Now = DateTimeOffset.UtcNow.AddSeconds(clockOffsetSeconds);
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Flow.CopyWebsiteTotpAsync(item.Id));
        Assert.Equal(0, fixture.Clipboard.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_queued_clipboard_write_rechecks_lock_and_cancellation(bool cancel)
    {
        using var fixture = new VaultFixture();
        var item = fixture.Vault.AddItem(new VaultItem { Title = "Account", TotpSecretBase32 = fixture.Secret });
        fixture.Clipboard.Delay = true;
        using var cancellation = new CancellationTokenSource();
        var copying = fixture.Flow.CopyWebsiteTotpAsync(item.Id, cancellation.Token);
        await fixture.Clipboard.Entered.Task;
        if (cancel) cancellation.Cancel();
        else await fixture.Flow.LockTableAsync();
        fixture.Clipboard.Release.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => copying);
        Assert.Equal(0, fixture.Clipboard.Writes);
    }

    [Fact]
    public async Task A_completed_TOTP_read_is_discarded_if_lock_begins_before_return()
    {
        using var fixture = new VaultFixture();
        var item = fixture.Vault.AddItem(new VaultItem { Title = "Account", TotpSecretBase32 = fixture.Secret });
        var runner = new DelayedRunner();
        var flow = new AppFlowCoordinator(fixture.Vault, runner, new TotpService());
        var reading = flow.GetWebsiteTotpCodeAsync(item.Id);
        await runner.Completed.Task;
        await flow.LockAsync();
        runner.Release.TrySetResult();
        await Assert.ThrowsAsync<OperationCanceledException>(() => reading);
    }

    private sealed class VaultFixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "YourSafe.Totp.Flow.Tests", Guid.NewGuid().ToString("N"));
        private readonly VaultOperationRunner runner = new();
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public string Secret { get; }
        public VaultService Vault { get; }
        public DeferredClipboard Clipboard { get; } = new();
        public AppFlowCoordinator Flow { get; }
        public VaultFixture()
        {
            var totp = new TotpService();
            Secret = totp.GenerateSecret();
            Vault = new VaultService(new VaultStorageService(directory), new EncryptionService(), totp, utcNow: () => Now);
            Vault.InitializeNewVault("correct horse battery staple", Secret, totp.GetCurrentCode(Secret).Code, RecoveryKeyService.Generate(), true);
            Flow = new AppFlowCoordinator(Vault, runner, totp, Clipboard);
        }
        public void Dispose()
        {
            Vault.Dispose();
            runner.Dispose();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private sealed class DeferredClipboard : ISensitiveClipboardService
    {
        public bool Delay;
        public string? Value;
        public int Writes;
        public bool? ClearAutomatically;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task CopyAsync(string value, CancellationToken cancellationToken = default)
        {
            Value = value;
            Writes++;
            return Task.CompletedTask;
        }
        public async Task CopyAsync(string value, Func<bool> canCopy, CancellationToken cancellationToken = default, bool clearAutomatically = true)
        {
            ClearAutomatically = clearAutomatically;
            Entered.TrySetResult();
            if (Delay) await Release.Task.WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!canCopy()) throw new OperationCanceledException();
            await CopyAsync(value, cancellationToken);
        }
        public Task ClearOwnedValueAsync(CancellationToken cancellationToken = default) { Value = null; return Task.CompletedTask; }
    }

    private sealed class DelayedRunner : IVaultOperationRunner
    {
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<T> RunAsync<T>(Func<T> operation, CancellationToken cancellationToken = default)
        {
            var result = operation();
            Completed.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return result;
        }
        public Task RunAsync(Action operation, CancellationToken cancellationToken = default) { operation(); return Task.CompletedTask; }
    }
}
