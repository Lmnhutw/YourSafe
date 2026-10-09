using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PasswordTool.Core.Services;

public sealed class EncryptionService
{
    public const int MaxEncryptedPayloadJsonBytes = 64 * 1024 * 1024;
    private const int MaxPlaintextBytes = MaxEncryptedPayloadJsonBytes / 4 * 3;
    private const int NonceSizeBytes = 12;
    private const int TagSizeBytes = 16;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    public string EncryptString(string plaintext, byte[] key) => EncryptStringCore(plaintext, key, null);
    public string EncryptString(string plaintext, byte[] key, string context) => EncryptStringCore(plaintext, key, ValidateContext(context));

    public string DecryptString(string encryptedPayloadJson, byte[] key) => DecryptStringCore(encryptedPayloadJson, key, null, 1);
    public string DecryptString(string encryptedPayloadJson, byte[] key, string context) => DecryptStringCore(encryptedPayloadJson, key, ValidateContext(context), 2);

    public string EncryptObject<T>(T value, byte[] key) => EncryptObjectCore(value, key, null, MaxPlaintextBytes);
    public string EncryptObject<T>(T value, byte[] key, string context) => EncryptObjectCore(value, key, ValidateContext(context), MaxPlaintextBytes);
    internal string EncryptObject<T>(T value, byte[] key, int maximumPlaintextBytes) => EncryptObjectCore(value, key, null, maximumPlaintextBytes);

    public T DecryptObject<T>(string encryptedPayloadJson, byte[] key) => Deserialize<T>(DecryptString(encryptedPayloadJson, key));
    public T DecryptObject<T>(string encryptedPayloadJson, byte[] key, string context) => Deserialize<T>(DecryptString(encryptedPayloadJson, key, context));

    public string EncryptKey(byte[] keyToWrap, byte[] wrappingKey, string context)
    {
        ArgumentNullException.ThrowIfNull(keyToWrap);
        if (keyToWrap.Length != 32) throw new ArgumentException("A 256-bit vault key is required.", nameof(keyToWrap));
        return EncryptBytes(keyToWrap, wrappingKey, ValidateContext(context));
    }

    public byte[] DecryptKey(string encryptedPayloadJson, byte[] wrappingKey, string context)
    {
        if (string.IsNullOrWhiteSpace(encryptedPayloadJson) || encryptedPayloadJson.Length > 4096
            || Encoding.UTF8.GetByteCount(encryptedPayloadJson) > 4096)
            throw new CryptographicException("The wrapped vault key exceeds the supported size.");
        var key = DecryptBytes(encryptedPayloadJson, wrappingKey, ValidateContext(context), 2);
        if (key.Length == 32) return key;
        CryptographicOperations.ZeroMemory(key);
        throw new CryptographicException("The wrapped vault key is invalid.");
    }

    private static string EncryptStringCore(string plaintext, byte[] key, byte[]? context)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        if (plaintext.Length > MaxPlaintextBytes || Encoding.UTF8.GetByteCount(plaintext) > MaxPlaintextBytes)
            throw new CryptographicException("The plaintext exceeds the supported encrypted payload size.");
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        try { return EncryptBytes(bytes, key, context); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static string EncryptObjectCore<T>(T value, byte[] key, byte[]? context, int maximumPlaintextBytes)
    {
        ValidateKey(key);
        if (maximumPlaintextBytes is < 1 or > MaxPlaintextBytes) throw new ArgumentOutOfRangeException(nameof(maximumPlaintextBytes));
        using var stream = new BoundedPlaintextStream(maximumPlaintextBytes);
        try
        {
            JsonSerializer.Serialize(stream, value, JsonOptions);
            return EncryptBytes(stream.GetBuffer().AsSpan(0, (int)stream.Length), key, context);
        }
        finally { CryptographicOperations.ZeroMemory(stream.GetBuffer().AsSpan(0, (int)stream.Length)); }
    }

    private static string DecryptStringCore(string encryptedPayloadJson, byte[] key, byte[]? context, int expectedVersion)
    {
        var plaintext = DecryptBytes(encryptedPayloadJson, key, context, expectedVersion);
        try { return Encoding.UTF8.GetString(plaintext); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private static string EncryptBytes(ReadOnlySpan<byte> plaintext, byte[] key, byte[]? context)
    {
        ValidateKey(key);
        if (plaintext.Length > MaxPlaintextBytes)
            throw new CryptographicException("The plaintext exceeds the supported encrypted payload size.");
        var nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSizeBytes];
        using var aes = new AesGcm(key, TagSizeBytes);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, context);
        var json = JsonSerializer.Serialize(new EncryptedPayload
        {
            Version = context is null ? 1 : 2,
            Algorithm = "AES-256-GCM",
            NonceBase64 = Convert.ToBase64String(nonce),
            TagBase64 = Convert.ToBase64String(tag),
            CipherTextBase64 = Convert.ToBase64String(ciphertext)
        }, JsonOptions);
        ValidatePayloadSize(json);
        return json;
    }

    private static byte[] DecryptBytes(string encryptedPayloadJson, byte[] key, byte[]? context, int expectedVersion)
    {
        ValidateKey(key);
        ValidatePayloadSize(encryptedPayloadJson);
        EncryptedPayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<EncryptedPayload>(encryptedPayloadJson, JsonOptions)
                ?? throw new CryptographicException("The encrypted payload is empty or invalid.");
        }
        catch (JsonException ex) { throw new CryptographicException("The encrypted payload is malformed.", ex); }
        if (payload.Version != expectedVersion || !string.Equals(payload.Algorithm, "AES-256-GCM", StringComparison.Ordinal))
            throw new CryptographicException("The encrypted payload format is not supported.");

        if (payload.NonceBase64 is not { Length: 16 } || payload.TagBase64 is not { Length: 24 }
            || payload.CipherTextBase64 is null)
            throw new CryptographicException("The encrypted payload is malformed.");
        var nonce = Convert.FromBase64String(payload.NonceBase64);
        var tag = Convert.FromBase64String(payload.TagBase64);
        if (nonce.Length != NonceSizeBytes || tag.Length != TagSizeBytes)
            throw new CryptographicException("The encrypted payload is malformed.");

        var ciphertext = Convert.FromBase64String(payload.CipherTextBase64);
        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, TagSizeBytes);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, context);
            return plaintext;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw;
        }
    }

    private static void ValidatePayloadSize(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxEncryptedPayloadJsonBytes
            || Encoding.UTF8.GetByteCount(json) > MaxEncryptedPayloadJsonBytes)
            throw new CryptographicException("The encrypted payload is empty or exceeds the 64 MiB size limit.");
    }

    private static byte[] ValidateContext(string context)
    {
        if (string.IsNullOrWhiteSpace(context)) throw new ArgumentException("An encryption context is required.", nameof(context));
        return Encoding.UTF8.GetBytes(context);
    }

    private static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, JsonOptions)
        ?? throw new InvalidOperationException("The encrypted payload did not contain valid JSON.");

    private static void ValidateKey(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != 32) throw new ArgumentException("AES-256-GCM requires a 32-byte key.", nameof(key));
    }

    private sealed class EncryptedPayload
    {
        public int Version { get; set; }
        public string Algorithm { get; set; } = string.Empty;
        public string NonceBase64 { get; set; } = string.Empty;
        public string TagBase64 { get; set; } = string.Empty;
        public string CipherTextBase64 { get; set; } = string.Empty;
    }

    private sealed class BoundedPlaintextStream(int maximumBytes) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            ValidateWrite(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            ValidateWrite(buffer.Length);
            base.Write(buffer);
        }

        private void ValidateWrite(int count)
        {
            if (count > maximumBytes - Length)
                throw new InvalidDataException("The serialized plaintext exceeds the supported size limit.");
        }
    }
}
