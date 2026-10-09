using PasswordTool.Core.Hashers;
using PasswordTool.Core.Inspection;
using PasswordTool.Core.Registry;
using PasswordTool.Core.Services;

namespace PasswordTool.Presentation.Tests;

public sealed class HashToolViewModelTests
{
    private const string Password = "correct horse battery staple";
    private readonly PasswordHasherRegistry registry = new();

    [Fact]
    public void Verification_starts_without_a_result()
    {
        var model = CreateModel();

        Assert.Empty(model.VerificationResult);
        Assert.Empty(model.ErrorMessage);
    }

    [Theory]
    [InlineData(Password, "Password verified.")]
    [InlineData("wrong password", "Password does not match.")]
    public void Verification_shows_the_outcome_of_the_submitted_request(string password, string expected)
    {
        var model = CreateModel();
        var storedHash = registry.GetHasher(PasswordHasherNames.Pbkdf2Sha256).HashPassword(Password);

        model.VerifyCommand.Execute(new HashVerificationRequest(password, storedHash));

        Assert.Equal(expected, model.VerificationResult);
        Assert.Empty(model.ErrorMessage);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "stored hash")]
    [InlineData("   ", "stored hash")]
    [InlineData(Password, "")]
    [InlineData(Password, "   ")]
    public void Missing_input_clears_the_previous_result(string? password, string? storedHash)
    {
        var model = CreateModel();
        model.VerificationResult = "Password verified.";
        var request = password is null ? null : new HashVerificationRequest(password, storedHash!);

        model.VerifyCommand.Execute(request);

        Assert.Empty(model.VerificationResult);
        Assert.Equal("Enter both a password and stored hash.", model.ErrorMessage);
    }

    [Fact]
    public void Reset_clears_the_previous_result()
    {
        var model = CreateModel();
        model.VerificationResult = "Password verified.";

        model.ResetVerification();

        Assert.Empty(model.VerificationResult);
    }

    private HashToolViewModel CreateModel() => new(registry, new PasswordHasherFactory(registry), new PasswordHashInspector(registry));
}
