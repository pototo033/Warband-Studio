namespace WarbandStudio.Pack;

/// <summary>
/// 新建"单位组"的命名（用户 2026-10-07 定的规则）：
/// `<c>&lt;用户填的 key&gt;_&lt;页签 key&gt;_&lt;兵种词…&gt;</c>`（一个组里有多个兵就依次往后接）。
///
/// 兵种词 = unit key **去掉结尾纯数字段后的最后一段**：`wh2_main_skv_inf_clanrat_1` → `clanrat`、
/// `Yukino_Skv_Inf_Night_Runners` → `Runners`（结尾的 `_0`/`_1` 是变体号，不算"词"）。
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
            var w = LastWord(u);
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

    /// <summary>兵种 key 的"最后一个词"：跳过结尾的纯数字段（变体号），取最后一段有意义的词。</summary>
    public static string LastWord(string unit)
    {
        var parts = (unit ?? "").Split('_', StringSplitOptions.RemoveEmptyEntries);
        for (var i = parts.Length - 1; i >= 0; i--)
            if (!parts[i].All(char.IsDigit)) return parts[i];
        return parts.Length > 0 ? parts[^1] : "";
    }
}
