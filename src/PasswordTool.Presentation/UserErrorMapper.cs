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
        InvalidDataException or FormatException => "The selected data is not a supported YourSafe format.",
        CryptographicException => "YourSafe could not authenticate or decrypt the protected data.",
        ArgumentException => "One or more values are invalid.",
        InvalidOperationException => "The operation is not available in the current vault state.",
        _ => "YourSafe could not complete the operation. No vault data was changed."
    };
}
