using System.IO;
using System.Text;
using System.Text.Json;
using WarbandStudio.Pack;
using WarbandStudio.Packfile;

namespace WarbandStudio.Ui.Tests;

public sealed partial class BackendSafetyTests
{
    [Fact]
    public async Task DeleteTabRemovesLocalRowsAndCrossPageReferencesAndCanUndoRedo()
    {
        var path = await ClipboardFixture();
        var original = File.ReadAllBytes(path);
        _backend.OpenTabs(["SRC", "OUT"]);
        Assert.Contains("2 条升级", _backend.DeleteTab("SRC"));
        Assert.DoesNotContain("SRC", _backend.TabKeys());
        Assert.DoesNotContain("SRC", _backend.Edits.OpenedTabs);
        Assert.Contains("OUT", _backend.Edits.OpenedTabs);
        Assert.Equal("C", ClipCell(_backend.ReadTable($"db/{TG}/SourceData"), 0, "unit_group"));
        Assert.Null(_backend.Pack!.Archive!.Find($"db/{TR}/SourceData"));
        Assert.Null(_backend.Pack.Archive.Find($"db/{TL}/SourceData"));
        Assert.Null(_backend.Pack.Archive.Find("db/unit_upgrade_to_tech_requirements_tables/author"));
        Assert.Null(_backend.Pack.Archive.Find("db/unit_upgrade_to_building_level_requirements_tables/author"));
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.True(_backend.Undo());
        Assert.Contains("SRC", _backend.TabKeys());
        Assert.Equal(2, _backend.ReadTable($"db/{TR}/SourceData").Rows.Count);
        Assert.True(_backend.Redo());
        await _backend.SaveInPlaceAsync();
        await _backend.OpenPackAsync(path);
        Assert.DoesNotContain("SRC", _backend.TabKeys());
        Assert.Equal("D", ClipCell(_backend.ReadTable($"db/{TG}/DestData"), 0, "unit_group"));
        Assert.Single(_backend.ReadTable($"db/{Cost}/author").Rows);
        Assert.Null(_backend.Pack!.Archive!.Find($"db/{TR}/SourceData"));
    }

    [Fact]
    public async Task DeletedLastTabCanBeRecreatedAfterSave()
    {
        const string ui = "ui/campaign ui/warband_upgrades.twui.xml";
        const string xml = """
            <root>
              <hierarchy><parent_tab><holder_tab_src this="holder"><button_toggle_tab_src this="button" /></holder_tab_src></parent_tab></hierarchy>
              <components>
                <warband_upgrades id="warband_upgrades" this="root">
                  <callback_with_context context_object_id="CcoCampaignUnit" context_function_id="FirstValidUpgradeCategoryForUnit(this, &quot;SRC&quot;)" />
                  <callback_with_context context_function_id="UnitGroupUpgradesTree(List.Filter(Category == SelectedCategory))" />
                  <states><src name="src"/><base name="base"/></states>
                </warband_upgrades>
                <holder_tab_src id="holder_tab_src" this="holder"><callback_with_context callback_id="ContextVisibilitySetter" context_function_id="HasUpgrades"/><property name="God" value="SRC"/></holder_tab_src>
                <button_toggle_tab_src id="button_toggle_tab_src" this="button"></button_toggle_tab_src>
              </components>
            </root>
            """;
        var restricted = TabFactionVisibility.Apply(xml, new Dictionary<string, string[]> { ["SRC"] = ["faction_one"] });
        var path = MakePack("tab.pack", (ui, Encoding.UTF8.GetBytes(restricted)),
            ($"db/{TabFileNaming.Cats}/author", Table(TabFileNaming.Cats, new Dictionary<string, object> { ["category"] = "SRC" })));
        await _backend.OpenPackAsync(path);
        _backend.DeleteTab("SRC");
        Assert.Empty(_backend.TabKeys());
        await _backend.SaveInPlaceAsync();
        await _backend.OpenPackAsync(path);
        var stored = Encoding.UTF8.GetString(_backend.Pack!.Archive!.ReadDecoded(ui));
        Assert.DoesNotContain("holder_tab_src", stored);
        Assert.Empty(TabFactionVisibility.Read(stored));
        Assert.Contains("FirstValidUpgradeCategoryForUnit", stored);
        _backend.Edit(() => { _backend.NewTab("NEXT", ""); return true; });
        await _backend.SaveInPlaceAsync();
        Assert.Equal(["NEXT"], _backend.TabKeys());
        var recreated = Encoding.UTF8.GetString(_backend.Pack!.Archive!.ReadDecoded(ui));
        Assert.Contains("button_toggle_tab_next", recreated);
        Assert.DoesNotContain("holder_tab_src", recreated);
        Assert.DoesNotContain("EmptyTabTemplate", recreated);
    }

    [Fact]
    public async Task ModRaceFilterAndUnassignedPageUseEffectivePermissionsFromReferences()
    {
        await PermissionFixture();
        const string main = "main_units_tables";
        const string land = "land_units_tables";
        const string recruit = "building_units_allowed_tables";
        var reference = MakePack("library_mod.pack",
            ($"db/{main}/ref", Table(main, new() { ["unit"] = "military" }, new() { ["unit"] = "exclusive" },
                new() { ["unit"] = "recruit" }, new() { ["unit"] = "orphan" }, new() { ["unit"] = "unmapped" },
                new() { ["unit"] = "hero", ["land_unit"] = "hero_land" })),
            ($"db/{land}/ref", Table(land, new Dictionary<string, object> { ["key"] = "hero_land", ["class"] = "com" })),
            ($"db/{Military}/ref", Table(Military,
                new() { ["unit"] = "military", ["military_group"] = "g2" },
                new() { ["unit"] = "unmapped", ["military_group"] = "no_faction_uses_this_group" })),
            ($"db/{Exclusive}/ref", Table(Exclusive,
                new() { ["key"] = "exclusive", ["faction"] = "f3", ["allowed"] = true },
                new() { ["key"] = "orphan", ["faction"] = "f1", ["allowed"] = false })),
            ($"db/{recruit}/ref", Table(recruit,
                new Dictionary<string, object> { ["unit"] = "recruit", ["faction"] = "f1", ["enabled"] = true })));
        await _backend.OpenPackAsync(reference, asUnitPack: true);

        static string[] Units(string json)
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("units").EnumerateArray().Select(item => item.GetString()!).ToArray();
        }
        var ordinary = Units(_backend.BuildUnitLibraryJson("race:race"));
        Assert.Equal(["u"], ordinary);
        Assert.Equal(["exclusive", "hero", "military", "orphan", "recruit", "unmapped"],
            Units(_backend.BuildModUnitLibraryJson(reference)));
        Assert.Equal(["exclusive", "military", "recruit"],
            Units(_backend.BuildModUnitLibraryJson(reference, $"db/{main}/ref", "race")));
        Assert.Empty(Units(_backend.BuildModUnitLibraryJson(reference, race: "elsewhere")));
        Assert.Equal(["orphan", "unmapped", "v"], Units(_backend.BuildUnitLibraryJson("f2", unassigned: true)));
        _backend.Edit(() => { _backend.Edits.SetExclusive("orphan", "f1", true); _backend.InvalidateCanvas(); return true; });
        Assert.DoesNotContain("orphan", Units(_backend.BuildUnitLibraryJson(null, unassigned: true)));
        Assert.Contains("orphan", Units(_backend.BuildModUnitLibraryJson(reference, race: "race")));
        Assert.True(_backend.Undo());
        Assert.Contains("orphan", Units(_backend.BuildUnitLibraryJson(null, unassigned: true)));
    }
}
