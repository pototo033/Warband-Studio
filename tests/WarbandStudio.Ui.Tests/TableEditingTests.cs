using System.IO;
using System.Reflection;
using WarbandStudio.Pack;
using WarbandStudio.Packfile;
using WarbandStudio.Ui.Services;
using WarbandStudio.Ui.ViewModels;

namespace WarbandStudio.Ui.Tests;

public sealed partial class BackendSafetyTests
{
    [Fact]
    public async Task OpeningTableActivatesDocumentAndNeverShowsWelcomeOverlay()
    {
        var vm = new MainViewModel(_settings, ValidateFixturePack);
        var backend = (Backend)typeof(MainViewModel).GetField("_backend", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(vm)!;
        await using var lifetime = backend;
        Assert.True(vm.IsWelcomeVisible);
        var path = MakePack("visible_table.pack");
        await backend.OpenPackAsync(path);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        await vm.OpenTableAsync("db/resource_costs_tables/author", path);
        var tab = Assert.IsType<TableTab>(vm.ActiveCenterTab);
        Assert.False(vm.IsCanvasActive); Assert.False(vm.IsWelcomeVisible);
        Assert.Contains(nameof(vm.IsWelcomeVisible), changed);
        Assert.Single(tab.View.Table!.Rows.Cast<System.Data.DataRow>());
        Assert.Equal("-100", tab.View.Table.Rows[0]["treasury_cost"]);
        await vm.CommitTableCellAsync(tab, 0, "treasury_cost", tab.View.Table.Rows[0].ItemArray.Select(v => v?.ToString() ?? "").ToArray(), "-321");
        Assert.Equal("-321", tab.View.Table.Rows[0]["treasury_cost"]);
        Assert.True(backend.Undo()); Assert.True(backend.Redo());
        await backend.SaveInPlaceAsync();
        await vm.OpenTableAsync(tab.InnerPath, path);
        Assert.Same(tab, vm.ActiveCenterTab); Assert.False(vm.IsWelcomeVisible);
        Assert.Equal("-321", backend.ReadTable(tab.InnerPath).Rows[0].Single(v => v.Kind == DbValueKind.Int).ToTsv());
        vm.CloseTableTab(tab);
        Assert.True(vm.IsWelcomeVisible);
    }

    [Fact]
    public async Task SameTableInDifferentPacksHasSeparateVisibleDocuments()
    {
        var vm = new MainViewModel(_settings, ValidateFixturePack);
        var backend = (Backend)typeof(MainViewModel).GetField("_backend", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(vm)!;
        await using var lifetime = backend;
        var second = MakePack("other_document.pack");
        await backend.OpenPackAsync(_pack); await backend.OpenPackAsync(second);
        const string inner = "db/resource_costs_tables/author";
        await vm.OpenTableAsync(inner, _pack);
        var firstTab = Assert.IsType<TableTab>(vm.ActiveCenterTab);
        Assert.Equal(_pack, backend.Pack!.PackPath); Assert.False(vm.IsWelcomeVisible);
        await vm.OpenTableAsync(inner, second);
        var secondTab = Assert.IsType<TableTab>(vm.ActiveCenterTab);
        Assert.NotSame(firstTab, secondTab); Assert.Equal(second, secondTab.PackPath);
        Assert.False(vm.IsWelcomeVisible);
        await vm.OpenTableAsync(inner, _pack);
        Assert.Same(firstTab, vm.ActiveCenterTab); Assert.Equal(2, vm.CenterTabs.Count);
        vm.ActiveCenterTab = new CanvasTab { Header = "canvas", PackPath = _pack };
        Assert.True(vm.IsCanvasActive); Assert.False(vm.IsWelcomeVisible);
    }

    [Fact]
    public async Task TableBuffersFollowOwningPackAndDiscardRestoresDisk()
    {
        const string path = "db/resource_costs_tables/author";
        var first = _backend.Pack!;
        Cell(path, "treasury_cost", "-222");
        var second = await _backend.OpenPackAsync(MakePack("second_table.pack"));
        Assert.Equal("-100", Value(path, "treasury_cost"));
        Cell(path, "treasury_cost", "-333");
        _backend.SwitchTo(first); Assert.Equal("-222", Value(path, "treasury_cost"));
        _backend.DiscardEdits(); Assert.Equal("-100", Value(path, "treasury_cost"));
        _backend.SwitchTo(second); Assert.Equal("-333", Value(path, "treasury_cost"));
    }
    private void Cell(string path, string column, string text, int row = 0)
    {
        var table = _backend.ReadTable(path);
        _backend.EditTableCell(_backend.Pack!.PackPath!, path, row, column, table.Rows[row].Select(v => v.ToTsv()).ToArray(), text);
    }
    private string Value(string path, string column, int row = 0)
    {
        var table = _backend.ReadTable(path);
        return table.Rows[row][table.Columns.FindIndex(c => c.Name == column)].ToTsv();
    }
    [Fact]
    public async Task TableAndCostFormLastActionWinsUndoRedoSaveReopen()
    {
        const string path = "db/resource_costs_tables/author";
        _backend.Edit(() => _backend.UpdateWarbandCost("C", -200, null, 0, false));
        Assert.Equal("-200", Value(path, "treasury_cost"));
        Cell(path, "treasury_cost", "-345");
        Assert.Equal(-345, _backend.CostList().Single().Gold);
        Assert.True(_backend.Undo()); Assert.Equal("-200", Value(path, "treasury_cost"));
        Assert.True(_backend.Redo()); Assert.Equal("-345", Value(path, "treasury_cost"));
        _backend.Edit(() => _backend.UpdateWarbandCost("C", -456, null, 0, false));
        Assert.Equal("-456", Value(path, "treasury_cost"));
        Cell(path, "treasury_cost", "-567");
        await _backend.SaveInPlaceAsync();
        Assert.Equal("-567", Value(path, "treasury_cost")); Assert.Equal(0, _backend.EditCount);
        await _backend.CloseSessionAsync(_backend.Pack!); await _backend.OpenPackAsync(_pack);
        Assert.Equal("-567", Value(path, "treasury_cost"));
    }
    [Fact]
    public void CellBadInputNoopAndStaleSourceNeverChangeEdits()
    {
        const string path = "db/resource_costs_tables/author";
        var before = _backend.UndoDepth;
        Cell(path, "treasury_cost", "-100"); Assert.Equal(before, _backend.UndoDepth);
        Assert.Throws<OverflowException>(() => Cell(path, "treasury_cost", "9999999999999999"));
        Assert.Equal(before, _backend.UndoDepth); Assert.Equal(0, _backend.EditCount);
        var snapshot = _backend.ReadTable(path).Rows[0].Select(v => v.ToTsv()).ToArray();
        Cell(path, "treasury_cost", "-200");
        Assert.Throws<InvalidOperationException>(() => _backend.EditTableCell(_pack, path, 0, "treasury_cost", snapshot, "-300"));
        Assert.Throws<InvalidOperationException>(() => _backend.EditTableCell("other.pack", path, 0, "treasury_cost", snapshot, "-300"));
        Assert.Equal("-200", Value(path, "treasury_cost"));
    }
    [Fact]
    public async Task CostKeyRenameSynchronizesCompositeReferencesWithoutDroppingResources()
    {
        Cell("db/resource_costs_tables/author", "id", "renamed_cost");
        Assert.Equal("renamed_cost", _backend.CostList().Single().Id);
        var links = _backend.ReadTable("db/resource_cost_pooled_resource_junctions_tables/author");
        Assert.Equal(2, links.Rows.Count);
        var col = links.Columns.FindIndex(c => c.Name == "resource_cost");
        Assert.All(links.Rows, row => Assert.Equal("renamed_cost", row[col].ToTsv()));
        Assert.True(_backend.Undo()); Assert.Equal("C", _backend.CostList().Single().Id);
        Assert.True(_backend.Redo()); await _backend.SaveInPlaceAsync();
        Assert.Equal(2, _backend.CostList().Single().Resources.Count);
    }
    private async Task<string> OpenGroupFixture()
    {
        var path = MakePack("group_edit.pack",
            ("db/unit_upgrade_groups_tables/author", Table("unit_upgrade_groups_tables", new() { ["unit_group"] = "g" }, new() { ["unit_group"] = "other" })),
            ("db/unit_to_unit_group_junctions_tables/author", Table("unit_to_unit_group_junctions_tables", new() { ["unit"] = "u", ["unit_group"] = "g" }, new() { ["unit"] = "u", ["unit_group"] = "other" })),
            ("db/unit_upgrade_group_ui_infos_tables/author", Table("unit_upgrade_group_ui_infos_tables", new() { ["unit_upgrade_group"] = "g", ["category"] = "EMP", ["x"] = 10, ["y"] = 20 }, new() { ["unit_upgrade_group"] = "other", ["category"] = "EMP", ["x"] = 30, ["y"] = 40 })),
            ("db/unit_upgrade_to_unit_groups_tables/author", Table("unit_upgrade_to_unit_groups_tables", new Dictionary<string, object> { ["upgrade_key"] = "r", ["base_unit_group"] = "g", ["target_unit_group"] = "other", ["resource_cost"] = "C" })),
            ("db/unit_upgrade_group_ui_links_tables/author", Table("unit_upgrade_group_ui_links_tables", new Dictionary<string, object> { ["child_key"] = "other", ["parent_key"] = "g" })));
        await _backend.OpenPackAsync(path); return path;
    }
    [Fact]
    public async Task GroupRenameKeepsPendingPositionUnitsRoutesLinksAndSurvivesSave()
    {
        var path = await OpenGroupFixture();
        _backend.Edit(() => { _backend.SetGroupPos("g", 111, 222); return true; });
        var depth = _backend.UndoDepth;
        _backend.RenameGroup("g", "renamed"); Assert.Equal(depth + 1, _backend.UndoDepth);
        Assert.Equal("renamed", Value("db/unit_upgrade_groups_tables/author", "unit_group"));
        Assert.Equal("renamed", Value("db/unit_upgrade_to_unit_groups_tables/author", "base_unit_group"));
        Assert.Equal("renamed", Value("db/unit_upgrade_group_ui_links_tables/author", "parent_key"));
        Assert.Equal("111", Value("db/unit_upgrade_group_ui_infos_tables/author", "x"));
        Assert.Equal(2, _backend.ReadTable("db/unit_to_unit_group_junctions_tables/author").Rows.Count);
        Assert.True(_backend.Undo()); Assert.Equal("g", Value("db/unit_upgrade_groups_tables/author", "unit_group"));
        Assert.True(_backend.Redo());
        _backend.Edit(() => { _backend.SetGroupPos("renamed", 333, 444); return true; });
        await _backend.SaveInPlaceAsync();
        Assert.Equal("renamed", Value("db/unit_upgrade_group_ui_infos_tables/author", "unit_upgrade_group"));
        Assert.Equal("333", Value("db/unit_upgrade_group_ui_infos_tables/author", "x"));
        await _backend.CloseSessionAsync(_backend.Pack!); await _backend.OpenPackAsync(path);
        Assert.Equal("renamed", Value("db/unit_upgrade_to_unit_groups_tables/author", "base_unit_group"));
    }
    [Fact]
    public async Task GroupCollisionAndCompositeDuplicateRollbackAndCaseRenameWork()
    {
        await OpenGroupFixture();
        Assert.Throws<InvalidDataException>(() => _backend.RenameGroup("g", "OTHER"));
        Assert.Equal(0, _backend.EditCount);
        Assert.Throws<InvalidDataException>(() => Cell("db/unit_to_unit_group_junctions_tables/author", "unit_group", "other"));
        Assert.Equal(0, _backend.EditCount);
        _backend.RenameGroup("g", "G");
        Assert.Equal("G", Value("db/unit_upgrade_to_unit_groups_tables/author", "base_unit_group"));
        Assert.True(_backend.Undo()); Assert.Equal("g", Value("db/unit_upgrade_to_unit_groups_tables/author", "base_unit_group"));
    }
    [Fact]
    public async Task NewGroupRenameDeleteUndoAndFailedExportRetainState()
    {
        await OpenGroupFixture();
        _backend.Edit(() => { _backend.AddUnitToGroup("new_group", "u", 1, 2, true, "EMP"); return true; });
        _backend.RenameGroup("new_group", "new_name");
        Assert.True(_backend.Edits.TableReplacements.Count > 0);
        _backend.Edit(() => { _backend.RemoveGroup("new_name"); return true; });
        Assert.True(_backend.Undo()); Assert.True(_backend.Redo()); Assert.True(_backend.Undo());
        Cell("db/unit_upgrade_to_unit_groups_tables/author", "base_unit_group", "missing");
        var destination = MakePack("unchanged.pack"); var original = File.ReadAllBytes(destination);
        Assert.Throws<InvalidDataException>(() => _backend.Export(destination));
        Assert.Equal(original, File.ReadAllBytes(destination)); Assert.True(_backend.EditCount > 0);
        Assert.True(_backend.Undo()); Assert.Equal("g", Value("db/unit_upgrade_to_unit_groups_tables/author", "base_unit_group"));
    }
}
