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
    public void UndoSnapshotsOwnTheirArraysAndEmptyOverrideIsStillAnEdit()
    {
        var edits = new WarbandEdits(); edits.TabAllowedFactions["EMP"] = ["reikland"];
        var snapshot = edits.Clone(); edits.TabAllowedFactions["EMP"][0] = "wissenland";
        Assert.Equal("reikland", snapshot.TabAllowedFactions["EMP"][0]);
        edits.TabAllowedFactions["EMP"] = [];
        Assert.False(edits.IsEmpty); Assert.Contains("页签显示派系", edits.Summary());
    }
}
