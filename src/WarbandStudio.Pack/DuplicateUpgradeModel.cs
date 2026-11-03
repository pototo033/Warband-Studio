namespace WarbandStudio.Pack;

public sealed record UpgradeRuleOption(string Id, string Label);
public sealed record UpgradeRuleConflict(string Id, string Description, IReadOnlyList<UpgradeRuleOption> Options);
public sealed class UpgradeRuleConflictException(IReadOnlyList<UpgradeRuleConflict> conflicts)
    : InvalidOperationException("重复兵升级规则冲突，需要选择适用规则；原包与未保存编辑保持不变。")
{
    public IReadOnlyList<UpgradeRuleConflict> Conflicts { get; } = conflicts;
}

internal sealed record UpgradeScope(string[] Factions, string[] Races, bool Excluded)
{
    public bool Matches(string faction, string race)
    {
        var f = Factions.Contains(faction, StringComparer.OrdinalIgnoreCase);
        var r = Races.Contains(race, StringComparer.OrdinalIgnoreCase);
        if (Excluded) return !(Factions.Length > 0 && Races.Length > 0 ? f && r : f || r);
        return Factions.Length == 0 && Races.Length == 0 || f || r;
    }
}
internal sealed record UpgradeFee(string Key, string Scope, long Amount, string Factor, string Icon, string Name, bool AiIgnored);
internal sealed record UpgradeBill(long Gold, UpgradeFee[] Pools);
internal sealed record UpgradeProfile(string Id, UpgradeScope[] Scopes, UpgradeBill Bill);
internal sealed record UpgradePolicy(string Id, string Cost, long NativeGold, UpgradeProfile[] Profiles,
    Dictionary<string, string> Decisions, string[] Slots, bool Direct = false);
