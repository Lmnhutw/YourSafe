using PasswordTool.Presentation;
using PasswordTool.Core.Models;

namespace PasswordTool.Presentation.Tests;

public sealed class UserErrorMapperTests
{
    [Fact]
    public void Map_UnknownFailure_DoesNotExposeExceptionMessage()
    {
        const string sensitiveText = "secret-value-that-must-not-escape";
        var mapper = new UserErrorMapper();

        var message = mapper.Map(new Exception(sensitiveText));

        Assert.DoesNotContain(sensitiveText, message, StringComparison.Ordinal);
        Assert.Contains("could not complete", message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(typeof(UnauthorizedAccessException), "permission")]
    [InlineData(typeof(AuthenticatorVerificationException), "Authenticator")]
    [InlineData(typeof(InvalidDataException), "format")]
    [InlineData(typeof(OperationCanceledException), "canceled")]
    public void Map_KnownFailure_ReturnsActionableCategory(Type exceptionType, string expectedText)
    {
        var exception = (Exception)Activator.CreateInstance(exceptionType)!;

        var message = new UserErrorMapper().Map(exception);

        Assert.Contains(expectedText, message, StringComparison.OrdinalIgnoreCase);
    }
}
