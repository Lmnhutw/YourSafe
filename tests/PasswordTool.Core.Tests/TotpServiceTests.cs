using System.Text;
using OtpNet;
using PasswordTool.Core.Models;
using PasswordTool.Core.Services;

namespace PasswordTool.Core.Tests;

public sealed class TotpServiceTests
{
    [Fact]
    public void Website_totp_accepts_base32_and_otpauth_uri()
    {
        var service = new TotpService();
        const string secret = "JBSWY3DPEHPK3PXP";

        Assert.True(service.TryNormalizeWebsiteSecret(secret.ToLowerInvariant(), out var normalized));
        Assert.Equal(secret, normalized);
        Assert.True(service.TryNormalizeWebsiteSecret(
            "otpauth://totp/Example:test?secret=JBSWY3DPEHPK3PXP&issuer=Example",
            out normalized));
        Assert.Equal(secret, normalized);

        var result = service.GetCurrentCode(secret, DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
        Assert.Matches("^[0-9]{6}$", result.Code);
        Assert.InRange(result.SecondsRemaining, 1, 30);
    }

    [Fact]
    public void Website_totp_rejects_malformed_secrets()
    {
        Assert.False(new TotpService().TryNormalizeWebsiteSecret("not base32!", out _));
    }

    [Fact]
    public void Parser_normalizes_whitespace_hyphens_padding_and_complete_uri_configuration()
    {
        var service = new TotpService();
        Assert.True(service.TryParseWebsiteConfiguration(" jbsw-y3dp\t ehpk\r\n3pxp ", out var manual, out var error), error);
        Assert.Equal(new TotpConfiguration("JBSWY3DPEHPK3PXP"), manual);
        var padded = Base32Encoding.ToString(new byte[11]).TrimEnd('=') + "======";
        Assert.True(service.TryParseWebsiteConfiguration(padded, out var parsed, out error), error);
        Assert.DoesNotContain("=", parsed.Secret);

        const string uri = "otpauth://totp/Example%20Site%3Aperson%40example.com?secret=JBSWY3DPEHPK3PXP&issuer=Example+Site&algorithm=sha512&digits=8&period=60&image=ignored";
        Assert.True(service.TryParseWebsiteConfiguration(uri, out parsed, out error), error);
        Assert.Equal(new TotpConfiguration("JBSWY3DPEHPK3PXP", "Example Site", "person@example.com", TotpAlgorithm.Sha512, 8, 60), parsed);
        Assert.False(service.TryNormalizeWebsiteSecret(uri, out _));
        Assert.DoesNotContain(parsed.Secret, parsed.ToString());
        Assert.DoesNotContain("otpauth", parsed.ToString());
    }

    [Theory]
    [InlineData("A")]
    [InlineData("JBSWY3DP")]
    [InlineData("JBSWY3DPEHPK3PXP=")]
    [InlineData("JBSWY3DP=EHPK3PXP")]
    [InlineData("AAAAAAAAAAAAAAAAAB")]
    [InlineData("otpauth://hotp/Example?secret=JBSWY3DPEHPK3PXP")]
    [InlineData("otpauth://totp/Example?issuer=Example")]
    [InlineData("otpauth://totp/%ZZ?secret=JBSWY3DPEHPK3PXP")]
    [InlineData("otpauth://totp/%FF?secret=JBSWY3DPEHPK3PXP")]
    [InlineData("otpauth://totp/User?issuer=% A&secret=JBSWY3DPEHPK3PXP")]
    [InlineData("otpauth://totp/User?issuer=%\tA&secret=JBSWY3DPEHPK3PXP")]
    [InlineData("otpauth://totp/User?issuer=%0 &secret=JBSWY3DPEHPK3PXP")]
    [InlineData("otpauth://totp/U\nser?secret=JBSWY3DPEHPK3PXP")]
    [InlineData("otpauth://totp/Example?secret=JBSWY3DPEHPK3PXP&SECRET=JBSWY3DPEHPK3PXP")]
    [InlineData("otpauth://totp/Example?secret=JBSWY3DPEHPK3PXP&digits=6&%64igits=8")]
    [InlineData("otpauth://totp/Example?secret=JBSWY3DPEHPK3PXP&issuer=Example&issuer=Example")]
    [InlineData("otpauth://totp/Example?secret=JBSWY3DPEHPK3PXP&algorithm=SHA1&algorithm=SHA256")]
    [InlineData("otpauth://totp/Example?secret=JBSWY3DPEHPK3PXP&period=30&period=60")]
    [InlineData("otpauth://totp/Example:user?secret=JBSWY3DPEHPK3PXP&issuer=Other")]
    [InlineData("otpauth://user@totp/Example?secret=JBSWY3DPEHPK3PXP")]
    [InlineData("otpauth://totp:123/Example?secret=JBSWY3DPEHPK3PXP")]
    [InlineData("otpauth://totp/Example?secret=JBSWY3DPEHPK3PXP#fragment")]
    [InlineData("otpauth://totp/Example?secret=JBSWY3DPEHPK3PXP&algorithm=MD5")]
    [InlineData("otpauth://totp/Example?secret=JBSWY3DPEHPK3PXP&algorithm=")]
    [InlineData("otpauth://totp/Example?secret=JBSWY3DPEHPK3PXP&digits=7")]
    [InlineData("otpauth://totp/Example?secret=JBSWY3DPEHPK3PXP&period=0")]
    [InlineData("otpauth://totp/Example?secret=JBSWY3DPEHPK3PXP&period=-30")]
    [InlineData("otpauth://totp/Example?secret=JBSWY3DPEHPK3PXP&period=2147483648")]
    [InlineData("otpauth://totp/Example?secret=JBSWY3DPEHPK3PXP&period=1.5")]
    public void Parser_rejects_invalid_input_without_echoing_sensitive_input(string value)
    {
        Assert.False(new TotpService().TryParseWebsiteConfiguration(value, out var configuration, out var error));
        Assert.Null(configuration);
        Assert.NotEmpty(error);
        Assert.DoesNotContain("JBSWY3DPEHPK3PXP", error);
        Assert.DoesNotContain(value, error, StringComparison.Ordinal);
    }

    [Fact]
    public void Parser_enforces_bounds_and_accepts_positive_int32_period()
    {
        var service = new TotpService();
        Assert.True(service.TryParseWebsiteConfiguration(new string('A', 512), out _, out _));
        Assert.False(service.TryParseWebsiteConfiguration(new string('A', 520), out _, out _));
        Assert.False(service.TryParseWebsiteConfiguration("otpauth://totp/" + new string('A', 4096), out _, out _));
        Assert.True(service.TryParseWebsiteConfiguration("otpauth://totp/User?secret=JBSWY3DPEHPK3PXP&period=2147483647", out var parsed, out _));
        Assert.Equal(int.MaxValue, parsed.Period);
    }

    // RFC 6238 Appendix B: https://www.rfc-editor.org/rfc/rfc6238#appendix-B
    [Theory]
    [InlineData(59L, TotpAlgorithm.Sha1, "94287082")]
    [InlineData(59L, TotpAlgorithm.Sha256, "46119246")]
    [InlineData(59L, TotpAlgorithm.Sha512, "90693936")]
    [InlineData(1111111109L, TotpAlgorithm.Sha1, "07081804")]
    [InlineData(1111111109L, TotpAlgorithm.Sha256, "68084774")]
    [InlineData(1111111109L, TotpAlgorithm.Sha512, "25091201")]
    [InlineData(1111111111L, TotpAlgorithm.Sha1, "14050471")]
    [InlineData(1111111111L, TotpAlgorithm.Sha256, "67062674")]
    [InlineData(1111111111L, TotpAlgorithm.Sha512, "99943326")]
    [InlineData(1234567890L, TotpAlgorithm.Sha1, "89005924")]
    [InlineData(1234567890L, TotpAlgorithm.Sha256, "91819424")]
    [InlineData(1234567890L, TotpAlgorithm.Sha512, "93441116")]
    [InlineData(2000000000L, TotpAlgorithm.Sha1, "69279037")]
    [InlineData(2000000000L, TotpAlgorithm.Sha256, "90698825")]
    [InlineData(2000000000L, TotpAlgorithm.Sha512, "38618901")]
    [InlineData(20000000000L, TotpAlgorithm.Sha1, "65353130")]
    [InlineData(20000000000L, TotpAlgorithm.Sha256, "77737706")]
    [InlineData(20000000000L, TotpAlgorithm.Sha512, "47863826")]
    public void Generator_matches_rfc_vectors_for_all_algorithms(long seconds, TotpAlgorithm algorithm, string expected)
    {
        var length = algorithm switch { TotpAlgorithm.Sha1 => 20, TotpAlgorithm.Sha256 => 32, _ => 64 };
        var ascii = new string(Enumerable.Range(0, length).Select(index => "1234567890"[index % 10]).ToArray());
        var configuration = new TotpConfiguration(Base32Encoding.ToString(Encoding.ASCII.GetBytes(ascii)), "Issuer", "Account", algorithm, 8);
        var service = new TotpService();
        var eightDigits = service.GetCurrentCode(configuration, DateTimeOffset.FromUnixTimeSeconds(seconds));
        Assert.Equal(expected, eightDigits.Code);
        Assert.Equal(expected[^6..], service.GetCurrentCode(configuration with { Digits = 6 }, DateTimeOffset.FromUnixTimeSeconds(seconds)).Code);
        Assert.Equal("Issuer", eightDigits.Issuer);
        Assert.Equal("Account", eightDigits.AccountName);
        Assert.DoesNotContain(eightDigits.Code, eightDigits.ToString());
    }

    [Theory]
    [InlineData(20)]
    [InlineData(30)]
    [InlineData(60)]
    public void Generator_rotates_by_utc_window_with_nondefault_period_and_clock_jumps(int period)
    {
        var service = new TotpService();
        Assert.True(service.TryParseWebsiteConfiguration($"otpauth://totp/Account?secret=JBSWY3DPEHPK3PXP&period={period}", out var configuration, out var error), error);
        var beforeTime = DateTimeOffset.FromUnixTimeMilliseconds(period * 1000 - 1);
        var afterTime = DateTimeOffset.FromUnixTimeSeconds(period);
        var before = service.GetCurrentCode(configuration!, beforeTime);
        var after = service.GetCurrentCode(configuration!, afterTime);
        Assert.Equal(1, before.SecondsRemaining);
        Assert.Equal(afterTime, before.ExpiresAtUtc);
        Assert.Equal(period, after.SecondsRemaining);
        Assert.Equal(afterTime.AddSeconds(period), after.ExpiresAtUtc);
        Assert.Equal(period, after.PeriodSeconds);
        Assert.NotEqual(before.Code, after.Code);
        Assert.Equal(before, service.GetCurrentCode(configuration!, beforeTime));
        Assert.Equal(after, service.GetCurrentCode(configuration!, afterTime.ToOffset(TimeSpan.FromHours(7))));
    }
}
