using Microsoft.Extensions.DependencyInjection;
using PasswordTool.Core.Abstractions;
using PasswordTool.Core.Inspection;
using PasswordTool.Core.Registry;
using PasswordTool.Core.Services;
using PasswordTool.Presentation;

namespace PasswordTool_WinUI;

internal static class ServiceRegistration
{
    public static ServiceProvider Build()
    {
        var services = new ServiceCollection();
#if DEBUG
        var testDirectory = Environment.GetEnvironmentVariable("PASSWORDTOOL_UI_TEST_DIRECTORY");
        if (!string.IsNullOrWhiteSpace(testDirectory))
            services.AddSingleton(new VaultService(new VaultStorageService(testDirectory), new EncryptionService(), new TotpService()));
        else
#endif
            services.AddSingleton<VaultService>();
        services.AddSingleton<TotpService>();
        services.AddSingleton<PasswordGeneratorService>();
        services.AddSingleton<DialogLifetime>();
        services.AddSingleton<AutofillApprovalService>();
        services.AddSingleton<AppearanceService>();
        services.AddSingleton<IPasswordHasherRegistry, PasswordHasherRegistry>();
        services.AddSingleton<PasswordHasherFactory>();
        services.AddSingleton<PasswordHashInspector>();
        services.AddSingleton<IVaultOperationRunner, VaultOperationRunner>();
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IUserErrorMapper, UserErrorMapper>();
        services.AddSingleton<ISensitiveClipboardService, SensitiveClipboardService>();
        services.AddSingleton<IUserDialogService, NavigationDialogService>();
        services.AddSingleton<IFilePickerService, WinAppFilePickerService>();
        services.AddSingleton<ISystemLockMonitor, SystemLockMonitor>();
        services.AddSingleton<PasswordGeneratorDialogService>();
        services.AddSingleton<AppFlowCoordinator>();
        services.AddSingleton<VaultWorkspaceViewModel>();
        services.AddSingleton<HashToolViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<BackupViewModel>();
        services.AddSingleton<SecurityCheckViewModel>();
        services.AddSingleton<TrashViewModel>();
        services.AddSingleton<ShellViewModel>();
        return services.BuildServiceProvider();
    }
}
