using System.Globalization;
using System.Text;
using System.Text.Json;
using PasswordTool.Core.Models;

namespace PasswordTool.Core.Services;

public sealed class VaultStorageService
{
    public const int MaxMetadataFileBytes = 64 * 1024;
    public const int MaxVaultFileBytes = EncryptionService.MaxEncryptedPayloadJsonBytes;
    // The existing journal embeds both documents as JSON strings, which can escape each byte as six characters.
    public const int MaxStateTransactionFileBytes = 6 * (MaxMetadataFileBytes + MaxVaultFileBytes) + 1024;
    private const int MaxSnapshots = 5;
    private readonly Action<string>? stateWriteCheckpoint;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public VaultStorageService()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PasswordTool"), null)
    {
    }

    public VaultStorageService(string appDirectory, Action<string>? stateWriteCheckpoint = null)
    {
        if (string.IsNullOrWhiteSpace(appDirectory))
        {
            throw new ArgumentException("Application storage directory is required.", nameof(appDirectory));
        }

        AppDirectory = appDirectory;
        this.stateWriteCheckpoint = stateWriteCheckpoint;
        ConfigPath = Path.Combine(AppDirectory, ".config");
        VaultPath = Path.Combine(AppDirectory, ".storage");
        TrustedUnlockTokenPath = Path.Combine(AppDirectory, ".trusted-unlock");
        SnapshotsDirectory = Path.Combine(AppDirectory, ".snapshots");
        StateTransactionPath = Path.Combine(AppDirectory, ".state-transaction");
        RecoverInterruptedStateTransaction();
    }

    public string AppDirectory { get; }

    public string ConfigPath { get; }

    public string VaultPath { get; }

    public string TrustedUnlockTokenPath { get; }

    public string SnapshotsDirectory { get; }

    internal string StateTransactionPath { get; }

    public bool HasConfig => File.Exists(ConfigPath);

    public bool HasVault => File.Exists(VaultPath);

    public bool IsInitialized => HasConfig && HasVault;

    public bool HasPartialStorage => HasConfig != HasVault;

    public bool HasTrustedUnlockToken => File.Exists(TrustedUnlockTokenPath);

    public void SaveConfig(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (HasVault)
        {
            SaveState(config, LoadVaultPayload());
            return;
        }

        EnsureStorageDirectory();
        WriteProtectedText(ConfigPath, SerializeConfig(config));
    }

    public AppConfig LoadConfig()
    {
        if (!File.Exists(ConfigPath))
        {
            throw new FileNotFoundException("PasswordTool config file was not found.", ConfigPath);
        }

        return DeserializeConfig(ReadBoundedTextFile(ConfigPath, MaxMetadataFileBytes));
    }

    public void SaveTrustedUnlockToken(TrustedUnlockToken token)
    {
        ArgumentNullException.ThrowIfNull(token);
        EnsureStorageDirectory();
        WriteProtectedText(TrustedUnlockTokenPath, JsonSerializer.Serialize(token, JsonOptions));
    }

    public TrustedUnlockToken LoadTrustedUnlockToken()
    {
        if (!File.Exists(TrustedUnlockTokenPath))
        {
            throw new FileNotFoundException("PasswordTool trusted unlock token was not found.", TrustedUnlockTokenPath);
        }

        var token = JsonSerializer.Deserialize<TrustedUnlockToken>(ReadBoundedTextFile(TrustedUnlockTokenPath, MaxMetadataFileBytes), JsonOptions);
        return token ?? throw new InvalidOperationException("PasswordTool trusted unlock token is empty or invalid.");
    }

    public void DeleteTrustedUnlockToken()
    {
        if (!File.Exists(TrustedUnlockTokenPath)) return;
        TryClearProtectedAttributes(TrustedUnlockTokenPath);
        File.Delete(TrustedUnlockTokenPath);
    }

    public void SaveVaultPayload(string encryptedVaultJson)
    {
        ValidateVaultPayload(encryptedVaultJson);
        if (HasConfig)
        {
            SaveState(LoadConfig(), encryptedVaultJson);
            return;
        }

        EnsureStorageDirectory();
        WriteProtectedText(VaultPath, encryptedVaultJson);
    }

    public string LoadVaultPayload()
    {
        if (!File.Exists(VaultPath))
        {
            throw new FileNotFoundException("PasswordTool vault file was not found.", VaultPath);
        }
        return ReadBoundedTextFile(VaultPath, MaxVaultFileBytes);
    }

    public void SaveState(AppConfig config, string encryptedVaultJson)
    {
        ArgumentNullException.ThrowIfNull(config);
        ValidateVaultPayload(encryptedVaultJson);
        SaveRawState(SerializeConfig(config), encryptedVaultJson, createSnapshot: true, verifyStagedState: null, beforeCommit: null);
    }

    public void SaveStateVerified(AppConfig config, string encryptedVaultJson, Action<AppConfig, string> verifyStagedState,
        Action? beforeCommit = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(verifyStagedState);
        ValidateVaultPayload(encryptedVaultJson);
        SaveRawState(SerializeConfig(config), encryptedVaultJson, createSnapshot: true, verifyStagedState, beforeCommit);
    }

    public IReadOnlyList<VaultSnapshotInfo> GetSnapshots()
    {
        if (!Directory.Exists(SnapshotsDirectory)) return [];
        var revision = LoadConfig().CredentialRevision;
        return Directory.EnumerateDirectories(SnapshotsDirectory)
            .Select(path => new DirectoryInfo(path))
            .Where(directory => File.Exists(Path.Combine(directory.FullName, ".config"))
                && File.Exists(Path.Combine(directory.FullName, ".storage")))
            .OrderByDescending(directory => directory.Name, StringComparer.Ordinal)
            .Select(directory => new VaultSnapshotInfo(directory.Name, ParseSnapshotTime(directory),
                DeserializeConfig(ReadBoundedTextFile(Path.Combine(directory.FullName, ".config"), MaxMetadataFileBytes)).CredentialRevision != revision))
            .ToList();
    }

    public void RestoreSnapshot(string snapshotId, Action? beforeCommit = null)
    {
        if (string.IsNullOrWhiteSpace(snapshotId)
            || !string.Equals(Path.GetFileName(snapshotId), snapshotId, StringComparison.Ordinal)
            || snapshotId.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            throw new ArgumentException("The snapshot identifier is invalid.", nameof(snapshotId));
        }

        var directory = Path.Combine(SnapshotsDirectory, snapshotId);
        var configPath = Path.Combine(directory, ".config");
        var vaultPath = Path.Combine(directory, ".storage");
        if (!File.Exists(configPath) || !File.Exists(vaultPath))
        {
            throw new FileNotFoundException("The selected vault snapshot is incomplete or no longer exists.");
        }

        var configJson = ReadBoundedTextFile(configPath, MaxMetadataFileBytes);
        _ = DeserializeConfig(configJson);
        var vaultJson = ReadBoundedTextFile(vaultPath, MaxVaultFileBytes);
        ValidateVaultPayload(vaultJson);
        SaveRawState(configJson, vaultJson, createSnapshot: true, verifyStagedState: null, beforeCommit);
        DeleteTrustedUnlockToken();
    }

    private void SaveRawState(string configJson, string vaultJson, bool createSnapshot,
        Action<AppConfig, string>? verifyStagedState, Action? beforeCommit)
    {
        ValidateTextSize(configJson, MaxMetadataFileBytes);
        ValidateTextSize(vaultJson, MaxVaultFileBytes);
        EnsureStorageDirectory();
        var oldConfig = File.Exists(ConfigPath) ? ReadBoundedTextFile(ConfigPath, MaxMetadataFileBytes) : null;
        var oldVault = File.Exists(VaultPath) ? ReadBoundedTextFile(VaultPath, MaxVaultFileBytes) : null;
        var configTemp = $"{ConfigPath}.{Guid.NewGuid():N}.tmp";
        var vaultTemp = $"{VaultPath}.{Guid.NewGuid():N}.tmp";

        try
        {
            File.WriteAllText(configTemp, configJson);
            File.WriteAllText(vaultTemp, vaultJson);
            stateWriteCheckpoint?.Invoke("staged");
            var stagedConfig = DeserializeConfig(ReadBoundedTextFile(configTemp, MaxMetadataFileBytes));
            var stagedVault = ReadBoundedTextFile(vaultTemp, MaxVaultFileBytes);
            ValidateVaultPayload(stagedVault);
            verifyStagedState?.Invoke(stagedConfig, stagedVault);
            stateWriteCheckpoint?.Invoke("verified");
            beforeCommit?.Invoke();
            if (createSnapshot && oldConfig is not null && oldVault is not null)
            {
                CreateSnapshot(oldConfig, oldVault);
            }
            beforeCommit?.Invoke();
            WriteProtectedText(StateTransactionPath,
                JsonSerializer.Serialize(new PendingStateTransaction(configJson, vaultJson), JsonOptions));

            ValidateExistingFileSize(ConfigPath, MaxMetadataFileBytes);
            ValidateExistingFileSize(VaultPath, MaxVaultFileBytes);
            TryClearProtectedAttributes(ConfigPath);
            TryClearProtectedAttributes(VaultPath);
            File.Move(configTemp, ConfigPath, overwrite: true);
            stateWriteCheckpoint?.Invoke("config-replaced");
            File.Move(vaultTemp, VaultPath, overwrite: true);
            stateWriteCheckpoint?.Invoke("vault-replaced");
            TryApplyHiddenSystemAttributes(ConfigPath);
            TryApplyHiddenSystemAttributes(VaultPath);
            DeleteFileStrict(StateTransactionPath);
            PruneSnapshots();
        }
        catch
        {
            var configRestored = RestoreRawFile(ConfigPath, oldConfig);
            var vaultRestored = RestoreRawFile(VaultPath, oldVault);
            if (configRestored && vaultRestored) TryDeleteFile(StateTransactionPath);
            throw;
        }
        finally
        {
            TryDeleteFile(configTemp);
            TryDeleteFile(vaultTemp);
        }
    }

    private void CreateSnapshot(string configJson, string vaultJson)
    {
        Directory.CreateDirectory(SnapshotsDirectory);
        TryApplyHiddenSystemAttributes(SnapshotsDirectory);
        var id = $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffffffZ}-{Guid.NewGuid():N}";
        var directory = Path.Combine(SnapshotsDirectory, id);
        Directory.CreateDirectory(directory);
        try
        {
            WriteProtectedText(Path.Combine(directory, ".config"), configJson);
            WriteProtectedText(Path.Combine(directory, ".storage"), vaultJson);
        }
        catch
        {
            DeleteDirectoryBestEffort(directory);
            throw;
        }
    }

    private void PruneSnapshots()
    {
        if (!Directory.Exists(SnapshotsDirectory)) return;
        foreach (var directory in Directory.EnumerateDirectories(SnapshotsDirectory)
                     .Where(directory => !File.Exists(Path.Combine(directory, ".config"))
                         || !File.Exists(Path.Combine(directory, ".storage"))))
        {
            DeleteDirectoryBestEffort(directory);
        }
        foreach (var directory in Directory.EnumerateDirectories(SnapshotsDirectory)
                     .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
                     .Skip(MaxSnapshots))
        {
            foreach (var file in Directory.EnumerateFiles(directory)) TryClearProtectedAttributes(file);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static DateTimeOffset ParseSnapshotTime(DirectoryInfo directory)
    {
        var timestamp = directory.Name.Split('-', 2)[0];
        return DateTimeOffset.TryParseExact(timestamp, "yyyyMMdd'T'HHmmssfffffff'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var result)
            ? result
            : directory.CreationTimeUtc;
    }

    private void EnsureStorageDirectory()
    {
        Directory.CreateDirectory(AppDirectory);
        TryApplyHiddenSystemAttributes(AppDirectory);
    }

    private static string SerializeConfig(AppConfig config)
    {
        var json = JsonSerializer.Serialize(config, JsonOptions);
        ValidateTextSize(json, MaxMetadataFileBytes);
        return json;
    }

    private void RecoverInterruptedStateTransaction()
    {
        if (!File.Exists(StateTransactionPath)) return;
        var transaction = JsonSerializer.Deserialize<PendingStateTransaction>(
            ReadBoundedTextFile(StateTransactionPath, MaxStateTransactionFileBytes), JsonOptions)
            ?? throw new InvalidOperationException("PasswordTool state transaction is invalid.");
        _ = DeserializeConfig(transaction.ConfigJson);
        ValidateVaultPayload(transaction.VaultJson);
        ValidateExistingFileSize(ConfigPath, MaxMetadataFileBytes);
        ValidateExistingFileSize(VaultPath, MaxVaultFileBytes);
        EnsureStorageDirectory();
        WriteProtectedText(ConfigPath, transaction.ConfigJson);
        WriteProtectedText(VaultPath, transaction.VaultJson);
        DeleteFileStrict(StateTransactionPath);
    }

    private static AppConfig DeserializeConfig(string json)
    {
        ValidateTextSize(json, MaxMetadataFileBytes);
        var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
        return config ?? throw new InvalidOperationException("PasswordTool config file is empty or invalid.");
    }

    private static void ValidateVaultPayload(string encryptedVaultJson)
    {
        if (string.IsNullOrWhiteSpace(encryptedVaultJson))
        {
            throw new ArgumentException("Encrypted vault payload is required.", nameof(encryptedVaultJson));
        }
        ValidateTextSize(encryptedVaultJson, MaxVaultFileBytes);
        using var _ = JsonDocument.Parse(encryptedVaultJson);
    }

    internal static string ReadBoundedTextFile(string path, int maximumBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var length = stream.Length;
        if (length > maximumBytes) throw new InvalidDataException("The storage file exceeds the supported size limit.");
        var bytes = new byte[(int)length];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new InvalidDataException("The storage file changed while being read.");
        using var reader = new StreamReader(new MemoryStream(bytes));
        var text = reader.ReadToEnd();
        ValidateTextSize(text, maximumBytes);
        return text;
    }

    private static void ValidateTextSize(string text, int maximumBytes)
    {
        if (text is null || text.Length > maximumBytes || Encoding.UTF8.GetByteCount(text) > maximumBytes)
            throw new InvalidDataException("The storage document exceeds the supported UTF-8 size limit.");
    }

    private static void ValidateExistingFileSize(string path, int maximumBytes)
    {
        if (File.Exists(path) && new FileInfo(path).Length > maximumBytes)
            throw new InvalidDataException("The existing storage file exceeds the supported size limit.");
    }

    private static bool RestoreRawFile(string path, string? contents)
    {
        try
        {
            if (contents is null)
            {
                TryDeleteFile(path);
            }
            else
            {
                WriteProtectedText(path, contents);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void DeleteFileStrict(string path)
    {
        if (!File.Exists(path)) return;
        TryClearProtectedAttributes(path);
        File.Delete(path);
    }

    private static void DeleteDirectoryBestEffort(string path)
    {
        if (!Directory.Exists(path)) return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path)) TryClearProtectedAttributes(file);
            TryClearProtectedAttributes(path);
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }
    }

    private static void WriteProtectedText(string path, string contents)
    {
        var maximumBytes = Path.GetFileName(path) switch
        {
            ".storage" => MaxVaultFileBytes,
            ".state-transaction" => MaxStateTransactionFileBytes,
            _ => MaxMetadataFileBytes
        };
        ValidateTextSize(contents, maximumBytes);
        ValidateExistingFileSize(path, maximumBytes);
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tempPath, contents);
            TryClearProtectedAttributes(path);
            File.Move(tempPath, path, overwrite: true);
            TryApplyHiddenSystemAttributes(path);
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    private static void TryDeleteFile(string path)
    {
        if (!File.Exists(path)) return;
        try
        {
            TryClearProtectedAttributes(path);
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }
    }

    private static void TryApplyHiddenSystemAttributes(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var attributes = File.GetAttributes(path);
            File.SetAttributes(path, attributes | FileAttributes.Hidden | FileAttributes.System);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }
    }

    private static void TryClearProtectedAttributes(string path)
    {
        if (!OperatingSystem.IsWindows() || (!File.Exists(path) && !Directory.Exists(path))) return;
        try
        {
            var attributes = File.GetAttributes(path);
            attributes &= ~FileAttributes.Hidden;
            attributes &= ~FileAttributes.System;
            attributes &= ~FileAttributes.ReadOnly;
            File.SetAttributes(path, attributes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }
    }

    private sealed record PendingStateTransaction(string ConfigJson, string VaultJson);
}
