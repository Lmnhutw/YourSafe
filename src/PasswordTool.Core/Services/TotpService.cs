using System.Security.Cryptography;
using System.Globalization;
using System.Text;
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
        if (!TryParseWebsiteConfiguration(value, out var configuration, out _)
            || configuration.Algorithm != TotpAlgorithm.Sha1 || configuration.Digits != 6 || configuration.Period != 30)
            return false;
        normalizedSecret = configuration.Secret;
        return true;
    }

    public bool TryParseWebsiteConfiguration(string value, out TotpConfiguration configuration, out string error)
    {
        configuration = null!;
        error = string.Empty;
        try
        {
            var candidate = value?.Trim() ?? string.Empty;
            if (!candidate.StartsWith("otpauth:", StringComparison.OrdinalIgnoreCase))
            {
                configuration = new TotpConfiguration(NormalizeSecret(candidate));
                return true;
            }

            if (candidate.Length > 4_096 || candidate.Contains('\\') || candidate.Any(char.IsControl))
                throw new FormatException("The otpauth URI is invalid or too long.");
            ValidatePercentEncoding(candidate);
            if (!candidate.StartsWith("otpauth://totp/", StringComparison.OrdinalIgnoreCase)
                || !Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
                || !string.Equals(uri.Host, "totp", StringComparison.OrdinalIgnoreCase)
                || uri.UserInfo.Length != 0 || uri.Port != -1 || uri.Fragment.Length != 0)
                throw new FormatException("Only an otpauth TOTP URI without user information, port, or fragment is supported.");

            // Read the original label because Uri canonicalization can remove dot segments.
            var labelEnd = candidate.IndexOf('?', "otpauth://totp/".Length);
            var label = Uri.UnescapeDataString(candidate["otpauth://totp/".Length..(labelEnd < 0 ? candidate.Length : labelEnd)]);
            var parts = label.Split(':', 2);
            var labelIssuer = parts.Length == 2 ? parts[0].Trim() : string.Empty;
            var account = parts[^1].Trim();
            if (account.Length == 0 || account.Contains(':'))
                throw new FormatException("The otpauth account label is invalid.");

            var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = entry.Split('=', 2);
                var name = DecodeQueryValue(pair[0]);
                if (name.ToLowerInvariant() is not ("secret" or "issuer" or "algorithm" or "digits" or "period"))
                    continue;
                if (pair.Length != 2 || !parameters.TryAdd(name, DecodeQueryValue(pair[1])))
                    throw new FormatException("The otpauth URI contains a missing or repeated TOTP parameter.");
            }
            if (!parameters.TryGetValue("secret", out var secret))
                throw new FormatException("The otpauth URI requires a secret.");

            var issuer = parameters.GetValueOrDefault("issuer", labelIssuer).Trim();
            if (labelIssuer.Length != 0 && parameters.ContainsKey("issuer")
                && !string.Equals(labelIssuer, issuer, StringComparison.Ordinal))
                throw new FormatException("The otpauth label and issuer do not match.");

            var algorithm = parameters.GetValueOrDefault("algorithm", "SHA1").ToUpperInvariant() switch
            {
                "SHA1" => TotpAlgorithm.Sha1,
                "SHA256" => TotpAlgorithm.Sha256,
                "SHA512" => TotpAlgorithm.Sha512,
                _ => throw new FormatException("The TOTP algorithm must be SHA1, SHA256, or SHA512.")
            };
            var digits = ParsePositiveInt(parameters.GetValueOrDefault("digits", "6"), "digits");
            if (digits is not (6 or 8)) throw new FormatException("TOTP codes must have 6 or 8 digits.");
            var period = ParsePositiveInt(parameters.GetValueOrDefault("period", "30"), "period");
            configuration = NormalizeConfiguration(new TotpConfiguration(secret, issuer, account, algorithm, digits, period));
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            error = ex is FormatException ? ex.Message : "The TOTP configuration is invalid.";
            return false;
        }
    }

    public TotpConfiguration NormalizeConfiguration(TotpConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!Enum.IsDefined(configuration.Algorithm) || configuration.Digits is not (6 or 8) || configuration.Period <= 0
            || configuration.Issuer is null || configuration.AccountName is null
            || configuration.Issuer.Length > 4_096 || configuration.AccountName.Length > 4_096)
            throw new ArgumentException("The TOTP configuration is invalid.", nameof(configuration));
        return configuration with
        {
            Secret = NormalizeSecret(configuration.Secret),
            Issuer = configuration.Issuer.Trim(),
            AccountName = configuration.AccountName.Trim()
        };
    }

    public TotpCodeResult GetCurrentCode(string secretBase32, DateTimeOffset? timestamp = null)
    {
        if (!TryParseWebsiteConfiguration(secretBase32, out var configuration, out _))
        {
            throw new ArgumentException("The website TOTP secret is invalid.", nameof(secretBase32));
        }

        return GetCurrentCode(configuration, timestamp);
    }

    public TotpCodeResult GetCurrentCode(TotpConfiguration configuration, DateTimeOffset? timestamp = null)
    {
        configuration = NormalizeConfiguration(configuration);
        var now = (timestamp ?? DateTimeOffset.UtcNow).ToUniversalTime();
        if (now.ToUnixTimeSeconds() < 0) throw new ArgumentOutOfRangeException(nameof(timestamp), "TOTP requires a time after the Unix epoch.");
        var secretBytes = Base32Encoding.ToBytes(configuration.Secret);
        try
        {
            var mode = configuration.Algorithm switch
            {
                TotpAlgorithm.Sha1 => OtpHashMode.Sha1,
                TotpAlgorithm.Sha256 => OtpHashMode.Sha256,
                TotpAlgorithm.Sha512 => OtpHashMode.Sha512,
                _ => throw new ArgumentException("The TOTP algorithm is invalid.")
            };
            var code = new Totp(secretBytes, configuration.Period, mode, configuration.Digits).ComputeTotp(now.UtcDateTime);
            var expires = DateTimeOffset.FromUnixTimeSeconds(checked((now.ToUnixTimeSeconds() / configuration.Period + 1) * configuration.Period));
            return new TotpCodeResult(code, (int)Math.Ceiling((expires - now).TotalSeconds), configuration.Period,
                expires, configuration.Issuer, configuration.AccountName);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secretBytes);
        }
    }

    private static int ParsePositiveInt(string value, string parameter)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0)
            throw new FormatException($"The TOTP {parameter} must be a positive 32-bit integer.");
        return number;
    }

    private static string DecodeQueryValue(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));

    private static void ValidatePercentEncoding(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '%') continue;
            var bytes = new List<byte>();
            do
            {
                if (index + 2 >= value.Length || !byte.TryParse(value.AsSpan(index + 1, 2), NumberStyles.AllowHexSpecifier,
                        CultureInfo.InvariantCulture, out var decoded))
                    throw new FormatException("The otpauth URI contains invalid percent encoding.");
                bytes.Add(decoded);
                index += 3;
            } while (index < value.Length && value[index] == '%');
            try { _ = new UTF8Encoding(false, true).GetString(bytes.ToArray()); }
            catch (DecoderFallbackException) { throw new FormatException("The otpauth URI contains invalid percent encoding."); }
            index--;
        }
    }

    private static string NormalizeSecret(string secretBase32)
    {
        if (string.IsNullOrWhiteSpace(secretBase32))
        {
            throw new ArgumentException("TOTP secret is required.", nameof(secretBase32));
        }

        var normalized = new string(secretBase32.Where(character => !char.IsWhiteSpace(character) && character != '-').ToArray()).ToUpperInvariant();
        var unpadded = normalized.TrimEnd('=');
        var expectedPadding = (unpadded.Length % 8) switch { 0 => 0, 2 => 6, 4 => 4, 5 => 3, 7 => 1, _ => -1 };
        if (unpadded.Length > 512 || expectedPadding < 0
            || unpadded.Any(character => character is not (>= 'A' and <= 'Z') and not (>= '2' and <= '7'))
            || normalized.Length != unpadded.Length && (normalized.Length % 8 != 0 || normalized.Length - unpadded.Length != expectedPadding))
            throw new FormatException("The TOTP secret has invalid Base32 characters, length, or padding.");
        var bytes = Base32Encoding.ToBytes(unpadded);
        try
        {
            if (bytes.Length < 10 || !string.Equals(Base32Encoding.ToString(bytes).TrimEnd('='), unpadded, StringComparison.Ordinal))
                throw new FormatException("The TOTP secret must contain at least 10 bytes and valid Base32 padding bits.");
            return unpadded;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    [GeneratedRegex("^\\d{6}$")]
    private static partial Regex SixDigitCodeRegex();
}
