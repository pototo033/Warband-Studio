using System.Text.Json.Nodes;
using WarbandStudio.Pack;

namespace WarbandStudio.Ui.Services;

public sealed partial class Backend
{
    private Dictionary<string, string[]> EffectiveTabAllowedFactions()
        => EffectiveTabVisibility(TabFactionVisibility.Read(TwuiText()), Edits.TabAllowedFactions);

    private Dictionary<string, string[]> EffectiveTabAllowedRaces()
        => EffectiveTabVisibility(TabFactionVisibility.ReadRaces(TwuiText()), Edits.TabAllowedRaces);

    private Dictionary<string, bool> EffectiveTabExcluded()
    {
        var values = TabFactionVisibility.ReadExcluded(TwuiText());
        foreach (var (oldKey, newKey) in Edits.TabRenames)
            if (values.Remove(oldKey, out var excluded)) values[newKey] = excluded;
        foreach (var (category, excluded) in Edits.TabVisibilityExcluded)
            if (excluded) values[category] = true; else values.Remove(category);
        return values;
    }

    private Dictionary<string, string[]> EffectiveTabVisibility(Dictionary<string, string[]> values, Dictionary<string, string[]> overrides)
    {
        foreach (var (oldKey, newKey) in Edits.TabRenames)
            if (values.Remove(oldKey, out var factions)) values[newKey] = factions;
        foreach (var (category, factions) in overrides)
        {
            if (factions.Length == 0) values.Remove(category);
            else values[category] = factions.ToArray();
        }
        return values;
    }

    public void SetTabAllowedFactions(string category, IEnumerable<string> factions)
        => SetTabVisibility(category, factions, null);

    public void SetTabVisibility(string category, IEnumerable<string> factions, IEnumerable<string>? races, bool? excluded = null)
    {
        if (string.IsNullOrWhiteSpace(category)) throw new ArgumentException("请先选择页签。");
        var selected = TabFactionVisibility.Normalize(factions);
        var selectedRaces = races is null ? null : TabFactionVisibility.Normalize(races);
        // 先确认已有配置能读取，避免损坏配置被当作不限派系覆盖掉。
        _ = EffectiveTabAllowedFactions();
        _ = EffectiveTabAllowedRaces();
        _ = EffectiveTabExcluded();
        if (!Edits.NewTabs.Any(t => t.Key.Equals(category, StringComparison.OrdinalIgnoreCase))
            && !TwuiTabs.HasStructure(TwuiText(), PackSideKey(category)))
            throw new InvalidOperationException("该页签缺少 TWUI 结构，请先补全页签。");
        Edits.TabAllowedFactions[category] = selected;
        if (selectedRaces is not null) Edits.TabAllowedRaces[category] = selectedRaces;
        if (excluded is not null) Edits.TabVisibilityExcluded[category] = excluded.Value;
        InvalidateCanvas();
        Log?.Invoke($"页签 {category} 显示名单：{selected.Length} 个派系、{EffectiveTabAllowedRaces().GetValueOrDefault(category, []).Length} 个种族；反选={EffectiveTabExcluded().GetValueOrDefault(category)}，空名单不限制（不改兵种授权，待保存）");
    }

    private JsonObject BuildTabVisibilityData()
    {
        var result = new JsonObject { ["known"] = false };
        try
        {
            var selected = new JsonObject();
            foreach (var (category, factions) in EffectiveTabAllowedFactions()) selected[category] = ToJsonArray(factions);
            var catalog = new JsonArray();
            foreach (var f in LoadFactions().Item1.OrderByDescending(f => f.Legendary).ThenBy(f => f.Key, StringComparer.OrdinalIgnoreCase))
                catalog.Add(new JsonObject { ["key"] = f.Key, ["name"] = f.Name ?? f.Key,
                    ["race"] = f.Subculture, ["legendary"] = f.Legendary });
            result["allowed"] = selected;
            var races = new JsonObject();
            foreach (var (category, values) in EffectiveTabAllowedRaces()) races[category] = ToJsonArray(values);
            result["allowedRaces"] = races;
            var excluded = new JsonObject();
            foreach (var (category, value) in EffectiveTabExcluded()) excluded[category] = value;
            result["excluded"] = excluded;
            result["factions"] = catalog;
            result["known"] = true;
        }
        catch (Exception ex)
        {
            result["error"] = ex.Message;
            Log?.Invoke("页签显示派系读取失败：" + ex.Message);
        }
        return result;
    }
}
