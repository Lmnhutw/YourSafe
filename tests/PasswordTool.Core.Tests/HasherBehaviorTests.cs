using PasswordTool.Core.Hashers;
using PasswordTool.Core.Inspection;
using PasswordTool.Core.Models;
using PasswordTool.Core.Registry;
using PasswordTool.Core.Utilities;

namespace PasswordTool.Core.Tests;

public sealed class HasherBehaviorTests
{
    [Fact]
    public void Argon2id_Is_The_Only_Default_Recommendation()
    {
        var defaultHasher = new PasswordHasherRegistry().GetAvailableHashers().Single(x => x.IsDefaultRecommendation);

        Assert.Equal(PasswordHasherNames.Argon2id, defaultHasher.AlgorithmName);
        Assert.Equal(PasswordHasherSecurityCategory.ProductionSafe, defaultHasher.SecurityCategory);
    }

    [Theory]
    [InlineData(PasswordHasherNames.Argon2id)]
    [InlineData(PasswordHasherNames.Bcrypt)]
    [InlineData(PasswordHasherNames.Pbkdf2Sha256)]
    [InlineData(PasswordHasherNames.Pbkdf2Sha512)]
    [InlineData(PasswordHasherNames.Scrypt)]
    [InlineData(PasswordHasherNames.AspNetCoreIdentity)]
    [InlineData(PasswordHasherNames.Md5)]
    [InlineData(PasswordHasherNames.Sha1)]
    [InlineData(PasswordHasherNames.Sha256Unsalted)]
    [InlineData(PasswordHasherNames.Sha512Unsalted)]
    [InlineData(PasswordHasherNames.Sha256Salted)]
    [InlineData(PasswordHasherNames.Sha512Salted)]
    public void Hashers_Can_Hash_Verify_Reject_And_Inspect(string algorithmName)
    {
        var registry = new PasswordHasherRegistry();
        var hasher = registry.GetHasher(algorithmName);

        var hash = hasher.HashPassword("correct horse battery staple");
        var info = hasher.InspectHash(hash);

        Assert.True(hasher.VerifyPassword("correct horse battery staple", hash));
        Assert.False(hasher.VerifyPassword("wrong password", hash));
        Assert.False(string.IsNullOrWhiteSpace(hash));
        Assert.False(string.IsNullOrWhiteSpace(info.AlgorithmName));
        Assert.False(string.IsNullOrWhiteSpace(info.Notes));
    }

    [Fact]
    public void Inspector_Returns_Unknown_For_Invalid_Hash()
    {
        var inspector = new PasswordHashInspector();

        var info = inspector.Inspect("not-a-real-stored-hash");

        Assert.Equal("Unknown", info.AlgorithmName);
        Assert.False(info.IsSecureForPasswordStorage);
    }

    [Theory]
    [InlineData(PasswordHasherNames.Pbkdf2Sha256, "PBKDF2-SHA256$v=1$iter=210000$salt=")]
    [InlineData(PasswordHasherNames.Pbkdf2Sha512, "PBKDF2-SHA512$v=1$iter=210000$salt=")]
    [InlineData(PasswordHasherNames.Scrypt, "SCRYPT$v=1$N=16384$r=8$p=1$salt=")]
    public void Secure_Stored_Format_Uses_Requested_Base64_Fields(string algorithmName, string prefix)
    {
        var hasher = new PasswordHasherRegistry().GetHasher(algorithmName);

        var storedHash = hasher.HashPassword("correct horse battery staple");

        Assert.StartsWith(prefix, storedHash);
        AssertBase64Field(storedHash, "salt");
        AssertBase64Field(storedHash, "hash");
    }

    [Theory]
    [InlineData(PasswordHasherNames.Md5, "LEGACY-MD5", false)]
    [InlineData(PasswordHasherNames.Sha1, "LEGACY-SHA1", false)]
    [InlineData(PasswordHasherNames.Sha256Unsalted, "LEGACY-SHA256", false)]
    [InlineData(PasswordHasherNames.Sha512Unsalted, "LEGACY-SHA512", false)]
    [InlineData(PasswordHasherNames.Sha256Salted, "LEGACY-SHA256-SALTED", true)]
    [InlineData(PasswordHasherNames.Sha512Salted, "LEGACY-SHA512-SALTED", true)]
    public void Legacy_Stored_Format_Uses_Base64_Hash_And_Salt_When_Present(
        string algorithmName,
        string formatName,
        bool hasSalt)
    {
        var hasher = new PasswordHasherRegistry().GetHasher(algorithmName);

        var storedHash = hasher.HashPassword("correct horse battery staple");

        Assert.StartsWith(formatName, storedHash);
        if (hasSalt)
        {
            AssertBase64Field(storedHash, "salt");
        }

        AssertBase64Field(storedHash, "hash");
    }

    [Fact]
    public void Inspector_Returns_User_Friendly_Invalid_Info_For_Bad_Base64()
    {
        var inspector = new PasswordHashInspector();

        var info = inspector.Inspect("PBKDF2-SHA256$v=1$iter=210000$salt=not-valid-base64$hash=also-not-valid");

        Assert.Equal("PBKDF2-SHA256", info.AlgorithmName);
        Assert.False(info.IsSecureForPasswordStorage);
        Assert.Contains("Base64", info.Notes);
    }

    [Fact]
    public void Scrypt_Matches_Rfc7914_Test_Vector()
    {
        var actual = ScryptKeyDerivation.DeriveKey(
            "password"u8.ToArray(),
            "NaCl"u8.ToArray(),
            cost: 1024,
            blockSize: 8,
            parallelization: 16,
            outputBytes: 64);

        const string expected =
            "FDBABE1C9D3472007856E7190D01E9FE" +
            "7C6AD7CBC8237830E77376634B373162" +
            "2EAF30D92E22A3886FF109279D9830DA" +
            "C727AFB94A83EE6D8360CBDFA2CC0640";

        Assert.Equal(expected, Convert.ToHexString(actual));
    }

    private static void AssertBase64Field(string storedHash, string key)
    {
        Assert.True(HashParser.TryParseKeyValueFormat(storedHash, out _, out var values));
        Assert.True(values.TryGetValue(key, out var encoded), $"Missing {key} field.");
        Assert.NotEmpty(Convert.FromBase64String(encoded));
    }
}
