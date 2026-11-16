using System.ComponentModel;
using Microsoft.UI.Xaml.Controls;

namespace PasswordTool_WinUI;

public sealed partial class MainPage
{
    private sealed record FormValidationField(
        INotifyPropertyChanged Model, string PropertyName, Control Input, TextBlock Error, Func<string> Message);

    private FormValidationField[] formValidationFields = [];

    private void SubscribeFormValidation()
    {
        if (formValidationFields.Length > 0) return;
        var settings = ViewModel.Settings;
        var backup = ViewModel.Backup;
        formValidationFields =
        [
            new(settings, nameof(settings.CurrentMasterPasswordError), CurrentMasterPassword,
                SettingsCurrentPasswordError, () => settings.CurrentMasterPasswordError),
            new(settings, nameof(settings.NewMasterPasswordError), ReplacementMasterPassword,
                SettingsNewPasswordError, () => settings.NewMasterPasswordError),
            new(settings, nameof(settings.ConfirmMasterPasswordError), ConfirmReplacementMasterPassword,
                SettingsConfirmPasswordError, () => settings.ConfirmMasterPasswordError),
            new(settings, nameof(settings.AuthenticatorMasterPasswordError), AuthenticatorResetMasterPassword,
                SettingsAuthenticatorPasswordError, () => settings.AuthenticatorMasterPasswordError),
            new(settings, nameof(settings.AuthenticatorCodeError), AuthenticatorResetCode,
                SettingsAuthenticatorCodeError, () => settings.AuthenticatorCodeError),
            new(backup, nameof(backup.BackupPasswordError), BackupPassphrase,
                BackupPasswordError, () => backup.BackupPasswordError),
            new(backup, nameof(backup.ConfirmBackupPasswordError), BackupPassphraseConfirmation,
                BackupConfirmationError, () => backup.ConfirmBackupPasswordError),
            new(backup, nameof(backup.SnapshotMasterPasswordError), SnapshotMasterPassword,
                SnapshotCurrentPasswordError, () => backup.SnapshotMasterPasswordError),
            new(backup, nameof(backup.SnapshotRestoreError), SnapshotPreviousMasterPassword,
                SnapshotRestoreError, () => backup.SnapshotRestoreError)
        ];
        settings.PropertyChanged += FormValidation_PropertyChanged;
        backup.PropertyChanged += FormValidation_PropertyChanged;
        CurrentMasterPassword.PasswordChanged += (_, _) => settings.CurrentMasterPasswordError = string.Empty;
        ReplacementMasterPassword.PasswordChanged += (_, _) => settings.NewMasterPasswordError = settings.ConfirmMasterPasswordError = string.Empty;
        ConfirmReplacementMasterPassword.PasswordChanged += (_, _) => settings.ConfirmMasterPasswordError = string.Empty;
        AuthenticatorResetMasterPassword.PasswordChanged += (_, _) => settings.AuthenticatorMasterPasswordError = string.Empty;
        AuthenticatorResetCode.CodeChanged += (_, _) => settings.AuthenticatorCodeError = string.Empty;
        SnapshotMasterPassword.PasswordChanged += (_, _) => backup.SnapshotMasterPasswordError = backup.SnapshotRestoreError = string.Empty;
        SnapshotPreviousMasterPassword.PasswordChanged += (_, _) => backup.SnapshotRestoreError = string.Empty;
        RefreshFormValidation();
    }

    private void FormValidation_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        foreach (var field in formValidationFields)
            if (ReferenceEquals(sender, field.Model) && args.PropertyName == field.PropertyName)
                InlineValidation.SetError(field.Input, field.Error, field.Message());
    }

    private void RefreshFormValidation()
    {
        foreach (var field in formValidationFields)
            InlineValidation.SetError(field.Input, field.Error, field.Message(), selectInput: false);
    }
}
