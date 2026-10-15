namespace WarbandStudio.Pack;

/// <summary>派系专属表的正向许可；false 不授予该渠道，不能据此删除独立军事组关系。</summary>
public static class FactionPermissions
{
    public static Dictionary<string, HashSet<string>> ResolvePositive(IEnumerable<(string Unit, string Faction, bool Allowed)> rows,
        IEnumerable<(string Unit, string Faction)>? removed = null, IEnumerable<(string Unit, string Faction)>? added = null)
    {
        var pairs = new Dictionary<(string Unit, string Faction), bool>();
        foreach (var (unit, faction, allowed) in rows)
        {
            if (string.IsNullOrWhiteSpace(unit) || string.IsNullOrWhiteSpace(faction)) continue;
            pairs[(unit.ToLowerInvariant(), faction.ToLowerInvariant())] = allowed;
        }
        foreach (var (unit, faction) in removed ?? []) pairs.Remove((unit.ToLowerInvariant(), faction.ToLowerInvariant()));
        foreach (var (unit, faction) in added ?? []) pairs[(unit.ToLowerInvariant(), faction.ToLowerInvariant())] = true;
        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var ((unit, faction), allowed) in pairs)
        {
            if (!allowed) continue;
            if (!result.TryGetValue(unit, out var factions)) result[unit] = factions = new(StringComparer.OrdinalIgnoreCase);
            factions.Add(faction);
        }
        return result;
    }
}
