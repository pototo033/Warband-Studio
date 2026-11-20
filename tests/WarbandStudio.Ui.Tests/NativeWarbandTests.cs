using System.IO;
using System.Text;
using System.Text.Json;
using WarbandStudio.Pack;
using WarbandStudio.Packfile;

namespace WarbandStudio.Ui.Tests;

public sealed partial class BackendSafetyTests
{
    private async Task<string> NativeFixture(bool replaceKhorne = false)
    {
        var game = Path.Combine(_settings.GameDir, "data", "db.pack");
        List<(string Path, byte[] Bytes)> files;
        using (var original = PackArchive.Open(game)) files = original.VisibleEntries.Select(entry => (entry.Path, original.ReadDecoded(entry))).ToList();
        void Replace(string table, params Dictionary<string, object>[] rows)
        {
            files.RemoveAll(file => file.Path.StartsWith("db/" + table + "/"));
            files.Add(($"db/{table}/data__", Table(table, rows)));
        }
        Replace(TabFileNaming.Groups, new() { ["unit_group"] = "NA" }, new() { ["unit_group"] = "NB" }, new() { ["unit_group"] = "NC" }, new() { ["unit_group"] = "ND" });
        Replace(TabFileNaming.Infos,
            new() { ["unit_upgrade_group"] = "NA", ["category"] = "KHORNE", ["x"] = 10, ["y"] = 20 },
            new() { ["unit_upgrade_group"] = "NB", ["category"] = "KHORNE", ["x"] = 110, ["y"] = 20 },
            new() { ["unit_upgrade_group"] = "NC", ["category"] = "UNDIVIDED", ["x"] = 10, ["y"] = 220 },
            new() { ["unit_upgrade_group"] = "ND", ["category"] = "UNDIVIDED", ["x"] = 110, ["y"] = 220 });
        Replace(TabFileNaming.Junc, new() { ["unit_group"] = "NA", ["unit"] = "u1" }, new() { ["unit_group"] = "NB", ["unit"] = "u2" },
            new() { ["unit_group"] = "NC", ["unit"] = "u3" }, new() { ["unit_group"] = "ND", ["unit"] = "u4" });
        Replace(TabFileNaming.Routes, new() { ["upgrade_key"] = "RK", ["base_unit_group"] = "NA", ["target_unit_group"] = "NB", ["resource_cost"] = "native_cost", ["required_rank"] = 4 },
            new() { ["upgrade_key"] = "RU", ["base_unit_group"] = "NC", ["target_unit_group"] = "ND", ["resource_cost"] = "native_cost" });
        Replace(TabFileNaming.Links, new() { ["child_key"] = "NB", ["parent_key"] = "NA", ["child_link_position"] = 4, ["parent_link_position"] = 2 },
            new() { ["child_key"] = "ND", ["parent_key"] = "NC", ["child_link_position"] = 4, ["parent_link_position"] = 2 });
        Replace(TabFileNaming.Cats, new() { ["category"] = "KHORNE" }, new() { ["category"] = "UNDIVIDED" }, new() { ["category"] = "EMP" });
        Replace(Cost, new Dictionary<string, object> { ["id"] = "native_cost", ["treasury_cost"] = -456L });
        Replace("main_units_tables", new() { ["unit"] = "u1" }, new() { ["unit"] = "u2" }, new() { ["unit"] = "u3" }, new() { ["unit"] = "u4" });
        Replace("unit_upgrade_to_tech_requirements_tables", new Dictionary<string, object> { ["unit_upgrade"] = "RK", ["technology"] = "test_tech" });
        PackWriter.WriteNew(game, files);
        var xml = """
            <root><warband_upgrades id="warband_upgrades" this="root"><components>
            <holder_tab_KHORNE id="holder_tab_KHORNE" this="a"><userproperties><property name="God" value="KHORNE"/></userproperties></holder_tab_KHORNE>
            <holder_tab_UNDIVIDED id="holder_tab_UNDIVIDED" this="b"><userproperties><property name="God" value="UNDIVIDED"/></userproperties></holder_tab_UNDIVIDED>
            <holder_tab_EMP id="holder_tab_EMP" this="c"><userproperties><property name="God" value="EMP"/></userproperties></holder_tab_EMP>
            </components></warband_upgrades></root>
            """;
        var pack = MakePack("native_mod.pack", ("ui/campaign ui/warband_upgrades.twui.xml", Encoding.UTF8.GetBytes(xml)),
            ($"db/{TabFileNaming.Groups}/mod", Table(TabFileNaming.Groups, new Dictionary<string, object> { ["unit_group"] = "M" })),
            ($"db/{TabFileNaming.Infos}/mod", Table(TabFileNaming.Infos, new Dictionary<string, object> { ["unit_upgrade_group"] = "M", ["category"] = replaceKhorne ? "KHORNE" : "EMP", ["x"] = 900 })),
            ($"db/{TabFileNaming.Junc}/mod", Table(TabFileNaming.Junc, new Dictionary<string, object> { ["unit_group"] = "M", ["unit"] = "u1" })),
            ($"db/{TabFileNaming.Cats}/mod", Table(TabFileNaming.Cats, new() { ["category"] = "EMP" }, new() { ["category"] = "KHORNE" })));
        await _backend.OpenPackAsync(pack);
        return pack;
    }

    private JsonElement NativeCanvas() => JsonDocument.Parse(_backend.BuildWarbandJson()).RootElement.Clone();
    private static string[] NativeGroups(JsonElement canvas) => canvas.GetProperty("groups").EnumerateArray().Select(group => group.GetProperty("unit_group").GetString()!).ToArray();

    [Fact]
    public async Task NativeCanvasInheritsWithoutEditsAndModCategoryReplacementWins()
    {
        var pack = await NativeFixture();
        var original = File.ReadAllBytes(pack);
        var canvas = NativeCanvas();
        Assert.Equal(5, NativeGroups(canvas).Length);
        Assert.Equal(2, canvas.GetProperty("routeList").GetArrayLength());
        Assert.Equal(-456, canvas.GetProperty("costKeys").EnumerateArray().Single(cost => cost.GetProperty("k").GetString() == "native_cost").GetProperty("v").GetDouble());
        Assert.True(_backend.Edits.IsEmpty);
        Assert.Equal(0, _backend.UndoDepth);
        Assert.Equal(original, File.ReadAllBytes(pack));
        _backend.Edit(() => { _backend.SetGroupPos("M", 800, 0); return true; });
        Assert.Empty(_backend.Edits.TableReplacements);
        Assert.Equal(5, NativeGroups(NativeCanvas()).Length);
        await _backend.CloseSessionAsync(_backend.Pack!);
        await NativeFixture(replaceKhorne: true);
        var replaced = NativeCanvas();
        Assert.Equal(["M", "NC", "ND"], NativeGroups(replaced).Order(StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain(replaced.GetProperty("routeList").EnumerateArray(), route => route.GetProperty("k").GetString() == "RK");
        Assert.True(_backend.Edits.IsEmpty);
    }

    [Fact]
    public async Task NativeMoveUndoRedoSaveAndNewGroupKeepTheRestOfTheOriginalPage()
    {
        var pack = await NativeFixture();
        var originalGame = File.ReadAllBytes(Path.Combine(_settings.GameDir, "data", "db.pack"));
        _backend.Edit(() => { _backend.SetGroupPos("NA", 333, 444); return true; });
        Assert.Equal(1, _backend.UndoDepth);
        Assert.NotEmpty(_backend.Edits.TableReplacements);
        Assert.Empty(_backend.Edits.AddUnitGroup);
        Assert.True(_backend.Undo());
        Assert.True(_backend.Edits.IsEmpty);
        Assert.Equal(10, NativeCanvas().GetProperty("groups").EnumerateArray().Single(group => group.GetProperty("unit_group").GetString() == "NA").GetProperty("x").GetInt32());
        Assert.True(_backend.Redo());
        await _backend.SaveInPlaceAsync();
        Assert.Equal("333", ClipCell(_backend.ReadTable($"db/{TabFileNaming.Infos}/data__"), 0, "x"));
        _backend.Edit(() => { _backend.NewGroup("NEW", 400, 500, "KHORNE"); return true; });
        await _backend.SaveInPlaceAsync();
        await _backend.CloseSessionAsync(_backend.Pack!);
        await _backend.OpenPackAsync(pack);
        Assert.Equal(6, NativeGroups(NativeCanvas()).Length);
        Assert.Contains("NB", NativeGroups(NativeCanvas()));
        Assert.Equal(originalGame, File.ReadAllBytes(Path.Combine(_settings.GameDir, "data", "db.pack")));
    }

    [Fact]
    public async Task NativeRouteAndGroupRemovalPersistAndDeletingAllNativeTabsDoesNotResurrectThem()
    {
        var pack = await NativeFixture();
        _backend.Edit(() => { _backend.RemoveRoute("RK", "NA", "NB"); _backend.RemoveGroup("NB"); return true; });
        await _backend.SaveInPlaceAsync();
        await _backend.CloseSessionAsync(_backend.Pack!);
        await _backend.OpenPackAsync(pack);
        Assert.DoesNotContain("NB", NativeGroups(NativeCanvas()));
        Assert.DoesNotContain(NativeCanvas().GetProperty("routeList").EnumerateArray(), route => route.GetProperty("k").GetString() == "RK");
        _backend.DeleteTab("KHORNE");
        _backend.DeleteTab("UNDIVIDED");
        await _backend.SaveInPlaceAsync();
        await _backend.CloseSessionAsync(_backend.Pack!);
        await _backend.OpenPackAsync(pack);
        Assert.Equal(["M"], NativeGroups(NativeCanvas()));
        using var saved = PackArchive.Open(pack);
        var nativeInfos = saved.Find($"db/{TabFileNaming.Infos}/data__");
        Assert.NotNull(nativeInfos);
        Assert.Empty(DbTable.Decode(saved.ReadDecoded(nativeInfos!), TabFileNaming.Infos, _schema).Rows);
    }

    [Fact]
    public async Task NativeReplacementReusingOriginalKeysDoesNotInheritOldUnitsRoutesOrLinks()
    {
        await NativeFixture();
        var pack = MakePack("native_reused.pack",
            ($"db/{TabFileNaming.Groups}/mod", Table(TabFileNaming.Groups, new() { ["unit_group"] = "NA" }, new() { ["unit_group"] = "NB" })),
            ($"db/{TabFileNaming.Infos}/mod", Table(TabFileNaming.Infos, new() { ["unit_upgrade_group"] = "NA", ["category"] = "KHORNE", ["x"] = 700 }, new() { ["unit_upgrade_group"] = "NB", ["category"] = "KHORNE", ["x"] = 800 })),
            ($"db/{TabFileNaming.Junc}/mod", Table(TabFileNaming.Junc, new() { ["unit_group"] = "NA", ["unit"] = "u3" }, new() { ["unit_group"] = "NB", ["unit"] = "u4" })));
        await _backend.OpenPackAsync(pack);
        var canvas = NativeCanvas();
        Assert.Equal(4, NativeGroups(canvas).Length);
        Assert.Equal(["u3"], canvas.GetProperty("groups").EnumerateArray().Single(group => group.GetProperty("unit_group").GetString() == "NA").GetProperty("units").EnumerateArray().Select(unit => unit.GetString()!).ToArray());
        Assert.Single(canvas.GetProperty("routeList").EnumerateArray());
        Assert.Single(canvas.GetProperty("links").EnumerateArray());
        _backend.Edit(() => { _backend.SetGroupPos("NC", 400, 500); return true; });
        Assert.Single(NativeCanvas().GetProperty("routeList").EnumerateArray());
        await _backend.SaveInPlaceAsync();
        Assert.Single(NativeCanvas().GetProperty("routeList").EnumerateArray());
    }

    [Fact]
    public async Task NativeTableSnapshotMapsFilteredRowsBackToTheOriginalFile()
    {
        await NativeFixture(replaceKhorne: true);
        var path = $"db/{TabFileNaming.Infos}/data__";
        var table = _backend.ReadTable(path);
        Assert.Equal(2, table.Rows.Count);
        var before = table.Rows[0].Select(value => value.ToTsv()).ToArray();
        _backend.EditTableCell(_backend.Pack!.PackPath!, path, 0, "x", before, "777");
        var group = NativeCanvas().GetProperty("groups").EnumerateArray().Single(group => group.GetProperty("unit_group").GetString() == "NC");
        Assert.Equal(777, group.GetProperty("x").GetInt32());
        Assert.DoesNotContain("NA", NativeGroups(NativeCanvas()));
        Assert.True(_backend.Undo());
        Assert.True(_backend.Edits.IsEmpty);
    }

    [Fact]
    public async Task NativeTabRenameKeepsMembersAndRoutesAfterSaving()
    {
        var pack = await NativeFixture();
        _backend.Edit(() => { _backend.RenameTab("KHORNE", "RENAMED"); return true; });
        await _backend.SaveInPlaceAsync();
        await _backend.CloseSessionAsync(_backend.Pack!);
        await _backend.OpenPackAsync(pack);
        var canvas = NativeCanvas();
        var group = canvas.GetProperty("groups").EnumerateArray().Single(group => group.GetProperty("unit_group").GetString() == "NA");
        Assert.Equal("RENAMED", group.GetProperty("category").GetString());
        Assert.Equal("u1", Assert.Single(group.GetProperty("units").EnumerateArray()).GetString());
        Assert.Contains(canvas.GetProperty("routeList").EnumerateArray(), route => route.GetProperty("k").GetString() == "RK");
    }

    [Fact]
    public async Task NativeCopyIsReadOnlyAndRenameSynchronizesReferences()
    {
        await NativeFixture();
        var copied = _backend.CopyTab("KHORNE");
        Assert.Contains("2 个组", copied);
        Assert.True(_backend.Edits.IsEmpty);
        _backend.PasteTab("EMP");
        Assert.Equal(7, NativeGroups(NativeCanvas()).Length);
        Assert.True(_backend.Undo());
        _backend.RenameGroup("NA", "RenamedNative");
        Assert.Contains("RenamedNative", NativeGroups(NativeCanvas()));
        Assert.DoesNotContain("NA", NativeGroups(NativeCanvas()));
        Assert.Equal("RenamedNative", NativeCanvas().GetProperty("routeList").EnumerateArray().Single(route => route.GetProperty("k").GetString() == "RK").GetProperty("b").GetString());
        Assert.True(_backend.Undo());
        Assert.True(_backend.Edits.IsEmpty);
    }
}
