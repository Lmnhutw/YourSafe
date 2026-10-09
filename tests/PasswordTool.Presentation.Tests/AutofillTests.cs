using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using PasswordTool.Autofill;
using PasswordTool.Autofill.Transport;
using PasswordTool.Core.Models;
using PasswordTool.Core.Services;
using PasswordTool.Presentation.Autofill;

namespace PasswordTool.Presentation.Tests;

public sealed class AutofillTests
{
    [Fact]
    public void CSharp_and_TypeScript_share_contract_fixtures()
    {
        using var fixtures = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contract-fixtures.json")));
        foreach (var fixture in fixtures.RootElement.GetProperty("requests").EnumerateArray())
        {
            var bytes = Encoding.UTF8.GetBytes(fixture.GetProperty("value").GetRawText());
            if (fixture.GetProperty("valid").GetBoolean()) WireProtocol.ParseRequest(bytes);
            else Assert.ThrowsAny<Exception>(() => WireProtocol.ParseRequest(bytes));
        }
        foreach (var fixture in fixtures.RootElement.GetProperty("origins").EnumerateArray())
        {
            var expected = fixture.GetProperty("canonical").GetString();
            var valid = CanonicalOrigin.TryParse(fixture.GetProperty("value").GetString(), out var origin);
            Assert.Equal(expected is not null, valid);
            if (valid) Assert.Equal(expected, origin);
        }
        Assert.Throws<JsonException>(() => WireProtocol.ParseRequest(Encoding.UTF8.GetBytes(
            "{\"version\":2,\"version\":2,\"requestId\":\"12345678-1234-1234-1234-123456789abc\",\"action\":\"ping\",\"payload\":{}}")));
        if (fixtures.RootElement.TryGetProperty("responses", out var responses))
        {
            foreach (var fixture in responses.EnumerateArray())
            {
                var request = WireProtocol.ParseRequest(Encoding.UTF8.GetBytes(fixture.GetProperty("request").GetRawText()));
                var bytes = Encoding.UTF8.GetBytes(fixture.GetProperty("value").GetRawText());
                if (fixture.GetProperty("valid").GetBoolean()) WireProtocol.ParseResponse(bytes, request);
                else Assert.ThrowsAny<Exception>(() => WireProtocol.ParseResponse(bytes, request));
            }
        }
    }

    [Fact]
    public async Task Framing_handles_partial_coalesced_truncated_and_oversized_input()
    {
        var message = Encoding.UTF8.GetBytes("{\"ok\":true}");
        using var stream = new MemoryStream();
        await Framing.WriteAsync(stream, message, default);
        await Framing.WriteAsync(stream, message, default);
        stream.Position = 0;
        using var partial = new PartialStream(stream.ToArray());
        Assert.Equal(message, await Framing.ReadAsync(partial, default));
        Assert.Equal(message, await Framing.ReadAsync(partial, default));
        Assert.Null(await Framing.ReadAsync(partial, default));
        using var truncated = new MemoryStream(stream.ToArray()[..^1]);
        await Framing.ReadAsync(truncated, default);
        await Assert.ThrowsAsync<EndOfStreamException>(() => Framing.ReadAsync(truncated, default));
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 65537);
        using var oversized = new MemoryStream(header);
        await Assert.ThrowsAsync<InvalidDataException>(() => Framing.ReadAsync(oversized, default));
        using var malformed = new MemoryStream(new byte[] { 1, 0, 0, 0, 0xff });
        await Assert.ThrowsAnyAsync<DecoderFallbackException>(() => Framing.ReadAsync(malformed, default));
        using var expired = new MemoryStream();
        await Assert.ThrowsAsync<OperationCanceledException>(() => Framing.WriteAsync(expired, message, default, () => false));
        Assert.Equal(4, expired.Length); // Header only: no response body after lifecycle invalidation.
    }

    [Fact]
    public void Discovery_fits_the_frame_budget_and_reports_truncation()
    {
        var request = new AutofillRequest(WireProtocol.Version, Guid.NewGuid().ToString(), "findCredentials", new AutofillPayload("https://example.com"));
        var credentials = Enumerable.Range(0, 100)
            .Select(_ => new CredentialMetadata(Guid.NewGuid(), new string('\u00e9', 200), new string('\u00e9', 500), true, false)).ToArray();
        var response = WireProtocol.DiscoverySuccess(request.RequestId, credentials);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(response, WireProtocol.Json);
        Assert.True(bytes.Length <= WireProtocol.MaxFrameBytes);
        var discovery = WireProtocol.ParseResponse(bytes, request).Result!.Value.Deserialize<DiscoveryResult>(WireProtocol.Json)!;
        Assert.True(discovery.Truncated);
        Assert.NotEmpty(discovery.Credentials);
        Assert.Equal(credentials.Take(discovery.Credentials.Count), discovery.Credentials);
        // The next complete account would cross the budget, including JSON escaping and envelope overhead.
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(WireProtocol.Success(request.RequestId,
            new DiscoveryResult(credentials.Take(discovery.Credentials.Count + 1).ToArray(), true)), WireProtocol.Json).Length > WireProtocol.MaxFrameBytes);
        var small = WireProtocol.DiscoverySuccess(request.RequestId, credentials[..1]).Result!.Value.Deserialize<DiscoveryResult>(WireProtocol.Json)!;
        Assert.False(small.Truncated);
        Assert.Single(small.Credentials);
        var huge = WireProtocol.DiscoverySuccess(request.RequestId,
            [new CredentialMetadata(Guid.NewGuid(), new string('x', WireProtocol.MaxFrameBytes), "", true, false)]);
        var empty = huge.Result!.Value.Deserialize<DiscoveryResult>(WireProtocol.Json)!;
        Assert.True(empty.Truncated);
        Assert.Empty(empty.Credentials);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(huge, WireProtocol.Json).Length <= WireProtocol.MaxFrameBytes);
    }

    [Fact]
    public async Task Imported_null_username_is_normalized_in_metadata_and_secret()
    {
        var directory = Path.Combine(Path.GetTempPath(), "YourSafe.Autofill.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var totp = new TotpService();
            var secret = totp.GenerateSecret();
            var code = totp.GetCurrentCode(secret).Code;
            using var vault = new VaultService(new VaultStorageService(directory), new EncryptionService(), totp);
            vault.InitializeNewVault("correct horse battery staple", secret, code, RecoveryKeyService.Generate(), true);
            var item = new VaultItem { Title = "Imported", Username = null!, Password = "synthetic", Url = "https://example.com" };
            var backup = new VaultBackupService().CreateBackup([item], "correct backup passphrase", DateTimeOffset.UtcNow);
            Assert.Equal(1, vault.ImportBackupJson(backup, "correct backup passphrase", code));
            using var runner = new VaultOperationRunner();
            var flow = new AppFlowCoordinator(vault, runner, totp) { RequestAutofillApprovalAsync = (_, _) => Task.FromResult(true) };
            flow.SetBrowserIntegrationEnabled(true);
            var metadata = Assert.Single(await flow.FindAutofillCredentialsAsync("https://example.com"));
            Assert.Equal("", metadata.Username);
            var retrieved = await flow.GetAutofillCredentialSecretAsync("https://example.com", metadata.Id);
            Assert.Equal("", retrieved.Username);
            Assert.Equal("synthetic", retrieved.Password);
            var request = new AutofillRequest(WireProtocol.Version, Guid.NewGuid().ToString(), "findCredentials", new AutofillPayload("https://example.com"));
            Assert.True(WireProtocol.ParseResponse(JsonSerializer.SerializeToUtf8Bytes(
                WireProtocol.DiscoverySuccess(request.RequestId, [metadata]), WireProtocol.Json), request).Ok);
            request = request with { Action = "getCredentialSecret", Payload = new AutofillPayload("https://example.com", metadata.Id) };
            Assert.True(WireProtocol.ParseResponse(JsonSerializer.SerializeToUtf8Bytes(
                WireProtocol.Success(request.RequestId, retrieved), WireProtocol.Json), request).Ok);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void NativeHost_validates_typed_results_before_forwarding()
    {
        var request = new AutofillRequest(WireProtocol.Version, "12345678-1234-1234-1234-123456789abc", "getCredentialSecret",
            new AutofillPayload("https://example.com", Guid.NewGuid()));
        var response = WireProtocol.Success(request.RequestId, new CredentialSecret("test", "synthetic"));
        Assert.True(WireProtocol.ParseResponse(JsonSerializer.SerializeToUtf8Bytes(response, WireProtocol.Json), request).Ok);
        Assert.Throws<JsonException>(() => WireProtocol.ParseResponse(JsonSerializer.SerializeToUtf8Bytes(
            WireProtocol.Success(request.RequestId, new { username = "test", password = "synthetic", notes = "must-not-forward" }), WireProtocol.Json), request));
        Assert.Throws<JsonException>(() => WireProtocol.ParseResponse(JsonSerializer.SerializeToUtf8Bytes(
            WireProtocol.Success(request.RequestId, new { username = "test" }), WireProtocol.Json), request));
        Assert.Throws<JsonException>(() => WireProtocol.ParseResponse(JsonSerializer.SerializeToUtf8Bytes(response, WireProtocol.Json), request with { RequestId = Guid.NewGuid().ToString() }));
        Assert.False(WireProtocol.ParseResponse(JsonSerializer.SerializeToUtf8Bytes(WireProtocol.Failure(request.RequestId, "locked"), WireProtocol.Json), request).Ok);
        var explicitNull = Encoding.UTF8.GetBytes("{\"version\":2,\"requestId\":\"12345678-1234-1234-1234-123456789abc\",\"ok\":false,\"error\":\"locked\",\"result\":null}");
        Assert.Throws<JsonException>(() => WireProtocol.ParseResponse(explicitNull, request));
    }

    [Fact]
    public async Task Discovery_uses_real_hidden_URL_and_retrieval_rechecks_origin_item_and_lock()
    {
        var directory = Path.Combine(Path.GetTempPath(), "YourSafe.Autofill.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var totp = new TotpService();
            var secret = totp.GenerateSecret();
            using var vault = new VaultService(new VaultStorageService(directory), new EncryptionService(), totp);
            vault.InitializeNewVault("correct horse battery staple", secret, totp.GetCurrentCode(secret).Code, RecoveryKeyService.Generate(), true);
            using var runner = new VaultOperationRunner();
            var flow = new AppFlowCoordinator(vault, runner, totp) { RequestAutofillApprovalAsync = (_, _) => Task.FromResult(true) };
            flow.SetBrowserIntegrationEnabled(true);
            var matching = new VaultItem { Title = "Hidden", Username = "user", Password = "test-secret", Url = "https://example.com/login", HideUrl = true, Notes = "private" };
            matching = vault.AddItem(matching);
            await flow.AddItemAsync(new VaultItem { Title = "Subdomain", Password = "other", Url = "https://sub.example.com" });
            await flow.AddItemAsync(new VaultItem { Title = "Port", Password = "other", Url = "https://example.com:8443" });
            await flow.AddItemAsync(new VaultItem { Title = "Missing scheme", Password = "other", Url = "example.com" });
            await flow.AddItemAsync(new VaultItem { Title = "Recovery", Type = VaultItemType.RecoveryCodes, RecoveryCodes = ["code-one", "code-two"], Url = "https://example.com" });
            var deleted = new VaultItem { Title = "Deleted", Password = "other", Url = "https://example.com" };
            deleted = vault.AddItem(deleted);
            await flow.DeleteItemAsync(deleted.Id);
            var discovered = await flow.FindAutofillCredentialsAsync("https://example.com");
            Assert.Equal(matching.Id, Assert.Single(discovered).Id);
            Assert.DoesNotContain("\"password\":", JsonSerializer.Serialize(discovered, WireProtocol.Json), StringComparison.OrdinalIgnoreCase);
            Assert.Equal("test-secret", (await flow.GetAutofillCredentialSecretAsync("https://example.com", matching.Id)).Password);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => flow.GetAutofillCredentialSecretAsync("https://evil.example", matching.Id));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => flow.GetAutofillCredentialSecretAsync("https://example.com", deleted.Id));
            await flow.LockAsync();
            await Assert.ThrowsAsync<OperationCanceledException>(() => flow.FindAutofillCredentialsAsync("https://example.com"));
            await Assert.ThrowsAsync<OperationCanceledException>(() => flow.GetAutofillCredentialSecretAsync("https://example.com", matching.Id));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task No_URL_items_are_denied_in_discovery_and_secret_reads(string? url)
    {
        var directory = Path.Combine(Path.GetTempPath(), "YourSafe.Autofill.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var totp = new TotpService();
            var secret = totp.GenerateSecret();
            using var vault = new VaultService(new VaultStorageService(directory), new EncryptionService(), totp);
            vault.InitializeNewVault("correct horse battery staple", secret, totp.GetCurrentCode(secret).Code, RecoveryKeyService.Generate(), true);
            using var runner = new VaultOperationRunner();
            var flow = new AppFlowCoordinator(vault, runner, totp) { RequestAutofillApprovalAsync = (_, _) => Task.FromResult(true) };
            flow.SetBrowserIntegrationEnabled(true);
            var noUrl = vault.AddItem(new VaultItem { Title = "No URL", Username = "user", Password = "synthetic", Url = url! });
            var matching = vault.AddItem(new VaultItem { Title = "Site", Password = "site-secret", Url = "https://example.com/login" });
            var other = vault.AddItem(new VaultItem { Title = "Other site", Password = "other-secret", Url = "https://other.example" });
            await flow.AddItemAsync(new VaultItem { Title = "Recovery only", RecoveryCodes = ["code-one", "code-two"], Url = "" });

            var discovered = await flow.FindAutofillCredentialsAsync("https://example.com");
            Assert.Single(discovered);
            Assert.DoesNotContain(discovered, item => item.Id == noUrl.Id);
            Assert.Contains(discovered, item => item.Id == matching.Id);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => flow.GetAutofillCredentialSecretAsync("https://example.com", noUrl.Id));
            Assert.Equal("site-secret", (await flow.GetAutofillCredentialSecretAsync("https://example.com", matching.Id)).Password);
            Assert.Empty(await flow.FindAutofillCredentialsAsync("http://localhost:8222"));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => flow.GetAutofillCredentialSecretAsync("http://localhost:8222", noUrl.Id));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => flow.GetAutofillCredentialSecretAsync("https://example.com", other.Id));
            await Assert.ThrowsAsync<ArgumentException>(() => flow.GetAutofillCredentialSecretAsync("http://example.com", noUrl.Id));

            var edited = vault.GetItemForEditing(noUrl.Id, "");
            edited.Url = "https://other.example";
            await flow.UpdateItemAsync(edited);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => flow.GetAutofillCredentialSecretAsync("https://example.com", noUrl.Id));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pending_secret_is_discarded_when_lock_or_logout_begins(bool logout)
    {
        var directory = Path.Combine(Path.GetTempPath(), "YourSafe.Autofill.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var totp = new TotpService();
            var secret = totp.GenerateSecret();
            using var vault = new VaultService(new VaultStorageService(directory), new EncryptionService(), totp);
            vault.InitializeNewVault("correct horse battery staple", secret, totp.GetCurrentCode(secret).Code, RecoveryKeyService.Generate(), true);
            using var runner = new VaultOperationRunner();
            var flow = new AppFlowCoordinator(vault, runner, totp) { RequestAutofillApprovalAsync = (_, _) => Task.FromResult(true) };
            flow.SetBrowserIntegrationEnabled(true);
            var item = new VaultItem { Title = "Account", Password = "test-secret", Url = "https://example.com" };
            item = vault.AddItem(item);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var blocked = runner.RunAsync(() => { entered.SetResult(); release.Task.GetAwaiter().GetResult(); });
            await entered.Task;
            var pending = flow.GetAutofillCredentialSecretAsync("https://example.com", item.Id);
            var closing = logout ? flow.LogoutAsync() : flow.LockAsync();
            release.SetResult();
            await blocked;
            await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
            await closing;
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pending_secret_is_discarded_at_vault_or_sign_in_expiry(bool signInExpiry)
    {
        var directory = Path.Combine(Path.GetTempPath(), "YourSafe.Autofill.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var now = DateTimeOffset.UtcNow;
            var totp = new TotpService();
            var secret = totp.GenerateSecret();
            using var vault = new VaultService(new VaultStorageService(directory), new EncryptionService(), totp, utcNow: () => now);
            vault.InitializeNewVault("correct horse battery staple", secret, totp.GetCurrentCode(secret).Code, RecoveryKeyService.Generate(), true);
            var item = vault.AddItem(new VaultItem { Title = "Account", Password = "synthetic", Url = "https://example.com" });
            using var runner = new VaultOperationRunner();
            var flow = new AppFlowCoordinator(vault, runner, totp) { RequestAutofillApprovalAsync = (_, _) => Task.FromResult(true) };
            flow.SetBrowserIntegrationEnabled(true);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var blocked = runner.RunAsync(() => { entered.SetResult(); release.Task.GetAwaiter().GetResult(); });
            await entered.Task;
            var pending = flow.GetAutofillCredentialSecretAsync("https://example.com", item.Id);
            now = (signInExpiry ? vault.LoginExpiresAt : vault.VaultExpiresAt)!.Value;
            release.SetResult();
            await blocked;
            await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Windows_pipe_peers_use_OS_PID_session_and_exact_executable_path()
    {
        if (!OperatingSystem.IsWindows()) return;
#if DEBUG
        Assert.Contains(".dev.", PipePeer.PipeName);
#else
        Assert.Contains(".prod.", PipePeer.PipeName);
#endif
        var name = "YourSafe.Peer.Tests." + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var waiting = server.WaitForConnectionAsync(timeout.Token);
        await client.ConnectAsync(timeout.Token);
        await waiting;
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException();
        PipePeer.Verify(server, true, executable);
        PipePeer.Verify(client, false, executable);
        Assert.Throws<UnauthorizedAccessException>(() => { if (OperatingSystem.IsWindows()) PipePeer.Verify(server, true, executable + ".wrong"); });
        Assert.Throws<UnauthorizedAccessException>(() => { if (OperatingSystem.IsWindows()) PipePeer.Verify(client, false, executable + ".wrong"); });
    }

    private sealed class PartialStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
    }
}
