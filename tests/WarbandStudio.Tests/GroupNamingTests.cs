using WarbandStudio.Pack;

namespace WarbandStudio.Tests;

/// <summary>新组命名规则（用户 2026-10-07 定）：<c>&lt;前缀&gt;_&lt;页签&gt;_&lt;兵种词…&gt;</c>。</summary>
public class GroupNamingTests
{
    private static readonly Func<string, bool> None = _ => false;

    [Theory]
    [InlineData("wh2_main_skv_inf_clanrat_1", "inf_clanrat")]
    [InlineData("Yukino_Skv_Inf_Night_Runners", "Night_Runners")]
    [InlineData("wh_main_brt_art_field_trebuchet", "field_trebuchet")]
    [InlineData("wh2_dlc11_cst_inf_zombie_gunnery_mob_ror_0", "gunnery_mob_ror")]
    [InlineData("wh2_dlc11_cst_mon_rotting_leviathan_ROR_0", "rotting_leviathan_ROR")]
    [InlineData("wh2_cst_inf_zombie_0_ror_1", "inf_zombie_ror")]
    [InlineData("unit_ror_0", "unit_ror")]
    [InlineData("some_mirror_warrior_0", "mirror_warrior")]
    [InlineData("unit_0_1", "unit")]                          // 结尾全是数字 → 退到最后一个有意义的词
    [InlineData("999", "999")]                                // 全数字 → 兜底用尾段
    [InlineData("", "")]
    public void UnitWords_普通两词精英三词且忽略数字(string unit, string want) =>
        Assert.Equal(want, GroupNaming.UnitWords(unit));

    [Fact]
    public void NewKey_前缀_页签_兵种词_多兵依次接()
    {
        var k = GroupNaming.NewKey("Yukino", "SKV",
            new[] { "wh2_main_skv_inf_clanrat_1", "Yukino_Skv_Inf_Night_Runners" }, None);
        Assert.Equal("Yukino_SKV_inf_clanrat_Night_Runners", k);
    }

    [Fact]
    public void NewKey_空组_只有前缀和页签() =>
        Assert.Equal("Yukino_SKV", GroupNaming.NewKey("Yukino", "SKV", Array.Empty<string>(), None));

    [Fact]
    public void NewKey_前缀两头的下划线会被清掉() =>
        Assert.Equal("Yukino_SKV_a_clanrat", GroupNaming.NewKey("_Yukino_", "SKV", new[] { "a_clanrat_1" }, None));

    [Fact]
    public void NewKey_没配前缀_返回null_由调用方退回旧命名() =>
        Assert.Null(GroupNaming.NewKey("  ", "SKV", new[] { "u_1" }, None));

    [Fact]
    public void NewKey_撞名自动加序号()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Yukino_SKV_a_clanrat", "Yukino_SKV_a_clanrat_2",
        };
        Assert.Equal("Yukino_SKV_a_clanrat_3", GroupNaming.NewKey("Yukino", "SKV", new[] { "a_clanrat" }, taken.Contains));
    }
}
