namespace WarbandStudio.Pack;

/// <summary>同一单位的各军事组关系独立，未保存增删只影响目标组合键。</summary>
public static class MilitaryPermissions
{
    public static Dictionary<string, HashSet<string>> Resolve(IEnumerable<(string Unit, string Group)> rows,
        IEnumerable<(string Unit, string Group)> removed, IEnumerable<(string Unit, string Group)> added)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        void Add(string unit, string group)
        {
            if (string.IsNullOrWhiteSpace(unit) || string.IsNullOrWhiteSpace(group)) return;
            if (!result.TryGetValue(unit, out var groups)) result[unit] = groups = new(StringComparer.OrdinalIgnoreCase);
            groups.Add(group);
        }
        foreach (var (unit, group) in rows) Add(unit, group);
        foreach (var (unit, group) in removed)
            if (result.TryGetValue(unit, out var groups)) groups.Remove(group);
        foreach (var (unit, group) in added) Add(unit, group);
        return result;
    }
}
