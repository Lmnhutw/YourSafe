using System.Security.Cryptography;
using System.Text.RegularExpressions;
using OtpNet;
using PasswordTool.Core.Models;

namespace PasswordTool.Core.Services;

public sealed partial class TotpService
{
    private const int SecretSizeBytes = 20;

    public string GenerateSecret()
    {
        var secretBytes = RandomNumberGenerator.GetBytes(SecretSizeBytes);
        try { return Base32Encoding.ToString(secretBytes); }
        finally { CryptographicOperations.ZeroMemory(secretBytes); }
    }

    public string CreateOtpAuthUri(string secretBase32, string issuer = "YourSafe", string? accountName = null)
    {
        var normalizedSecret = NormalizeSecret(secretBase32);
        var normalizedIssuer = string.IsNullOrWhiteSpace(issuer) ? "YourSafe" : issuer.Trim();
        var normalizedAccount = string.IsNullOrWhiteSpace(accountName)
            ? Environment.UserName
            : accountName.Trim();

        var label = $"{Uri.EscapeDataString(normalizedIssuer)}:{Uri.EscapeDataString(normalizedAccount)}";
        return $"otpauth://totp/{label}?secret={Uri.EscapeDataString(normalizedSecret)}&issuer={Uri.EscapeDataString(normalizedIssuer)}&digits=6&period=30";
    }

    public bool VerifyCode(string secretBase32, string code)
    {
        var normalizedCode = code?.Trim() ?? string.Empty;
        if (!SixDigitCodeRegex().IsMatch(normalizedCode))
        {
            return false;
        }

        try
        {
            var secretBytes = Base32Encoding.ToBytes(NormalizeSecret(secretBase32));
            try
            {
                var totp = new Totp(secretBytes);
                return totp.VerifyTotp(
                    normalizedCode,
                    out _,
                    new VerificationWindow(previous: 1, future: 1));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(secretBytes);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            return false;
        }
    }

    public bool VerifyCode(byte[] secretBytes, string code)
    {
        ArgumentNullException.ThrowIfNull(secretBytes);
        var normalizedCode = code?.Trim() ?? string.Empty;
        if (secretBytes.Length < 10 || !SixDigitCodeRegex().IsMatch(normalizedCode)) return false;
        var totp = new Totp(secretBytes);
        return totp.VerifyTotp(normalizedCode, out _, new VerificationWindow(previous: 1, future: 1));
    }
    public bool IsSecretValid(string secretBase32)
    {
        try
        {
            var secretBytes = Base32Encoding.ToBytes(NormalizeSecret(secretBase32));
            try { return secretBytes.Length >= 10; }
            finally { CryptographicOperations.ZeroMemory(secretBytes); }
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            return false;
        }
    }

    public bool TryNormalizeWebsiteSecret(string value, out string normalizedSecret)
    {
        normalizedSecret = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            var candidate = value.Trim();
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
                && string.Equals(uri.Scheme, "otpauth", StringComparison.OrdinalIgnoreCase))
            {
                candidate = GetQueryParameter(uri.Query, "secret")
                    ?? throw new FormatException("The otpauth URI has no secret.");
            }

            normalizedSecret = NormalizeSecret(candidate);
            var secretBytes = Base32Encoding.ToBytes(normalizedSecret);
            try
            {
                if (secretBytes.Length < 10)
                {
                    normalizedSecret = string.Empty;
                    return false;
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(secretBytes);
            }
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or UriFormatException)
        {
            normalizedSecret = string.Empty;
            return false;
        }
    }

    public TotpCodeResult GetCurrentCode(string secretBase32, DateTimeOffset? timestamp = null)
    {
        if (!TryNormalizeWebsiteSecret(secretBase32, out var normalizedSecret))
        {
            throw new ArgumentException("The website TOTP secret is invalid.", nameof(secretBase32));
        }

        var now = timestamp ?? DateTimeOffset.UtcNow;
        var secretBytes = Base32Encoding.ToBytes(normalizedSecret);
        try
        {
            var code = new Totp(secretBytes).ComputeTotp(now.UtcDateTime);
            var elapsed = (int)(now.ToUnixTimeSeconds() % 30);
            return new TotpCodeResult(code, 30 - elapsed);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secretBytes);
        }
    }

    private static string? GetQueryParameter(string query, string name)
    {
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2 && string.Equals(Uri.UnescapeDataString(pair[0]), name, StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(pair[1].Replace('+', ' '));
            }
        }
        return null;
    }

    private static string NormalizeSecret(string secretBase32)
    {
        if (string.IsNullOrWhiteSpace(secretBase32))
        {
            throw new ArgumentException("TOTP secret is required.", nameof(secretBase32));
        }

        return secretBase32.Replace(" ", string.Empty, StringComparison.Ordinal)
            .Trim()
            .ToUpperInvariant();
    }

    [GeneratedRegex("^\\d{6}$")]
    private static partial Regex SixDigitCodeRegex();
}
