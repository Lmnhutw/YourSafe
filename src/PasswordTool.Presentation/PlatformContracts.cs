namespace PasswordTool.Presentation;

public interface INavigationService
{
    AppRoute CurrentRoute { get; }
    void Navigate(AppRoute route);
    bool TryGoBack();
    void ResetForLock();
}

public interface IUserDialogService
{
    Task<string?> PromptMasterPasswordAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    Task<(string Confirmation, string TotpCode)?> ConfirmGroupDeletionAsync(string groupName, CancellationToken cancellationToken = default);
    Task<bool> ConfirmAsync(string title, string message, string confirmText, CancellationToken cancellationToken = default);
    Task ShowErrorAsync(string title, string message, CancellationToken cancellationToken = default);
    Task<string?> PromptTotpAsync(string title, string message, CancellationToken cancellationToken = default);
    Task<string?> PromptBackupPassphraseAsync(string title, string message, CancellationToken cancellationToken = default);
    Task ShowSecretAsync(string title, string value, bool multiline, CancellationToken cancellationToken = default);
}

public interface IFilePickerService
{
    Task<string?> PickOpenPathAsync(CancellationToken cancellationToken = default);
    Task<string?> PickSavePathAsync(string suggestedFileName, CancellationToken cancellationToken = default);
}

public interface ISensitiveClipboardService
{
    Task CopyAsync(string value, CancellationToken cancellationToken = default);
    Task CopyAsync(string value, Func<bool> canCopy, CancellationToken cancellationToken = default, bool clearAutomatically = true)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!canCopy()) throw new OperationCanceledException();
        return CopyAsync(value, cancellationToken);
    }
    Task ClearOwnedValueAsync(CancellationToken cancellationToken = default);
}

public interface ISystemLockMonitor
{
    event EventHandler? LockRequired;
    void Start();
    void Stop();
}
