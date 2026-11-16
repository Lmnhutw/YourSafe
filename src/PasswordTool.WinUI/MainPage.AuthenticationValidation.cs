using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PasswordTool.Core.Services;

namespace PasswordTool_WinUI;

public sealed partial class MainPage
{
    private TextBlock masterPasswordError = null!;
    private TextBlock newMasterPasswordError = null!;
    private TextBlock confirmMasterPasswordError = null!;
    private TextBlock authenticatorSetupError = null!;
    private TextBlock recoveryPasswordError = null!;

    private void InitializeAuthenticationValidation()
    {
        masterPasswordError = TxtMasterPasswordError;
        newMasterPasswordError = TxtNewMasterPasswordError;
        confirmMasterPasswordError = TxtConfirmMasterPasswordError;
        authenticatorSetupError = TxtAuthenticatorSetupError;
        recoveryPasswordError = TxtRecoveryPasswordError;
        MasterPasswordInput.PasswordChanged += (_, _) =>
        {
            ViewModel.MasterPasswordError = string.Empty;
            InlineValidation.ClearError(MasterPasswordInput, masterPasswordError);
        };
        NewMasterPasswordInput.PasswordChanged += (_, _) => InlineValidation.ClearError(NewMasterPasswordInput, newMasterPasswordError);
        ConfirmMasterPasswordInput.PasswordChanged += (_, _) => InlineValidation.ClearError(ConfirmMasterPasswordInput, confirmMasterPasswordError);
        AuthenticatorConfirmationInput.CodeChanged += (_, _) => InlineValidation.ClearError(AuthenticatorConfirmationInput, authenticatorSetupError);
        RecoveryPassphraseInput.PasswordChanged += (_, _) =>
        {
            ViewModel.RecoveryPasswordError = string.Empty;
            InlineValidation.ClearError(RecoveryPassphraseInput, recoveryPasswordError);
        };
    }

    private void RefreshAuthenticationValidation()
    {
        if (ViewModel.MasterPasswordError.Length > 0)
            InlineValidation.SetError(MasterPasswordInput, masterPasswordError, ViewModel.MasterPasswordError);
        if (ViewModel.RecoveryPasswordError.Length > 0)
            InlineValidation.SetError(RecoveryPassphraseInput, recoveryPasswordError, ViewModel.RecoveryPasswordError);
    }

    private bool ValidateFirstLaunchPassword()
    {
        InlineValidation.ClearError(NewMasterPasswordInput, newMasterPasswordError);
        InlineValidation.ClearError(ConfirmMasterPasswordInput, confirmMasterPasswordError);
        try { MasterPasswordService.ValidateNewMasterPassword(NewMasterPasswordInput.Password); }
        catch (ArgumentException)
        {
            InlineValidation.SetError(NewMasterPasswordInput, newMasterPasswordError,
                "Use a Master Password with at least 12 characters.");
            return false;
        }
        if (string.Equals(NewMasterPasswordInput.Password, ConfirmMasterPasswordInput.Password, StringComparison.Ordinal)) return true;
        InlineValidation.SetError(ConfirmMasterPasswordInput, confirmMasterPasswordError, "The Master Passwords do not match.");
        return false;
    }
}
