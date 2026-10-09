using System.Text;
using PasswordTool.Core.Models;
using PasswordTool.Core.Services;

namespace PasswordTool.Core.Tests;

public sealed class VaultStorageBoundarySecurityTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "PasswordTool.StorageBoundaries", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(".config")]
    [InlineData(".trusted-unlock")]
    public void Oversized_metadata_is_rejected_without_altering_the_file(string name)
    {
        var storage = new VaultStorageService(directory);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        var bytes = new byte[VaultStorageService.MaxMetadataFileBytes + 1];
        File.WriteAllBytes(path, bytes);
        Assert.Throws<InvalidDataException>(() =>
        {
            if (name == ".config") _ = storage.LoadConfig(); else _ = storage.LoadTrustedUnlockToken();
        });
        Assert.Equal(bytes.LongLength, new FileInfo(path).Length);
        Assert.Throws<InvalidDataException>(() =>
        {
            if (name == ".config") storage.SaveConfig(new AppConfig()); else storage.SaveTrustedUnlockToken(new TrustedUnlockToken());
        });
        Assert.Equal(bytes.LongLength, new FileInfo(path).Length);
    }

    [Fact]
    public void Bounded_reader_rejects_oversized_input_and_honors_utf8_byte_limits()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "test-json");
        File.WriteAllText(path, "abc", Encoding.UTF8);
        Assert.Throws<InvalidDataException>(() => VaultStorageService.ReadBoundedTextFile(path, 2));
        File.WriteAllText(path, "é", new UTF8Encoding(false));
        Assert.Equal("é", VaultStorageService.ReadBoundedTextFile(path, 2));
        Assert.Throws<InvalidDataException>(() => VaultStorageService.ReadBoundedTextFile(path, 1));
    }

    [Fact]
    public void Snapshot_file_reads_enforce_their_metadata_limits()
    {
        var storage = new VaultStorageService(directory);
        storage.SaveConfig(new AppConfig());
        var snapshot = Path.Combine(storage.SnapshotsDirectory, "test-snapshot");
        Directory.CreateDirectory(snapshot);
        File.WriteAllBytes(Path.Combine(snapshot, ".config"), new byte[VaultStorageService.MaxMetadataFileBytes + 1]);
        File.WriteAllText(Path.Combine(snapshot, ".storage"), "{}");
        Assert.Throws<InvalidDataException>(() => storage.GetSnapshots());
        Assert.Throws<InvalidDataException>(() => storage.RestoreSnapshot("test-snapshot"));
    }

    [Fact]
    public void Invalid_journal_documents_do_not_replace_existing_state_or_delete_the_journal()
    {
        Directory.CreateDirectory(directory);
        var configPath = Path.Combine(directory, ".config");
        var vaultPath = Path.Combine(directory, ".storage");
        var transactionPath = Path.Combine(directory, ".state-transaction");
        File.WriteAllText(configPath, "original config");
        File.WriteAllText(vaultPath, "original vault");
        File.WriteAllText(transactionPath, System.Text.Json.JsonSerializer.Serialize(new
        {
            ConfigJson = new string('x', VaultStorageService.MaxMetadataFileBytes + 1),
            VaultJson = "{}"
        }));
        Assert.Throws<InvalidDataException>(() => new VaultStorageService(directory));
        Assert.Equal("original config", File.ReadAllText(configPath));
        Assert.Equal("original vault", File.ReadAllText(vaultPath));
        Assert.True(File.Exists(transactionPath));
    }

    public void Dispose()
    {
        if (!Directory.Exists(directory)) return;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(directory, recursive: true);
    }
}
