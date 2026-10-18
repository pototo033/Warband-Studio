using System.Text.Json.Nodes;
using WarbandStudio.Pack;

namespace WarbandStudio.Ui.Services;

public sealed partial class Backend
{
    private Dictionary<string, string[]> EffectiveTabAllowedFactions()
    {
        var values = TabFactionVisibility.Read(TwuiText());
        foreach (var (oldKey, newKey) in Edits.TabRenames)
            if (values.Remove(oldKey, out var factions)) values[newKey] = factions;
        foreach (var (category, factions) in Edits.TabAllowedFactions)
        {
            if (factions.Length == 0) values.Remove(category);
            else values[category] = factions.ToArray();
        }
        return values;
    }

    public void SetTabAllowedFactions(string category, IEnumerable<string> factions)
    {
        if (string.IsNullOrWhiteSpace(category)) throw new ArgumentException("请先选择页签。");
        var selected = TabFactionVisibility.Normalize(factions);
        // 先确认已有配置能读取，避免损坏配置被当作不限派系覆盖掉。
        _ = EffectiveTabAllowedFactions();
        if (!Edits.NewTabs.Any(t => t.Key.Equals(category, StringComparison.OrdinalIgnoreCase))
            && !TwuiTabs.HasStructure(TwuiText(), PackSideKey(category)))
            throw new InvalidOperationException("该页签缺少 TWUI 结构，请先补全页签。");
        Edits.TabAllowedFactions[category] = selected;
        InvalidateCanvas();
        Log?.Invoke($"页签 {category} 显示派系：{(selected.Length == 0 ? "不限制" : string.Join("、", selected))}（不改兵种授权，待保存）");
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
