namespace WarbandStudio.Pack;

/// <summary>
/// 新建"单位组"的命名（用户 2026-10-24 调整的规则）：
/// `<c>&lt;用户填的 key&gt;_&lt;页签 key&gt;_&lt;兵种词…&gt;</c>`（一个组里有多个兵就依次往后接）。
///
/// 普通单位取最后两个词，含独立 ror 段的精英单位取最后三个词；纯数字段不算词。
/// 例如 `wh2_main_skv_inf_clanrat_1` → `inf_clanrat`，`Yukino_Night_Runners_ror_1` → `Night_Runners_ror`。
///
/// 用户没填 key（全局选项里留空）时返回 null —— 调用方退回旧的时间戳命名（studio_new_…）。
/// **前端和后端都要用同一条规则**：后端（Backend.MakeGroupKey）负责所有"程序自己起名"的入口；
/// 画布上"新建分组"的默认值只用到 `<前缀>_<页签>`（空组没有兵），在 canvas.html 里直接拼。
/// </summary>
public static class GroupNaming
{
    /// <summary>按规则拼一个新组名（撞名自动 _2/_3…）。<paramref name="taken"/> 由调用方给
    /// —— 包里已有的组 + 本会话新建的组都算占用。用户没填 key 时返回 null。</summary>
    public static string? NewKey(string? userKey, string? category, IEnumerable<string> units, Func<string, bool> taken)
    {
        var prefix = (userKey ?? "").Trim().Trim('_');
        if (prefix.Length == 0) return null;
        var name = prefix;
        if (!string.IsNullOrWhiteSpace(category)) name += "_" + category.Trim().Trim('_');
        foreach (var u in units)
        {
            var w = UnitWords(u);
            if (w.Length > 0) name += "_" + w;
        }
        return Unique(name, taken);
    }

    /// <summary>撞名就往后找空位：`name` → `name_2` → `name_3`…（组名是表主键，重了会把两个组的数据混在一起）。</summary>
    public static string Unique(string name, Func<string, bool> taken)
    {
        if (!taken(name)) return name;
        for (var k = 2; k <= 99; k++)
        {
            var cand = $"{name}_{k}";
            if (!taken(cand)) return cand;
        }
        return $"{name}_x";
    }

    /// <summary>跳过数字变体号，普通单位取两个词，精英单位取三个词；不足时保留所有可用词。</summary>
    public static string UnitWords(string unit)
    {
        var parts = (unit ?? "").Split('_', StringSplitOptions.RemoveEmptyEntries);
        var words = parts.Where(p => !p.All(char.IsDigit)).ToArray();
        if (words.Length == 0) return parts.Length > 0 ? parts[^1] : "";
        var count = words.Any(p => p.Equals("ror", StringComparison.OrdinalIgnoreCase)) ? 3 : 2;
        return string.Join("_", words.TakeLast(count));
    }
}
