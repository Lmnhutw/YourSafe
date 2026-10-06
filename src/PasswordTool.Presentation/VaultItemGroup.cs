using CommunityToolkit.Mvvm.ComponentModel;

namespace PasswordTool.Presentation;

public sealed partial class VaultItemGroup(Guid? id, string name, string? accentColor, int sortOrder, bool isAll = false) : ObservableObject
{
    public Guid? Id { get; } = id;
    public string Name { get; } = name;
    public string? AccentColor { get; } = accentColor;
    public int SortOrder { get; } = sortOrder;
    public bool IsAll { get; } = isAll;
    public string CountText => Count.ToString("N0");
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText))]
    public partial int Count { get; set; }
}
