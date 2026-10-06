using PasswordTool.Core.Models;
using PasswordTool.Core.Services;

namespace PasswordTool.Presentation.Tests;

public sealed class VaultWorkspaceViewModelTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "PasswordTool.Presentation.Tests", Guid.NewGuid().ToString("N"));
    private readonly VaultService vault;
    private readonly VaultOperationRunner runner = new();
    private readonly VaultWorkspaceViewModel workspace;
    private readonly VaultGroup work;
    private readonly VaultGroup personal;

    public VaultWorkspaceViewModelTests()
    {
        var totp = new TotpService();
        var secret = totp.GenerateSecret();
        vault = new(new VaultStorageService(directory), new EncryptionService(), totp);
        vault.InitializeNewVault("correct horse battery staple", secret, totp.GetCurrentCode(secret).Code, RecoveryKeyService.Generate(), true);
        work = vault.AddGroup("Work", "#336699");
        personal = vault.AddGroup("Personal");
        vault.AddGroup("Empty");
        vault.AddItem(new VaultItem { Title = "GitHub work", Username = "work-user", Password = "test-password", GroupId = work.Id, IsFavorite = true });
        vault.AddItem(new VaultItem { Title = "Alpha", Password = "test-password", GroupId = work.Id });
        vault.AddItem(new VaultItem { Title = "GitHub personal", Password = "test-password", GroupId = personal.Id, Tags = ["social"] });
        vault.AddItem(new VaultItem { Title = "Recovery", Type = VaultItemType.RecoveryCodes, RecoveryCodes = ["test-code-1", "test-code-2"] });
        workspace = new(new AppFlowCoordinator(vault, runner, totp));
    }

    [Fact]
    public async Task Clear_filters_availability_tracks_resettable_state_without_changing_layout()
    {
        await workspace.RefreshAsync();
        Assert.False(workspace.CanClearFilters);
        workspace.IsVerticalTabs = true;
        workspace.SearchText = "github";
        Assert.True(workspace.CanClearFilters);
        workspace.ResetToDefaultView();
        Assert.False(workspace.CanClearFilters);
        Assert.True(workspace.IsVerticalTabs);
        workspace.SearchText = "   ";
        Assert.True(workspace.CanClearFilters);
        workspace.ResetToDefaultView();
        Assert.Equal(string.Empty, workspace.SearchText);
        workspace.SelectedSortOrder = workspace.SortOrders[1];
        Assert.True(workspace.CanClearFilters);
        workspace.ResetToDefaultView();
        workspace.SelectGroup(Tab(work.Id));
        Assert.True(workspace.CanClearFilters);
    }

    [Fact]
    public async Task Defaults_show_all_sorted_and_tabs_include_empty_groups()
    {
        await workspace.RefreshAsync();
        Assert.Same(workspace.AllGroup, workspace.SelectedGroup);
        Assert.False(workspace.FilterByGroup);
        Assert.False(workspace.IsVerticalTabs);
        Assert.Equal(["Alpha", "GitHub personal", "GitHub work", "Recovery"], workspace.Items.Select(item => item.Title));
        Assert.Equal(["Ungrouped", "Work", "Personal", "Empty"], workspace.GroupTabs.Select(group => group.Name));
        Assert.All(workspace.GroupTabs, group => Assert.NotNull(group.Id));
        Assert.Equal(4, workspace.AllGroup.Count);
        Assert.Equal(0, workspace.GroupTabs.Single(group => group.Name == "Empty").Count);
        workspace.SelectGroup(workspace.GroupTabs.Single(group => group.Name == "Empty"));
        Assert.Empty(workspace.Items);
        Assert.True(workspace.IsEmpty);
    }

    [Fact]
    public async Task Scoped_search_and_view_filter_stay_in_selected_group_including_ungrouped()
    {
        await workspace.RefreshAsync();
        workspace.FilterByGroup = true;
        workspace.SelectGroup(Tab(work.Id));
        workspace.SearchText = "github";
        Assert.Equal("GitHub work", Assert.Single(workspace.Items).Title);
        Assert.Equal(work.Id, workspace.SelectedGroup.Id);
        workspace.SelectedViewFilter = workspace.ViewFilters[1];
        Assert.Equal("GitHub work", Assert.Single(workspace.Items).Title);
        workspace.SelectGroup(Tab(personal.Id));
        Assert.Empty(workspace.Items);
        workspace.ResetToDefaultView();
        workspace.FilterByGroup = true;
        workspace.SelectGroup(workspace.GroupTabs.Single(group => group.Name == "Ungrouped"));
        workspace.SelectedViewFilter = workspace.ViewFilters[3];
        Assert.Equal("Recovery", Assert.Single(workspace.Items).Title);
        Assert.False(workspace.SelectedGroup.IsAll);
    }

    [Fact]
    public async Task Global_filter_moves_to_all_and_group_click_starts_fresh_scoped_view()
    {
        await workspace.RefreshAsync();
        workspace.SelectGroup(Tab(work.Id));
        Assert.Equal(2, workspace.Items.Count);
        workspace.SearchText = "github";
        Assert.Same(workspace.AllGroup, workspace.SelectedGroup);
        Assert.Equal(2, workspace.Items.Count);
        workspace.SelectedSortOrder = workspace.SortOrders[1];
        workspace.SelectedViewFilter = workspace.ViewFilters[2];
        workspace.SelectGroup(Tab(work.Id));
        Assert.True(workspace.FilterByGroup);
        Assert.Equal(work.Id, workspace.SelectedGroup.Id);
        Assert.Equal(string.Empty, workspace.SearchText);
        Assert.Equal(VaultViewFilter.All, workspace.SelectedViewFilter.Value);
        Assert.Equal(VaultSortOrder.TitleAscending, workspace.SelectedSortOrder.Value);
        Assert.Equal(["Alpha", "GitHub work"], workspace.Items.Select(item => item.Title));
    }

    [Fact]
    public async Task Flag_toggle_resets_view_but_keeps_the_new_flag()
    {
        await workspace.RefreshAsync();
        foreach (var flag in new[] { true, false })
        {
            workspace.SelectGroup(Tab(work.Id));
            workspace.SearchText = "github";
            workspace.SelectedSortOrder = workspace.SortOrders[1];
            workspace.FilterByGroup = flag;
            AssertDefaultView();
            Assert.Equal(flag, workspace.FilterByGroup);
        }
    }

    [Theory]
    [InlineData("search")]
    [InlineData("view")]
    [InlineData("clear")]
    public async Task Clearing_any_filter_resets_all_filters_and_sort_without_changing_flag(string trigger)
    {
        await workspace.RefreshAsync();
        workspace.FilterByGroup = true;
        workspace.SelectGroup(Tab(work.Id));
        workspace.SearchText = "github";
        workspace.SelectedViewFilter = workspace.ViewFilters[1];
        workspace.SelectedSortOrder = workspace.SortOrders[1];
        if (trigger == "search") workspace.SearchText = string.Empty;
        else if (trigger == "view") workspace.SelectedViewFilter = workspace.ViewFilters[0];
        else workspace.ResetToDefaultView();
        AssertDefaultView();
        Assert.Equal(trigger != "clear", workspace.FilterByGroup);
    }

    [Fact]
    public async Task Layout_changes_preserve_collection_selection_filters_and_sort_in_both_directions()
    {
        await workspace.RefreshAsync();
        workspace.FilterByGroup = true;
        workspace.SelectGroup(Tab(work.Id));
        workspace.SearchText = "github";
        workspace.SelectedViewFilter = workspace.ViewFilters[1];
        workspace.SelectedSortOrder = workspace.SortOrders[1];
        workspace.SelectedItem = workspace.Items[0];
        var selected = workspace.SelectedItem;
        var tab = workspace.SelectedGroup;
        var rows = workspace.Items.ToArray();
        foreach (var vertical in new[] { true, false })
        {
            workspace.IsVerticalTabs = vertical;
            Assert.Same(tab, workspace.SelectedGroup);
            Assert.Same(selected, workspace.SelectedItem);
            Assert.Equal(rows, workspace.Items);
            Assert.True(workspace.FilterByGroup);
            Assert.Equal("github", workspace.SearchText);
            Assert.Equal(VaultViewFilter.Favorites, workspace.SelectedViewFilter.Value);
            Assert.Equal(VaultSortOrder.TitleDescending, workspace.SelectedSortOrder.Value);
        }
    }

    [Fact]
    public async Task Refresh_preserves_active_group_and_selection_after_rename_and_select_item_can_escape_scope()
    {
        await workspace.RefreshAsync();
        workspace.FilterByGroup = true;
        workspace.SelectGroup(Tab(work.Id));
        workspace.SelectedItem = workspace.Items[0];
        var selectedId = workspace.SelectedItem.Id;
        vault.UpdateGroup(work.Id, "Renamed", "#224466");
        await workspace.RefreshAsync();
        Assert.Equal(work.Id, workspace.SelectedGroup.Id);
        Assert.Equal("Renamed", workspace.SelectedGroup.Name);
        Assert.Equal("#224466", workspace.SelectedGroup.AccentColor);
        Assert.Equal(selectedId, workspace.SelectedItem?.Id);
        var other = vault.GetItems().Single(item => item.GroupId == personal.Id);
        Assert.True(workspace.SelectItem(other.Id));
        Assert.Same(workspace.AllGroup, workspace.SelectedGroup);
        Assert.Equal(other.Id, workspace.SelectedItem?.Id);
        workspace.Clear();
        AssertDefaultView();
        Assert.Null(workspace.SelectedItem);
        Assert.Equal(0, workspace.AllGroup.Count);
    }

    private VaultItemGroup Tab(Guid? id) => workspace.GroupTabs.Single(group => group.Id == id);

    private void AssertDefaultView()
    {
        Assert.Same(workspace.AllGroup, workspace.SelectedGroup);
        Assert.Equal(string.Empty, workspace.SearchText);
        Assert.Equal(VaultViewFilter.All, workspace.SelectedViewFilter.Value);
        Assert.Equal(VaultSortOrder.TitleAscending, workspace.SelectedSortOrder.Value);
    }

    public void Dispose()
    {
        runner.Dispose();
        vault.Dispose();
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
