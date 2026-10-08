using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PasswordTool.Core.Models;

namespace PasswordTool.Presentation;

public sealed partial class SecurityCheckViewModel(
    AppFlowCoordinator flow,
    IUserErrorMapper errorMapper) : ObservableObject
{
    public ObservableCollection<VaultSecurityFinding> Findings { get; } = [];

    [ObservableProperty] public partial VaultSecurityFinding? SelectedFinding { get; set; }
    [ObservableProperty] public partial string Summary { get; set; } = "Check for weak, reused, or old passwords on this device.";
    [ObservableProperty] public partial string ErrorMessage { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsErrorOpen { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }

    public async Task RunAsync()
    {
        IsBusy = true;
        var version = flow.LifecycleVersion;
        IsErrorOpen = false;
        try
        {
            var findings = await flow.GetSecurityFindingsAsync(string.Empty);
            if (!flow.IsCurrentUnlock(version)) return;
            Findings.Clear();
            foreach (var finding in findings) Findings.Add(finding);
            var affected = findings.Select(finding => finding.ItemId).Distinct().Count();
            Summary = findings.Count == 0
                ? "No weak or reused passwords found, and none are a year old or older."
                : $"Found {findings.Count:N0} password issues in {affected:N0} saved items. Passwords are never shown in these results.";
        }
        catch (Exception exception)
        {
            if (flow.IsCurrentUnlock(version) && exception is not OperationCanceledException)
            {
                ErrorMessage = errorMapper.Map(exception);
                IsErrorOpen = true;
            }
        }
        finally
        {
            if (flow.IsCurrentUnlock(version)) IsBusy = false;
        }
    }

    public void Clear()
    {
        Findings.Clear();
        SelectedFinding = null;
        Summary = "Check for weak, reused, or old passwords on this device.";
        ErrorMessage = string.Empty;
        IsErrorOpen = false;
        IsBusy = false;
    }
}
