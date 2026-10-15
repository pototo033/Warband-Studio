using WarbandStudio.Pack;

namespace WarbandStudio.Tests;

public class PermissionEditStateTests
{
    [Fact]
    public void ExclusiveAddThenAddKeepsOnePairAndOtherFactions()
    {
        var edits = new WarbandEdits();
        edits.SetExclusive("U", "F", true);
        edits.SetExclusive("u", "f", true);
        edits.SetExclusive("u", "other", true);
        Assert.Equal(2, edits.AddExclusive.Count);
        Assert.Empty(edits.RemoveExclusive);
    }

    [Fact]
    public void ExclusiveAddThenRemoveDoesNotResurrectInPreview()
    {
        var edits = new WarbandEdits();
        edits.SetExclusive("u", "f", true);
        edits.SetExclusive("U", "F", false);
        Assert.Empty(edits.AddExclusive);
        var permissions = FactionPermissions.ResolvePositive([("u", "other", true)], edits.RemoveExclusive, edits.AddExclusive);
        Assert.True(permissions["u"].SetEquals(["other"]));
    }

    [Fact]
    public void ExclusiveRemoveThenAddRestoresOnePairAndEnablesFalse()
    {
        var edits = new WarbandEdits();
        edits.SetExclusive("u", "f", false);
        edits.SetExclusive("U", "F", true);
        Assert.Empty(edits.RemoveExclusive);
        Assert.Single(edits.AddExclusive);
        Assert.Contains("f", FactionPermissions.ResolvePositive([("u", "f", false)], edits.RemoveExclusive, edits.AddExclusive)["u"]);
    }

    [Fact]
    public void UndoSnapshotPreservesBothChannelsAndOtherPendingChanges()
    {
        var edits = new WarbandEdits();
        edits.SetUnitGroup("u", "g", true);
        edits.SetExclusive("u", "f", true);
        edits.SetExclusive("u", "other", false);
        edits.NewTabs.Add(("tab", "donor", null, null));
        edits.TabScopes.Add(("tab", "race", "f"));
        edits.UnlockXp.Add("u");
        var saved = edits.Clone();
        edits.SetExclusive("u", "f", false);
        Assert.Equal(6, saved.Count);
        Assert.Single(saved.AddExclusive);
        Assert.Single(saved.RemoveExclusive);
        Assert.Single(saved.AddUnitGroup);
        Assert.Single(saved.NewTabs);
        Assert.Single(saved.TabScopes);
        Assert.Single(saved.UnlockXp);
        Assert.False(saved.IsEmpty);
    }
}
