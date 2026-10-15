using WarbandStudio.Pack;

namespace WarbandStudio.Tests;

public class MilitaryPermissionsTests
{
    [Fact]
    public void RemovingOneGroupPreservesOtherGroupsForSameUnit()
    {
        var result = MilitaryPermissions.Resolve([("u", "common"), ("u", "special")], [("u", "common")], []);
        Assert.Equal(["special"], result["u"]);
    }

    [Fact]
    public void RemovingLastGroupRetainsUnitWithEmptyMembership()
    {
        var result = MilitaryPermissions.Resolve([("u", "common")], [("u", "common")], []);
        Assert.True(result.ContainsKey("u"));
        Assert.Empty(result["u"]);
    }

    [Fact]
    public void ReAddingAndCaseDifferencesDoNotDuplicateMembership()
    {
        var result = MilitaryPermissions.Resolve([("U", "Common"), ("other", "special")], [("u", "common")], [("u", "COMMON"), ("U", "common")]);
        Assert.Single(result["u"]);
        Assert.Contains("common", result["u"]);
        Assert.Contains("special", result["other"]);
    }
}
