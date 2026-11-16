namespace PasswordTool.Core.Models;

public sealed class AuthenticatorVerificationException()
    : UnauthorizedAccessException("The Authenticator code is incorrect or has expired. Enter the current 6-digit code.")
{
}
