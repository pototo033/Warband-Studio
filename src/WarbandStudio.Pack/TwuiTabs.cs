using System.Text.RegularExpressions;

namespace WarbandStudio.Pack;

/// <summary>
/// twui（`ui/campaign ui/warband_upgrades.twui.xml`）里"**页签 → 实际用的图**"的解析。
///
/// 一个页签在 twui 里是一整套（见 <see cref="WarbandNewTab"/>）：
///   ① hierarchy 节点 `holder_tab_&lt;key&gt;`（里面挂着 `button_toggle_tab_&lt;x&gt;`）
///   ② `holder_tab_&lt;key&gt;` 本体（God 属性 = 页签 key，控制"这个页签有没有内容"）
///   ③ `button_toggle_tab_&lt;x&gt;` 按钮组件（按钮图写在 component_image_uniqueguid 的 name 里）
///   ④ `warband_upgrades` 组件 `&lt;states&gt;` 里**以页签 key 命名的状态** `&lt;key 小写 name="key 小写"&gt;`
///      —— 这就是页签选中时的**面板背景**（游戏按页签 key 切这个状态）
///   ⑤ 该状态里的 `&lt;image componentimage=GUID&gt;` → `&lt;component_image imagepath="…/background_images_&lt;y&gt;.png"/&gt;`
///
/// **图文件名不必等于页签 key**：改名撞名会加 `_1`/`_2` 后缀；历史遗留（老版本改名只改了 holder_tab）
/// 还会出现"页签 SKV2 的状态/按钮组件叫 skvg"这种不一致。所以"这个页签用哪张图"**只能从 twui 解析**，
/// 按约定名 `&lt;前缀&gt;&lt;key&gt;.png` 猜 = 换图写到页签根本不看的那个文件上（用户实测"换图没生效"）。
/// </summary>
public static class TwuiTabs
{
    /// <summary>一个页签的图信息（都是**文件名**，不含目录；解析不到就是 null）。</summary>
    /// <param name="PanelState">④ 里这个页签实际用的状态名（正常情况下 = key 小写；遗留可能不同）。</param>
    public sealed record TabArt(string Key, string? BgFile, string? BtnFile, string? BtnComponent, string? PanelState);

    public const string SkinDir = "ui/skins/default/warband_upgrades/";

    /// <summary>约定名：`background_images_&lt;key 小写&gt;.png` / `button_upgrade_&lt;key 小写&gt;.png`。</summary>
    public static string ConventionName(string prefix, string key) => prefix + key.ToLowerInvariant() + ".png";

    /// <summary>
    /// 改名后这张图**应该叫什么**：`&lt;前缀&gt;&lt;新 key 小写&gt;.png`；被占用就 `_1`、`_2`…（绝不覆盖）。
    /// <paramref name="taken"/> 由调用方给（包里有的 + 本轮待导出会新增的都算"占用"）。
    /// **UI 解析和落表共用这一条规则** —— 两边各算一次就会出现"界面说这张、包里改成那张"。
    /// </summary>
    public static string PlanTargetName(string prefix, string newKey, Func<string, bool> taken)
    {
        var baseName = prefix + newKey.ToLowerInvariant();
        var name = baseName + ".png";
        for (var k = 1; k <= 99 && taken(name); k++) name = $"{baseName}_{k}.png";
        return name;
    }

    /// <summary>从一段 twui 文本解析出"页签 key（原样大小写）→ 图信息"。解析不了的部分留 null，由调用方兜底。</summary>
    public static Dictionary<string, TabArt> Parse(string? twuiXml)
    {
        var map = new Dictionary<string, TabArt>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(twuiXml)) return map;

        // ① GUID → 图路径（面板状态的 <image componentimage="GUID"> 靠它落地）
        var guidToPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(twuiXml, @"<component_image\b[^>]*?/>"))
        {
            var t = Regex.Match(m.Value, "this=\"([^\"]+)\"");
            var p = Regex.Match(m.Value, "imagepath=\"([^\"]+)\"");
            if (t.Success && p.Success) guidToPath[t.Groups[1].Value] = p.Groups[1].Value;
        }

        // ② 面板状态：**自命名**元素（<skvg … name="skvg">）且第一张图是 background_images_*.png
        //    注意缩进不齐（工具建的页签插进去的是两个制表符）→ 不能按缩进找，只能按"自命名 + 图"认。
        var panelByState = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(twuiXml, @"<([A-Za-z0-9_]+)\s+[^>]*?\bname=""([A-Za-z0-9_\.]+)"""))
        {
            var tag = m.Groups[1].Value;
            if (!tag.Equals(m.Groups[2].Value, StringComparison.OrdinalIgnoreCase)) continue;
            if (panelByState.ContainsKey(tag)) continue;
            var close = twuiXml.IndexOf("</" + tag + ">", m.Index, StringComparison.OrdinalIgnoreCase);
            var block = twuiXml[m.Index..(close > m.Index ? close : Math.Min(twuiXml.Length, m.Index + 4000))];
            var img = Regex.Match(block, @"componentimage=""([^""]+)""");
            if (!img.Success || !guidToPath.TryGetValue(img.Groups[1].Value, out var path)) continue;
            if (path.IndexOf("background_images_", StringComparison.OrdinalIgnoreCase) < 0) continue;   // 别的状态（default/hover…）
            panelByState[tag] = FileName(path);
        }

        // ③ hierarchy 段：holder_tab_<key> 节点里挂着哪个按钮组件（组件名可能和 key 不一样）
        var holderBtn = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var hierStart = twuiXml.IndexOf("<hierarchy>", StringComparison.Ordinal);
        var hierEnd = twuiXml.IndexOf("</hierarchy>", StringComparison.Ordinal);
        if (hierStart >= 0 && hierEnd > hierStart)
        {
            var hier = twuiXml[hierStart..hierEnd];
            foreach (Match m in Regex.Matches(hier, @"<holder_tab_([A-Za-z0-9_]+)\b[^>]*>(.*?)</holder_tab_\1>",
                                              RegexOptions.Singleline))
            {
                var btn = Regex.Match(m.Groups[2].Value, @"<button_toggle_tab_([A-Za-z0-9_]+)\b");
                if (btn.Success) holderBtn[m.Groups[1].Value] = btn.Groups[1].Value;
            }
        }

        // ④ 按钮组件（按 id="button_toggle_tab_X" 定位**定义**，别抓到 hierarchy 里的引用）→ 按钮图
        var btnByComponent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(twuiXml, @"<button_toggle_tab_([A-Za-z0-9_]+)\b[^>]*?\bid=""button_toggle_tab_\1""",
                                          RegexOptions.IgnoreCase))
        {
            var name = m.Groups[1].Value;
            if (btnByComponent.ContainsKey(name)) continue;
            var close = twuiXml.IndexOf("</button_toggle_tab_" + name + ">", m.Index, StringComparison.OrdinalIgnoreCase);
            var block = twuiXml[m.Index..(close > m.Index ? close : Math.Min(twuiXml.Length, m.Index + 4000))];
            // 按钮图：component_image_uniqueguid 的 name 是**反斜杠**形式的完整路径
            var img = Regex.Match(block, @"name=""[^""]*warband_upgrades[\\/](button_upgrade_[^""\\/]+\.png)""",
                                  RegexOptions.IgnoreCase);
            if (img.Success) btnByComponent[name] = img.Groups[1].Value;
        }

        // ⑤ 页签清单 = hierarchy 里的 holder_tab_*（和工具 TabKeys() 同一口径）
        foreach (var (holder, btnComp) in holderBtn)
        {
            btnByComponent.TryGetValue(btnComp, out var btnFile);
            var state = panelByState.ContainsKey(holder) ? holder
                      : panelByState.ContainsKey(btnComp) ? btnComp
                      : StemOf(btnFile);
            panelByState.TryGetValue(state ?? "", out var bgFile);
            map[holder] = new TabArt(holder, bgFile, btnFile, btnComp, state);
        }

        // ⑥ 兜底：上面没解析到图的，直接在全文里按"key 精确 → key_N"扫一遍
        foreach (var key in map.Keys.ToList())
        {
            var a = map[key];
            map[key] = a with
            {
                BgFile = a.BgFile ?? ScanName(twuiXml, "background_images_", key),
                BtnFile = a.BtnFile ?? ScanName(twuiXml, "button_upgrade_", key),
            };
        }
        return map;
    }

    /// <summary>全文里按 `&lt;前缀&gt;&lt;key&gt;.png`（优先）或 `&lt;前缀&gt;&lt;key&gt;_N.png` 找图文件名。</summary>
    public static string? ScanName(string? twuiXml, string prefix, string key)
    {
        if (string.IsNullOrWhiteSpace(twuiXml) || string.IsNullOrWhiteSpace(key)) return null;
        string? suffixed = null;
        foreach (Match m in Regex.Matches(twuiXml, Regex.Escape(prefix) + @"([A-Za-z0-9_\-]+)\.png", RegexOptions.IgnoreCase))
        {
            var stem = m.Groups[1].Value;
            if (stem.Equals(key, StringComparison.OrdinalIgnoreCase)) return prefix + stem + ".png";
            if (suffixed is null && stem.StartsWith(key + "_", StringComparison.OrdinalIgnoreCase)) suffixed = prefix + stem + ".png";
        }
        return suffixed;
    }

    /// <summary>某个页签的图信息（key 大小写不敏感）。</summary>
    public static TabArt? Of(string? twuiXml, string key) =>
        Parse(twuiXml).TryGetValue(key, out var a) ? a : null;

    /// <summary>这个页签在 twui 里有没有结构（`holder_tab_&lt;key&gt;`）。没有 = 建了一半的页签（游戏里不显示）。</summary>
    public static bool HasStructure(string? twuiXml, string key) =>
        !string.IsNullOrWhiteSpace(twuiXml) && !string.IsNullOrWhiteSpace(key)
        && Regex.IsMatch(twuiXml, "id=\"holder_tab_" + Regex.Escape(key) + "\"", RegexOptions.IgnoreCase);

    /// <summary>取路径里的文件名（`ui/…\button_upgrade_x.png` → `button_upgrade_x.png`）。</summary>
    public static string FileName(string? path) =>
        string.IsNullOrWhiteSpace(path) ? "" : path.Replace('\\', '/').TrimEnd('/').Split('/')[^1];

    /// <summary>从图片文件名取"词干"（`background_images_skvg.png` → `skvg`）。</summary>
    public static string? StemOf(string? file)
    {
        var n = FileName(file);
        if (n.Length == 0) return null;
        if (!n.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return n;
        var stem = n[..^4];
        foreach (var prefix in new[] { "background_images_", "button_upgrade_" })
            if (stem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return stem[prefix.Length..];
        return stem;
    }

    /// <summary>页面背景状态的全部名字（`&lt;states&gt;` 里带 background_images_ 图的自命名状态）。</summary>
    public static List<string> PanelStates(string? twuiXml)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(twuiXml)) return list;
        var guidToPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(twuiXml, @"<component_image\b[^>]*?/>"))
        {
            var t = Regex.Match(m.Value, "this=\"([^\"]+)\"");
            var p = Regex.Match(m.Value, "imagepath=\"([^\"]+)\"");
            if (t.Success && p.Success) guidToPath[t.Groups[1].Value] = p.Groups[1].Value;
        }
        foreach (Match m in Regex.Matches(twuiXml, @"<([A-Za-z0-9_]+)\s+[^>]*?\bname=""([A-Za-z0-9_\.]+)"""))
        {
            var tag = m.Groups[1].Value;
            if (!tag.Equals(m.Groups[2].Value, StringComparison.OrdinalIgnoreCase) || list.Contains(tag, StringComparer.OrdinalIgnoreCase)) continue;
            var close = twuiXml.IndexOf("</" + tag + ">", m.Index, StringComparison.OrdinalIgnoreCase);
            var block = twuiXml[m.Index..(close > m.Index ? close : Math.Min(twuiXml.Length, m.Index + 4000))];
            var img = Regex.Match(block, @"componentimage=""([^""]+)""");
            if (!img.Success || !guidToPath.TryGetValue(img.Groups[1].Value, out var path)) continue;
            if (path.IndexOf("background_images_", StringComparison.OrdinalIgnoreCase) < 0) continue;
            list.Add(tag);
        }
        return list;
    }
}
