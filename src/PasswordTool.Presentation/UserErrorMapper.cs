using System.Security.Cryptography;
using PasswordTool.Core.Models;

namespace PasswordTool.Presentation;

public interface IUserErrorMapper
{
    string Map(Exception exception);
}

/// <summary>Maps operation failures without exposing exception details or sensitive input.</summary>
public sealed class UserErrorMapper : IUserErrorMapper
{
    public string Map(Exception exception) => exception switch
    {
        BackupOperationException backup => backup.Message,
        OperationCanceledException => "The operation was canceled.",
        UnauthorizedAccessException => "YourSafe does not have permission to access the selected location.",
        FileNotFoundException => "The selected file could not be found.",
        IOException => "YourSafe could not complete the file operation.",
        InvalidDataException or FormatException => "The selected file's format or contents are not supported.",
        CryptographicException => "YourSafe could not verify or open the encrypted data.",
        ArgumentException => "Check the details you entered and try again.",
        InvalidOperationException => "This action is unavailable right now. Check that your vault is unlocked and try again.",
        _ => "YourSafe could not complete this action. Check your saved items before trying again."
    };
}
