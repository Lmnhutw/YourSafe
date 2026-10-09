namespace PasswordTool.Core.Models;

public sealed class TrustedUnlockToken
{
    public int Version { get; set; } = 3;
    public string Protection { get; set; } = "Windows-DPAPI-CurrentUser";
    public string ConfigFingerprintBase64 { get; set; } = string.Empty;
    public string ProtectedEncryptionKeyBase64 { get; set; } = string.Empty;
    public string ProtectedAuthenticatorSecretBase64 { get; set; } = string.Empty;
    public string ProtectedVaultKeyBase64 { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; } = DateTimeOffset.UtcNow;
}
