using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PasswordTool.Core.Models;

namespace PasswordTool.Presentation;

public sealed partial class VaultWorkspaceViewModel : ObservableObject
{
    private readonly AppFlowCoordinator flow;
    private IReadOnlyList<VaultItemListItem> allItems = [];
    private IReadOnlyList<VaultGroup> groups = [];
    private bool updatingView;

    public VaultWorkspaceViewModel(AppFlowCoordinator flow)
    {
        this.flow = flow;
        SelectedGroup = AllGroup;
        SelectedViewFilter = ViewFilters[0];
        SelectedSortOrder = SortOrders[0];
    }

    public ObservableCollection<VaultItemListItem> Items { get; } = [];
    public VaultItemGroup AllGroup { get; } = new(null, "All", null, -1, isAll: true);
    public ObservableCollection<VaultItemGroup> GroupTabs { get; } = [];
    public ObservableCollection<VaultGroupOption> GroupOptions { get; } = [new("Create new group...", null, true)];
    public IReadOnlyList<VaultFilterOption> ViewFilters { get; } = [new("All items", VaultViewFilter.All), new("Favorites", VaultViewFilter.Favorites), new("Passwords", VaultViewFilter.Passwords), new("Recovery codes", VaultViewFilter.RecoveryCodes)];
    public IReadOnlyList<VaultSortOption> SortOrders { get; } = [new("Title A–Z", VaultSortOrder.TitleAscending), new("Title Z–A", VaultSortOrder.TitleDescending), new("Recently updated", VaultSortOrder.UpdatedNewest), new("Oldest updated", VaultSortOrder.UpdatedOldest)];

    [ObservableProperty] public partial string SearchText { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial VaultFilterOption SelectedViewFilter { get; set; }
    [ObservableProperty] public partial VaultSortOption SelectedSortOrder { get; set; }
    [ObservableProperty] public partial VaultItemListItem? SelectedItem { get; set; }
    [ObservableProperty] public partial VaultItemGroup SelectedGroup { get; set; }
    [ObservableProperty] public partial bool FilterByGroup { get; set; }
    [ObservableProperty] public partial bool IsVerticalTabs { get; set; }
    public string ItemCountText => $"{Items.Count:N0} item{(Items.Count == 1 ? string.Empty : "s")}";
    public bool IsEmpty => Items.Count == 0 && !IsBusy;
    public bool CanClearFilters => SearchText.Length > 0 || SelectedViewFilter.Value != VaultViewFilter.All || FilterByGroup || !SelectedGroup.IsAll || SelectedSortOrder.Value != VaultSortOrder.TitleAscending;

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        var version = flow.LifecycleVersion;
        try
        {
            var loadedGroups = await flow.GetGroupsAsync(cancellationToken);
            var loadedItems = await flow.GetListItemsAsync(cancellationToken);
            if (!flow.IsCurrentUnlock(version)) return;
            allItems = loadedItems;
            groups = loadedGroups;
            RefreshGroupOptions();
            RefreshGroupTabs();
            ApplyFilter();
        }
        finally
        {
            if (version == flow.LifecycleVersion) { IsBusy = false; OnPropertyChanged(nameof(IsEmpty)); }
        }
    }

    public void Clear()
    {
        allItems = []; groups = []; GroupOptions.Clear();
        GroupOptions.Add(new("Create new group...", null, true));
        RefreshGroupTabs();
        FilterByGroup = false;
        ResetToDefaultView();
        IsBusy = false; SelectedItem = null;
        OnPropertyChanged(nameof(ItemCountText)); OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CanClearFilters));
    }

    public bool SelectItem(Guid id)
    {
        ResetToDefaultView();
        SelectedItem = Items.FirstOrDefault(candidate => candidate.Id == id);
        return SelectedItem is not null;
    }

    public void SelectGroup(VaultItemGroup group)
    {
        if (!group.IsAll && !FilterByGroup && HasFilter)
        {
            // A global-result tab click explicitly starts a fresh group-scoped view.
            updatingView = true;
            try
            {
                FilterByGroup = true;
                SearchText = string.Empty;
                SelectedViewFilter = ViewFilters[0];
                SelectedSortOrder = SortOrders[0];
                SelectedGroup = group;
            }
            finally { updatingView = false; }
            ApplyFilter();
        }
        else SelectedGroup = group;
    }

    public void ResetToDefaultView(bool resetGroupFilter = true)
    {
        updatingView = true;
        try
        {
            SelectedGroup = AllGroup;
            if (resetGroupFilter) FilterByGroup = false;
            SearchText = string.Empty;
            SelectedViewFilter = ViewFilters[0];
            SelectedSortOrder = SortOrders[0];
        }
        finally { updatingView = false; }
        ApplyFilter();
    }

    private bool HasFilter => !string.IsNullOrWhiteSpace(SearchText) || SelectedViewFilter?.Value != VaultViewFilter.All;

    partial void OnSearchTextChanged(string oldValue, string newValue)
    {
        if (updatingView) return;
        if (!string.IsNullOrWhiteSpace(oldValue) && string.IsNullOrWhiteSpace(newValue)) ResetToDefaultView(resetGroupFilter: false);
        else ApplyFilter();
    }
    partial void OnSelectedViewFilterChanged(VaultFilterOption oldValue, VaultFilterOption newValue)
    {
        if (updatingView) return;
        if (oldValue is not null && oldValue.Value != VaultViewFilter.All && newValue.Value == VaultViewFilter.All) ResetToDefaultView(resetGroupFilter: false);
        else ApplyFilter();
    }
    partial void OnSelectedSortOrderChanged(VaultSortOption value) => ApplyFilter();
    partial void OnSelectedGroupChanged(VaultItemGroup value) => ApplyFilter();
    partial void OnFilterByGroupChanged(bool value)
    {
        if (!updatingView) ResetToDefaultView(resetGroupFilter: false);
    }

    private void ApplyFilter()
    {
        if (updatingView || SelectedGroup is null || SelectedViewFilter is null || SelectedSortOrder is null) return;
        if (!FilterByGroup && HasFilter && !SelectedGroup.IsAll)
        {
            updatingView = true;
            try { SelectedGroup = AllGroup; }
            finally { updatingView = false; }
        }
        var selectedId = SelectedItem?.Id;
        var query = SearchText.Trim();
        IEnumerable<VaultItemListItem> filtered = SelectedGroup.IsAll ? allItems : allItems.Where(item => item.GroupId == SelectedGroup.Id);
        if (!string.IsNullOrEmpty(query)) filtered = filtered.Where(item => item.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) || item.Username.Contains(query, StringComparison.CurrentCultureIgnoreCase) || item.Url.Contains(query, StringComparison.CurrentCultureIgnoreCase) || item.Tags.Any(tag => tag.Contains(query, StringComparison.CurrentCultureIgnoreCase)));
        filtered = SelectedViewFilter.Value switch { VaultViewFilter.Favorites => filtered.Where(item => item.IsFavorite), VaultViewFilter.Passwords => filtered.Where(item => item.HasPassword), VaultViewFilter.RecoveryCodes => filtered.Where(item => item.RecoveryCodeCount > 0), _ => filtered };
        filtered = SelectedSortOrder.Value switch { VaultSortOrder.TitleDescending => filtered.OrderByDescending(item => item.Title, StringComparer.CurrentCultureIgnoreCase), VaultSortOrder.UpdatedNewest => filtered.OrderByDescending(item => item.UpdatedAt), VaultSortOrder.UpdatedOldest => filtered.OrderBy(item => item.UpdatedAt), _ => filtered.OrderBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase) };
        var visible = filtered.ToList();
        Items.Clear();
        for (var index = 0; index < visible.Count; index++) Items.Add(visible[index] with { IsLastInGroup = index == visible.Count - 1 });
        SelectedItem = Items.FirstOrDefault(item => item.Id == selectedId);
        OnPropertyChanged(nameof(ItemCountText)); OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CanClearFilters));
    }

    private void RefreshGroupTabs()
    {
        var previous = SelectedGroup;
        updatingView = true;
        try
        {
            AllGroup.Count = allItems.Count;
            GroupTabs.Clear();
            foreach (var group in groups.OrderBy(group => group.SortOrder).ThenBy(group => group.Name, StringComparer.CurrentCultureIgnoreCase))
                GroupTabs.Add(new(group.Id, group.Name, group.AccentColor, group.SortOrder) { Count = allItems.Count(item => item.GroupId == group.Id) });
            SelectedGroup = previous is { IsAll: false } ? GroupTabs.FirstOrDefault(group => group.Id == previous.Id) ?? AllGroup : AllGroup;
        }
        finally { updatingView = false; }
    }

    private void RefreshGroupOptions()
    {
        GroupOptions.Clear();
        foreach (var group in groups.OrderBy(group => group.SortOrder).ThenBy(group => group.Name, StringComparer.CurrentCultureIgnoreCase)) GroupOptions.Add(new(group.Name, group.Id));
        GroupOptions.Add(new("Create new group...", null, true));
    }
}
