namespace PasswordTool.Presentation;

public enum AppFlowState
{
    Loading,
    FirstLaunch,
    Recover,
    CreateMasterPassword,
    SetupAuthenticator,
    SaveRecoveryKey,
    RecoveryKeyValidation,
    RecoveryMasterPassword,
    RecoveryKeySave,
    RecoveryAuthenticator,
    Unlock,
    Unlocked,
    TableLocked
}
