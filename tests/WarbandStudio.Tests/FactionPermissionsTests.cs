using WarbandStudio.Pack;

namespace WarbandStudio.Tests;

public class FactionPermissionsTests
{
    [Fact]
    public void SharedMilitaryGroupDoesNotShareExclusivePermissions()
    {
        var result = FactionPermissions.ResolvePositive([("cannon", "mors", true), ("cannon", "eshin", true), ("guard", "shanlin", false)]);
        Assert.Equal(2, result["cannon"].Count);
        Assert.Contains("mors", result["cannon"]);
        Assert.DoesNotContain("skryre", result["cannon"]);
        Assert.False(result.ContainsKey("guard"));
    }

    [Fact]
    public void ModOverrideReplacesOnlyTheSameUnitFactionPair()
    {
        var result = FactionPermissions.ResolvePositive([("U", "F1", true), ("u", "F2", true), ("u", "f1", false), ("U", "f3", false), ("u", "F3", true)]);
        Assert.False(result["u"].Contains("f1"));
        Assert.True(result["U"].SetEquals(["f2", "f3"]));
    }
}
