using System.Xml.Linq;
using WarbandStudio.Pack;

namespace WarbandStudio.Tests;

public sealed class TabFactionVisibilityTests
{
    private const string Xml = """
        <root><warband_upgrades id="warband_upgrades" this="root-guid">
        <callback_with_context callback_id="ContextCommandGiverOnContextSet" context_object_id="CcoCampaignUnit" context_function_id="UnitUpgradeManagerContext.FirstValidUpgradeCategoryForUnit(this, &quot;EMP&quot;)"/>
        <callback_with_context callback_id="ContextList" context_function_id="UnitUpgradeManagerContext.UnitGroupUpgradesTree(UnitUpgradeManagerContext.ForceUnitGroupUpgradesList(SelectedCharacter.MilitaryForceContext).Filter(Category == SelectedCategory))"/>
        <holder_tab_emp id="holder_tab_emp" this="a"><callback_with_context callback_id="ContextVisibilitySetter" context_function_id="OriginalCondition &amp;&amp; HasUpgrades"/></holder_tab_emp>
        <holder_tab_emp2 id="holder_tab_emp2" this="b"><callback_with_context callback_id="ContextVisibilitySetter" context_function_id="HasSecondPage"/></holder_tab_emp2>
        <holder_tab_emp3 id="holder_tab_emp3" this="c"><callback_with_context callback_id="ContextVisibilitySetter" context_function_id="ThirdPageCondition"/></holder_tab_emp3>
        </warband_upgrades></root>
        """;
    private static Dictionary<string, string[]> Rules(params (string Tab, string[] Factions)[] rules) => rules.ToDictionary(x => x.Tab, x => x.Factions);
    private static string Visibility(string xml, string id) => (string)XDocument.Parse(xml).Descendants()
        .Single(e => (string?)e.Attribute("id") == "holder_tab_" + id).Element("callback_with_context")!.Attribute("context_function_id")!;

    [Fact]
    public void MultipleFactionsPersistWithoutChangingOtherPagesOrOriginalConditions()
    {
        var result = TabFactionVisibility.Apply(Xml, Rules(("EMP", ["reikland", "wissenland"])));
        Assert.Equal(["reikland", "wissenland"], TabFactionVisibility.Read(result)["emp"]);
        Assert.Contains("OriginalCondition && HasUpgrades", Visibility(result, "emp"));
        Assert.Contains("== \"reikland\" ||", Visibility(result, "emp"));
        Assert.Equal(Visibility(Xml, "emp3"), Visibility(result, "emp3"));
        Assert.Contains("studioVisibleTabs", result);
        Assert.Contains("Category !=", result);
    }

    [Fact]
    public void AddChangeClearRestoresOriginalCallbacksAndRemovesMetadata()
    {
        var first = TabFactionVisibility.Apply(Xml, Rules(("EMP", ["reikland"])));
        var second = TabFactionVisibility.Apply(first, Rules(("EMP", ["wissenland"])));
        Assert.DoesNotContain("reikland", Visibility(second, "emp"));
        var cleared = TabFactionVisibility.Apply(second, Rules(("EMP", [])));
        Assert.Empty(TabFactionVisibility.Read(cleared));
        Assert.True(XNode.DeepEquals(XDocument.Parse(Xml), XDocument.Parse(cleared)));
        var again = TabFactionVisibility.Apply(cleared, Rules(("EMP", ["reikland"])));
        Assert.Equal(Visibility(first, "emp"), Visibility(again, "emp"));
    }

    [Fact]
    public void ClearingOnePagePreservesOtherRestrictionsAndReapplyingIsIdempotent()
    {
        var first = TabFactionVisibility.Apply(Xml, Rules(("EMP", ["reikland"]), ("EMP2", ["wissenland"])));
        var second = TabFactionVisibility.Apply(first, Rules(("EMP", [])));
        Assert.Single(TabFactionVisibility.Read(second));
        Assert.Equal(Visibility(Xml, "emp"), Visibility(second, "emp"));
        Assert.Equal(second, TabFactionVisibility.Apply(second, Rules(("EMP2", ["wissenland"]))));
    }

    [Fact]
    public void RenameMovesRulesAndCloningDoesNotInheritDonorRestriction()
    {
        var first = TabFactionVisibility.Apply(Xml, Rules(("EMP", ["reikland"])));
        var doc = XDocument.Parse(first);
        var donor = doc.Descendants().Single(e => (string?)e.Attribute("id") == "holder_tab_emp");
        var clone = new XElement(donor); clone.Name = "holder_tab_clone"; clone.SetAttributeValue("id", "holder_tab_clone"); clone.SetAttributeValue("this", "new-guid");
        donor.AddAfterSelf(clone);
        var second = TabFactionVisibility.Apply(doc.ToString(), Rules(), newTabs: [("CLONE", "EMP")]);
        Assert.Equal(Visibility(Xml, "emp"), Visibility(second, "clone"));
        Assert.False(TabFactionVisibility.Read(second).ContainsKey("CLONE"));
        var renamedXml = second.Replace("holder_tab_emp\"", "holder_tab_renamed\"");
        var renamed = TabFactionVisibility.Apply(renamedXml, Rules(), renames: [("EMP", "RENAMED")]);
        Assert.Equal(["reikland"], TabFactionVisibility.Read(renamed)["RENAMED"]);
        Assert.False(TabFactionVisibility.Read(renamed).ContainsKey("EMP"));
    }

    [Fact]
    public void InvalidOrUnsupportedLayoutsAreRejectedInsteadOfPartiallySaving()
    {
        Assert.Throws<ArgumentException>(() => TabFactionVisibility.Apply(Xml, Rules(("EMP", ["bad\"key"]))));
        Assert.Throws<InvalidDataException>(() => TabFactionVisibility.Apply(Xml, Rules(("MISSING", ["reikland"]))));
        Assert.Throws<InvalidDataException>(() => TabFactionVisibility.Apply(Xml.Replace("FirstValidUpgradeCategoryForUnit", "UnknownAutoSelection"), Rules(("EMP", ["reikland"]))));
        Assert.Throws<InvalidDataException>(() => TabFactionVisibility.Read(Xml.Replace("<root>", "<root><!--WarbandStudio.TabFactionVisibility.v1:broken-->")));
    }

    [Fact]
    public void RaceOnlyAndMixedListsUseUnionInAllThreeCallbacks()
    {
        var raceOnly = TabFactionVisibility.Apply(Xml, Rules(), raceEdits: Rules(("EMP", ["wh_main_sc_emp_empire"])));
        Assert.Equal(["wh_main_sc_emp_empire"], TabFactionVisibility.ReadRaces(raceOnly)["EMP"]);
        Assert.Empty(TabFactionVisibility.Read(raceOnly));
        var mixed = TabFactionVisibility.Apply(raceOnly, Rules(("EMP", ["special_faction"])));
        var conditions = XDocument.Parse(mixed).Descendants("callback_with_context")
            .Select(e => (string?)e.Attribute("context_function_id") ?? "")
            .Where(e => e.Contains("SubcultureContext.Key")).ToArray();
        Assert.Equal(3, conditions.Length);
        Assert.All(conditions, e => Assert.Contains("FactionRecordContext.Key == \"special_faction\" || SelectedCharacter.MilitaryForceContext.FactionContext.SubcultureContext.Key == \"wh_main_sc_emp_empire\"", e));
        Assert.Equal(Visibility(Xml, "emp2"), Visibility(mixed, "emp2"));
        Assert.Equal(mixed, TabFactionVisibility.Apply(mixed, Rules(), raceEdits: Rules(("EMP", ["wh_main_sc_emp_empire"]))));
        var factionOnly = TabFactionVisibility.Apply(mixed, Rules(), raceEdits: Rules(("EMP", [])));
        Assert.DoesNotContain("SubcultureContext.Key", factionOnly);
        Assert.Contains("special_faction", Visibility(factionOnly, "emp"));
        var cleared = TabFactionVisibility.Apply(factionOnly, Rules(("EMP", [])));
        Assert.True(XNode.DeepEquals(XDocument.Parse(Xml), XDocument.Parse(cleared)));
    }

    [Fact]
    public void ClearingFactionsRetainsRaceAndRaceOnlyRenameAndCloneAreSafe()
    {
        var mixed = TabFactionVisibility.Apply(Xml, Rules(("EMP", ["reikland"])), raceEdits: Rules(("EMP", ["empire"])));
        var raceOnly = TabFactionVisibility.Apply(mixed, Rules(("EMP", [])));
        Assert.Contains("SubcultureContext.Key", Visibility(raceOnly, "emp"));
        var doc = XDocument.Parse(raceOnly);
        var donor = doc.Descendants().Single(e => (string?)e.Attribute("id") == "holder_tab_emp");
        var clone = new XElement(donor); clone.SetAttributeValue("id", "holder_tab_clone"); clone.SetAttributeValue("this", "clone-guid"); donor.AddAfterSelf(clone);
        var cloned = TabFactionVisibility.Apply(doc.ToString(), Rules(), newTabs: [("CLONE", "EMP")]);
        Assert.Equal(Visibility(Xml, "emp"), Visibility(cloned, "clone"));
        var renamed = TabFactionVisibility.Apply(cloned.Replace("holder_tab_emp\"", "holder_tab_renamed\""), Rules(), renames: [("EMP", "RENAMED")]);
        Assert.False(TabFactionVisibility.ReadRaces(renamed).ContainsKey("EMP"));
        Assert.Equal(["empire"], TabFactionVisibility.ReadRaces(renamed)["RENAMED"]);
        Assert.Throws<ArgumentException>(() => TabFactionVisibility.Apply(Xml, Rules(), raceEdits: Rules(("EMP", ["bad\"race"]))));
        Assert.Throws<InvalidDataException>(() => TabFactionVisibility.Apply(Xml, Rules(), raceEdits: Rules(("MISSING", ["empire"]))));
    }

    [Fact]
    public void ExclusionHidesIntersectionAndRetainsOthersInAllCallbacks()
    {
        var modes = new Dictionary<string, bool> { ["EMP"] = true };
        var denied = TabFactionVisibility.Apply(Xml, Rules(("EMP", ["reikland"])), raceEdits: Rules(("EMP", ["empire"])), excludedEdits: modes);
        var match = "((SelectedCharacter.MilitaryForceContext.FactionContext.SubcultureContext.Key == \"empire\") && (SelectedCharacter.MilitaryForceContext.FactionContext.FactionRecordContext.Key == \"reikland\")) == false";
        var callbacks = XDocument.Parse(denied).Descendants("callback_with_context")
            .Select(e => (string?)e.Attribute("context_function_id") ?? "").Where(e => e.Contains("SubcultureContext.Key")).ToArray();
        Assert.Equal(3, callbacks.Length);
        Assert.All(callbacks, e => Assert.Contains(match, e));
        Assert.True(TabFactionVisibility.ReadExcluded(denied)["EMP"]);
        Assert.Equal(Visibility(Xml, "emp2"), Visibility(denied, "emp2"));
        Assert.Equal(denied, TabFactionVisibility.Apply(denied, Rules(), excludedEdits: modes));
        var allowed = TabFactionVisibility.Apply(denied, Rules(), excludedEdits: new Dictionary<string, bool> { ["EMP"] = false });
        Assert.Empty(TabFactionVisibility.ReadExcluded(allowed));
        Assert.DoesNotContain(") == false", Visibility(allowed, "emp"));
        Assert.Equal(["reikland"], TabFactionVisibility.Read(allowed)["EMP"]);
        Assert.Equal(["empire"], TabFactionVisibility.ReadRaces(allowed)["EMP"]);
        var raceOnly = TabFactionVisibility.Apply(denied, Rules(("EMP", [])));
        Assert.Contains("SubcultureContext.Key == \"empire\")) == false", Visibility(raceOnly, "emp"));
        var noRace = TabFactionVisibility.Apply(denied, Rules(), raceEdits: Rules(("EMP", [])));
        Assert.Contains("FactionRecordContext.Key == \"reikland\")) == false", Visibility(noRace, "emp"));
        Assert.DoesNotContain("SubcultureContext.Key", Visibility(noRace, "emp"));
        Assert.Throws<InvalidDataException>(() => TabFactionVisibility.Apply(Xml, Rules(), excludedEdits: new Dictionary<string, bool> { ["MISSING"] = true }));
    }

    [Fact]
    public void EmptyExclusionHidesNobodyAndSurvivesRenameWithoutLeakingToClone()
    {
        var empty = TabFactionVisibility.Apply(Xml, Rules(), excludedEdits: new Dictionary<string, bool> { ["EMP"] = true });
        Assert.Equal("(OriginalCondition && HasUpgrades) && (true)", Visibility(empty, "emp"));
        Assert.True(TabFactionVisibility.ReadExcluded(empty)["EMP"]);
        var doc = XDocument.Parse(empty);
        var donor = doc.Descendants().Single(e => (string?)e.Attribute("id") == "holder_tab_emp");
        var clone = new XElement(donor); clone.SetAttributeValue("id", "holder_tab_clone"); clone.SetAttributeValue("this", "clone-guid"); donor.AddAfterSelf(clone);
        var cloned = TabFactionVisibility.Apply(doc.ToString(), Rules(), newTabs: [("CLONE", "EMP")]);
        Assert.Equal(Visibility(Xml, "emp"), Visibility(cloned, "clone"));
        Assert.False(TabFactionVisibility.ReadExcluded(cloned).ContainsKey("CLONE"));
        var renamed = TabFactionVisibility.Apply(cloned.Replace("holder_tab_emp\"", "holder_tab_renamed\""), Rules(), renames: [("EMP", "RENAMED")]);
        Assert.True(TabFactionVisibility.ReadExcluded(renamed)["RENAMED"]);
        Assert.False(TabFactionVisibility.ReadExcluded(renamed).ContainsKey("EMP"));
        var restored = TabFactionVisibility.Apply(empty, Rules(), excludedEdits: new Dictionary<string, bool> { ["EMP"] = false });
        Assert.True(XNode.DeepEquals(XDocument.Parse(Xml), XDocument.Parse(restored)));
        var edits = new WarbandEdits(); edits.TabVisibilityExcluded["EMP"] = true;
        var copy = edits.Clone(); edits.TabVisibilityExcluded["EMP"] = false;
        Assert.True(copy.TabVisibilityExcluded["EMP"]); Assert.False(edits.IsEmpty);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public void VisibilityTruthTableCoversRaceOnlyFactionOnlyIntersectionAndEmpty(bool selectRace, bool selectFaction, bool excluded)
    {
        var result = TabFactionVisibility.Apply(Xml, Rules(("EMP", selectFaction ? ["reikland", "wissenland"] : [])),
            raceEdits: Rules(("EMP", selectRace ? ["empire", "bretonnia"] : [])),
            excludedEdits: new Dictionary<string, bool> { ["EMP"] = excluded });
        foreach (var (race, faction) in new[] { ("empire", "reikland"), ("empire", "wissenland"),
            ("empire", "gold_order"), ("bretonnia", "couronne"), ("dwarfs", "karaz_a_karak"), ("dwarfs", "reikland") })
        {
            // 解析导出的布尔条件，验证族外、族内其他派系不会因反选被意外隐藏。
            var expression = Visibility(result, "emp").Replace("OriginalCondition", "true").Replace("HasUpgrades", "true")
                .Replace("SelectedCharacter.MilitaryForceContext.FactionContext.FactionRecordContext.Key", $"'{faction}'")
                .Replace("SelectedCharacter.MilitaryForceContext.FactionContext.SubcultureContext.Key", $"'{race}'")
                .Replace('"', '\'').Replace("==", "=").Replace("&&", " AND ").Replace("||", " OR ");
            using var evaluator = new System.Data.DataTable();
            var actual = (bool)evaluator.Compute(expression, "");
            var matchesRace = race is "empire" or "bretonnia";
            var matchesFaction = faction is "reikland" or "wissenland";
            var expected = excluded
                ? !(selectRace || selectFaction) || !((!selectRace || matchesRace) && (!selectFaction || matchesFaction))
                : !(selectRace || selectFaction) || selectRace && matchesRace || selectFaction && matchesFaction;
            Assert.True(expected == actual, $"race={race}, faction={faction}, selectedRace={selectRace}, selectedFaction={selectFaction}, excluded={excluded}");
        }
    }

    [Fact]
    public void LegacyFactionMetadataWithoutRacesStillLoadsAndCanBeExtended()
    {
        var legacy = XDocument.Parse(TabFactionVisibility.Apply(Xml, Rules(("EMP", ["reikland"]))));
        var comment = legacy.DescendantNodes().OfType<XComment>().Single();
        var split = comment.Value.IndexOf(':') + 1;
        var data = System.Text.Json.Nodes.JsonNode.Parse(Convert.FromBase64String(comment.Value[split..]))!.AsObject();
        data.Remove("Races");
        data.Remove("Excluded");
        comment.Value = comment.Value[..split] + Convert.ToBase64String(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(data));
        Assert.Empty(TabFactionVisibility.ReadRaces(legacy.ToString()));
        Assert.Empty(TabFactionVisibility.ReadExcluded(legacy.ToString()));
        var updated = TabFactionVisibility.Apply(legacy.ToString(), Rules(), raceEdits: Rules(("EMP", ["empire"])));
        Assert.Equal(["reikland"], TabFactionVisibility.Read(updated)["EMP"]);
        Assert.Contains("SubcultureContext.Key", Visibility(updated, "emp"));
    }

    [Fact]
    public void UndoSnapshotsOwnTheirArraysAndEmptyOverrideIsStillAnEdit()
    {
        var edits = new WarbandEdits(); edits.TabAllowedFactions["EMP"] = ["reikland"];
        var snapshot = edits.Clone(); edits.TabAllowedFactions["EMP"][0] = "wissenland";
        Assert.Equal("reikland", snapshot.TabAllowedFactions["EMP"][0]);
        edits.TabAllowedFactions["EMP"] = [];
        Assert.False(edits.IsEmpty); Assert.Contains("页签显示派系", edits.Summary());
        var races = new WarbandEdits(); races.TabAllowedRaces["EMP"] = ["empire"];
        var copy = races.Clone(); races.TabAllowedRaces["EMP"][0] = "dwarfs";
        Assert.Equal(["empire"], copy.TabAllowedRaces["EMP"]);
        races.TabAllowedRaces["EMP"] = [];
        Assert.False(races.IsEmpty); Assert.Contains("页签显示种族", races.Summary());
    }
}
