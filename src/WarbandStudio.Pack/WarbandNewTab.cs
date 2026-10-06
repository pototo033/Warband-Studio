using System.Text;
using System.Text.RegularExpressions;
using WarbandStudio.Packfile;

namespace WarbandStudio.Pack;

/// <summary>新建页签的结果。</summary>
public sealed record NewTabReport(bool Ok, string Key, string Donor, IReadOnlyList<string> Files, string? Error);

/// <summary>
/// **新建页签**（按 WUU 方案，用户 C2 拍板）。一个页签在游戏里是四样东西：
///
///   1. `ui/campaign ui/warband_upgrades.twui.xml` 里的一块 `&lt;holder_tab_KEY&gt;` 组件
///      （含 `&lt;property name="God" value="KEY"/&gt;`、offset 位置、以及"这个页签有内容才显示"的回调），
///      外加文件顶部 `&lt;hierarchy&gt;` 索引里对应的那条；
///   2. `ui/skins/default/warband_upgrades/background_images_KEY.png`（页签选中时的面板背景）；
///   3. `ui/skins/default/warband_upgrades/button_upgrade_KEY.png`（页签按钮图）；
///   4. `unit_upgrade_group_ui_categories_tables` 里的一行（组按这个 key 归页签）。
///
/// 做法是**克隆一个已有的母版页签**：
///   · 母版块里"只属于它自己"的 GUID 换新（格式跟着原文走 —— 这份 twui 是 8-4-4-16 四段写法）；
///   · 被别处引用的共享组件 GUID（selected_frame_general / button_flame / icon 之类）保持原值；
///   · key 改成新的、位置**排在所有已有页签最下面**（连建两个不会叠在一起）；
///   · 背景/按钮图可指定素材库里的来源，没指定就用母版那张；
///   · 改完先做 XML 良构校验，不通过就不写。
/// </summary>
public static class WarbandNewTab
{
    private const string TwuiSuffix = "warband_upgrades.twui.xml";
    private const string SkinDir = "ui/skins/default/warband_upgrades/";
    private static readonly string[] PreferredDonors = ["MOD2", "MOD1", "ART", "OVN5", "OVN1"];
    private const string GuidPattern = @"[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-?[0-9a-fA-F]{12,20}";

    public static NewTabReport Build(PackArchive pack, string key, string? donorKey,
                                     Dictionary<string, byte[]> repl, List<string> notes,
                                     string? bgSource = null, string? btnSource = null)
    {
        key = (key ?? "").Trim().ToUpperInvariant();
        if (key.Length == 0 || !Regex.IsMatch(key, "^[A-Z0-9_]{2,16}$"))
            return new NewTabReport(false, key, "", [], "页签 key 只能用 2–16 位大写字母/数字/下划线。");

        var twuiEntry = pack.VisibleEntries.FirstOrDefault(e =>
            e.Path.EndsWith(TwuiSuffix, StringComparison.OrdinalIgnoreCase));
        if (twuiEntry is null)
            return new NewTabReport(false, key, "", [], $"包里没有 {TwuiSuffix}（新建页签的核心就是这个文件）。");

        // twui 读**本轮缓冲优先**：页签重命名（holder_tab_旧 → 新）会先往 repl 写一份，
        // 若这里读原包，克隆出来的新页签会把重命名结果整块盖回去（表现："重命名被还原"）。
        byte[] twuiBytes;
        if (repl.TryGetValue(twuiEntry.Path, out var bufferedTwui)) twuiBytes = bufferedTwui;
        else
        {
            var hit = repl.FirstOrDefault(kv =>
                kv.Key.Replace('/', '\\').Equals(twuiEntry.Path.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase));
            twuiBytes = hit.Value ?? pack.ReadDecoded(twuiEntry);
        }
        var xml = Encoding.UTF8.GetString(twuiBytes);
        var bom = xml.Length > 0 && xml[0] == '\uFEFF' ? "\uFEFF" : "";
        xml = xml.TrimStart('\uFEFF');

        if (Regex.IsMatch(xml, "id=\"holder_tab_" + Regex.Escape(key) + "\"", RegexOptions.IgnoreCase))
            return new NewTabReport(false, key, "", [], $"页签 {key} 已经存在了。");

        // 母版：优先自由页签，其次任意一个
        var existing = ExistingTabs(xml);
        var donor = !string.IsNullOrWhiteSpace(donorKey) && existing.Contains(donorKey!, StringComparer.OrdinalIgnoreCase)
            ? existing.First(t => t.Equals(donorKey, StringComparison.OrdinalIgnoreCase))
            : PreferredDonors.FirstOrDefault(d => existing.Contains(d, StringComparer.OrdinalIgnoreCase))
              ?? existing.LastOrDefault();
        if (donor is null)
            return new NewTabReport(false, key, "", [], "twui 里一个 holder_tab_* 都没有，没法克隆母版。");

        // ── 母版在 twui 里是**一整套**（WUU 里每个页签都齐这五处，缺一处游戏里就不对）──
        //   ① hierarchy 节点 holder_tab_<donor>（里面挂着 selected_frame 与 button_toggle_tab_<donor>）
        //   ② components 里的 holder_tab_<donor>（页签本体：God 属性 + "有内容才显示"回调）
        //   ③ components 里的 button_toggle_tab_<donor>（页签按钮：button_upgrade_<donor>.png 用**路径**声明）
        //   ④ components 里的 <donor ... name="<donor>">（页签背景面板：1317×780 的 <image> 指向 ⑤）
        //   ⑤ components 里的 <component_image imagepath=".../background_images_<donor>.png"/>（背景图条目）
        // 以前只克隆了 ②，① 还只换外层 GUID（里面的按钮仍是母版的 → 两个页签抢同一个按钮组件），
        // ③④⑤ 完全没建 —— 游戏里"按钮是母版的 / 我选的背景不出现 / 只剩一个页签"就出在这里。
        var donorBody = ExtractBodyBlock(xml, donor, out _, out var holderEnd);
        if (donorBody is null)
            return new NewTabReport(false, key, donor, [], $"找不到母版页签 holder_tab_{donor} 的本体块（twui 结构可能不同）。");

        var hierStart = xml.IndexOf("<hierarchy>", StringComparison.Ordinal);
        var hierEnd = xml.IndexOf("</hierarchy>", StringComparison.Ordinal);
        string? donorHier = null;
        var hierNodeEnd = -1;
        if (hierStart > 0 && hierEnd > hierStart)
        {
            donorHier = ExtractBlock(xml[hierStart..hierEnd], "holder_tab_" + donor, out _, out var he);
            if (donorHier is not null) hierNodeEnd = hierStart + he;
        }
        // ③④⑤ 都在 <components> 段里找（hierarchy 段里也有同名短节点，别抓错）
        var compsStart = xml.IndexOf("<components>", StringComparison.Ordinal);
        string? donorBtn = null, donorBg = null, donorBgEntry = null;
        var btnEnd = -1;
        var bgEnd = -1;
        var bgEntryEnd = -1;
        if (compsStart > 0)
        {
            donorBtn = ExtractBlock(xml, "button_toggle_tab_" + donor, compsStart, out _, out btnEnd);
            donorBg = ExtractNamedComponent(xml, donor, compsStart, out _, out bgEnd);
            donorBgEntry = ExtractComponentImage(xml, donor, compsStart, out _, out bgEntryEnd);
        }

        var lower = key.ToLowerInvariant();
        var donorLow = donor.ToLowerInvariant();
        // 计数用的"母版范围"= 上面五处合起来：只在这里出现的 GUID 就是母版自己的（克隆时要换新），
        // 在别处也出现的（selected_frame_general / button_flame / icon 之类）是共享组件，保持原值。
        var donorScope = string.Join("\n", new[] { donorBody, donorHier ?? "", donorBtn ?? "", donorBg ?? "", donorBgEntry ?? "" });

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string NewGuid(string g)
        {
            if (map.TryGetValue(g, out var n)) return n;
            var total = Regex.Matches(xml, Regex.Escape(g), RegexOptions.IgnoreCase).Count;
            var mine = Regex.Matches(donorScope, Regex.Escape(g), RegexOptions.IgnoreCase).Count;
            if (total > mine) { map[g] = g; return g; }                   // 还被别处引用 → 共享组件，保持原值
            var raw = Guid.NewGuid().ToString();
            n = g.Count(c => c == '-') >= 4 ? raw : raw.Remove(23, 1);     // 跟随原文的 GUID 写法
            map[g] = n;
            return n;
        }

        // 克隆：GUID 换新 + **key 相关的名字/路径全换**（标签名是 donor→key、God 值、按钮/背景图路径）。
        // 图片路径这一处最容易漏：它们在 twui 里不是 GUID 引用，而是直接写死的字符串
        // （按钮组件里是反斜杠形式、背景是正斜杠），只换 GUID 的话新页签永远用母版的图。
        string Sub(string t)
        {
            t = Regex.Replace(t, "holder_tab_" + Regex.Escape(donor), "holder_tab_" + lower, RegexOptions.IgnoreCase);
            t = Regex.Replace(t, "button_toggle_tab_" + Regex.Escape(donor), "button_toggle_tab_" + lower, RegexOptions.IgnoreCase);
            t = Regex.Replace(t, "<(/?)(?:" + Regex.Escape(donor) + ")(?=[\\s/>])", "<$1" + lower, RegexOptions.IgnoreCase);
            t = t.Replace("name=\"" + donor + "\"", "name=\"" + lower + "\"", StringComparison.OrdinalIgnoreCase);
            t = t.Replace("value=\"" + donor + "\"", "value=\"" + key + "\"", StringComparison.OrdinalIgnoreCase);
            t = t.Replace("background_images_" + donorLow + ".png", "background_images_" + lower + ".png", StringComparison.OrdinalIgnoreCase);
            t = t.Replace("button_upgrade_" + donorLow + ".png", "button_upgrade_" + lower + ".png", StringComparison.OrdinalIgnoreCase);
            return t;
        }
        string Clone(string text) => Sub(Regex.Replace(text, GuidPattern, m => NewGuid(m.Value)));

        var block = Clone(donorBody);
        var offset = Regex.Match(block, "offset=\"([0-9.]+),([0-9.]+)\"");
        if (offset.Success)
        {
            // 位置算法照 WUU 的 UI 框架（!!!!!!TLA_warband_twui.pack）：
            //   页签是**固定 20px 间距的一列**（x=8），y 从 12 起、到 292 共 15 个槽位；子 mod 各占自己的槽位 → 不重叠。
            // 占槽：第一列 x=8（y=12 起、步长 20，共 15 槽）；第一列满了用第二列 x=79（8 + 按钮宽 63 + 8）。
            // **每列各判各的空槽**：原来第二列拿"全文所有 y 值"判空，第一列一满就永远找不到空槽 →
            // 后面每个新页签都落在同一个 (79,312) 上互相叠（验收抓到的）。
            var col1 = new HashSet<double>();
            var col2 = new HashSet<double>();
            foreach (Match m in Regex.Matches(xml, "offset=\"([0-9.]+),([0-9.]+)\""))
            {
                if (double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out var vx) &&
                    double.TryParse(m.Groups[2].Value, System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out var vy))
                {
                    if (vx < 40) col1.Add(vy); else col2.Add(vy);
                }
            }
            var x = "8.00";
            var y = 0.0;
            for (var slot = 292.0; slot >= 12.0; slot -= 20.0)
                if (!col1.Contains(slot)) { y = slot; break; }
            if (y == 0)
            {
                x = "79.00";                                     // 第一列满了 → 第二列
                for (var slot = 292.0; slot >= 12.0; slot -= 20.0)
                    if (!col2.Contains(slot)) { y = slot; break; }
                if (y == 0)                                      // 两列都满：回到第一列往下再排一屏
                {
                    x = "8.00";
                    for (var slot = 312.0; ; slot += 20.0)
                        if (!col1.Contains(slot)) { y = slot; break; }
                }
            }
            block = block.Remove(offset.Index, offset.Length).Insert(offset.Index, $"offset=\"{x},{y:0.00}\"");
            notes.Add($"新页签 {key} 的位置：offset={x},{y:0.00}（排在已有页签最下面，避免重叠）");
        }

        var files = new List<string>();
        var sb = new StringBuilder(xml);
        var inserts = new List<(int Pos, string Text)>
        {
            (holderEnd, "\r\n\t\t" + block),                                // ② 页签本体
        };
        files.Add($"{twuiEntry.Path}：新增 holder_tab_{lower}（克隆 {donor}）");
        if (donorHier is not null && hierNodeEnd > 0)
        {
            inserts.Add((hierNodeEnd, "\r\n\t\t\t" + Clone(donorHier)));    // ① hierarchy 节点（含里面的按钮节点）
            files.Add("hierarchy：已加对应节点（里面的按钮节点跟着换 key / 换新 GUID）");
        }
        else notes.Add($"hierarchy 里没有 holder_tab_{donor} 的节点，只加了本体。");
        if (donorBtn is not null)
        {
            inserts.Add((btnEnd, "\r\n\t\t" + Clone(donorBtn)));            // ③ 按钮组件
            files.Add($"twui：新增 button_toggle_tab_{lower}（按钮图 button_upgrade_{lower}.png）");
        }
        else notes.Add($"twui 里没有 button_toggle_tab_{donor} 组件，按钮没建（游戏里会沿用母版的按钮）。");
        if (donorBg is not null)
        {
            inserts.Add((bgEnd, "\r\n\t\t" + Clone(donorBg)));              // ④ 背景面板组件
            files.Add($"twui：新增背景面板组件 <{lower}>");
        }
        else notes.Add($"twui 里没有 name=\"{donor}\" 的背景面板组件，背景没建。");
        if (donorBgEntry is not null)
        {
            inserts.Add((bgEntryEnd, "\r\n\t\t" + Clone(donorBgEntry)));    // ⑤ 背景图条目
            files.Add($"twui：新增 background_images_{lower}.png 条目");
        }
        else notes.Add($"twui 里没有 background_images_{donor}.png 的条目，背景条目没建。");
        // 从后往前插，前面的下标才不会跑
        foreach (var (pos, text) in inserts.OrderByDescending(x => x.Pos)) sb.Insert(pos, text);

        // 两张图：把字节复制进包（twui 侧的引用已经由克隆带过去了；用「换图」先准备好的不覆盖）
        var pairs = new[]
        {
            ($"{SkinDir}background_images_{donorLow}.png", $"{SkinDir}background_images_{lower}.png"),
            ($"{SkinDir}button_upgrade_{donorLow}.png", $"{SkinDir}button_upgrade_{lower}.png"),
        };
        foreach (var (dPath, tPath) in pairs)
        {
            if (repl.ContainsKey(tPath))
            {
                notes.Add($"页签 {key} 的 {Path.GetFileName(tPath)} 用「换图」选的那张（不覆盖）");
                continue;
            }
            var want = tPath.Contains("background_images_", StringComparison.OrdinalIgnoreCase) ? bgSource : btnSource;
            // 来源有两种：**本地素材库的文件**（v0.92 起选素材就是本地路径）或包内路径（老用法/母版）
            byte[]? bytes = null;
            var fromLib = false;
            if (!string.IsNullOrWhiteSpace(want))
            {
                if (File.Exists(want)) { bytes = File.ReadAllBytes(want!); fromLib = true; }
                else if (FindLoose(pack, want!) is { } picked) { bytes = pack.ReadDecoded(picked); fromLib = true; }
            }
            if (bytes is null && FindLoose(pack, dPath) is { } donorEntry) bytes = pack.ReadDecoded(donorEntry);
            if (bytes is null) { notes.Add($"图片没找到：{want ?? dPath}（这一项跳过）"); continue; }
            repl[tPath] = bytes;
            files.Add(tPath);
            notes.Add(fromLib
                ? $"页签 {key} 的图取自素材库：{want}"
                : $"页签 {key} 的图取自母版：{Path.GetFileName(dPath)}");
        }

        var finalXml = sb.ToString();
        try
        {
            new System.Xml.XmlDocument().LoadXml(finalXml);
        }
        catch (Exception ex)
        {
            return new NewTabReport(false, key, donor, [], "生成的 twui 不是良构 XML，已放弃：" + ex.Message);
        }

        repl[twuiEntry.Path] = Encoding.UTF8.GetBytes(bom + finalXml);
        notes.Add($"新建页签 {key}（母版 {donor}）：hierarchy 节点 + holder_tab + button_toggle_tab + 背景面板/条目 + 两张图 + categories 行。");
        return new NewTabReport(true, key, donor, files, null);
    }

    /// <summary>twui 里已有的页签 key（holder_tab_*）。</summary>
    public static List<string> ExistingTabs(string twuiXml)
    {
        var list = new List<string>();
        foreach (Match m in Regex.Matches(twuiXml, "id=\"holder_tab_([A-Za-z0-9_]+)\""))
        {
            var k = m.Groups[1].Value.ToUpperInvariant();
            if (!list.Contains(k)) list.Add(k);
        }
        return list;
    }

    /// <summary>自动挑母版（新建页签时和 <see cref="Build"/> 同一套规则）：优先 MOD2/MOD1/ART/OVN5/OVN1，其次最后一个已有页签。</summary>
    public static string? PickDonor(PackArchive pack)
    {
        var e = pack.VisibleEntries.FirstOrDefault(x => x.Path.EndsWith(TwuiSuffix, StringComparison.OrdinalIgnoreCase));
        if (e is null) return null;
        var keys = ExistingTabs(System.Text.Encoding.UTF8.GetString(pack.ReadDecoded(e)));
        return PreferredDonors.FirstOrDefault(d => keys.Contains(d, StringComparer.OrdinalIgnoreCase))
               ?? keys.LastOrDefault();
    }

    public static PackEntry? FindLoose(PackArchive pack, string path)
    {
        var hit = pack.Find(path);
        if (hit is not null) return hit;
        var alt = path.Replace('/', '\\');
        return pack.VisibleEntries.FirstOrDefault(e =>
            string.Equals(e.Path.Replace('/', '\\'), alt, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>按 id 定位**本体**块（先从 id 往前找到开标签，再往后找闭合标签）。</summary>
    private static string? ExtractBodyBlock(string text, string key, out int start, out int end)
    {
        start = end = -1;
        var idm = Regex.Match(text, "id=\"holder_tab_" + Regex.Escape(key) + "\"", RegexOptions.IgnoreCase);
        if (!idm.Success) return null;
        var open = text.LastIndexOf("<holder_tab_" + key, idm.Index, StringComparison.OrdinalIgnoreCase);
        if (open < 0) return null;
        var close = text.IndexOf("</holder_tab_" + key + ">", open, StringComparison.OrdinalIgnoreCase);
        if (close < 0) return null;
        start = open;
        end = close + key.Length + "</holder_tab_>".Length;
        return text[start..end];
    }

    /// <summary>从 &lt;tag ...&gt; 到配对的 &lt;/tag&gt;（含）。</summary>
    private static string? ExtractBlock(string text, string tag, out int start, out int end)
        => ExtractBlock(text, tag, 0, out start, out end);

    /// <summary>同上，但从 from 之后开始找（twui 的 hierarchy 段和 components 段里有同名节点，要指定从哪找）。</summary>
    private static string? ExtractBlock(string text, string tag, int from, out int start, out int end)
    {
        start = end = -1;
        if (from < 0) from = 0;
        var open = Regex.Match(text[from..], $"<{Regex.Escape(tag)}[\\s>]", RegexOptions.IgnoreCase);
        if (!open.Success) return null;
        start = from + open.Index;
        var close = text.IndexOf($"</{tag}>", start, StringComparison.OrdinalIgnoreCase);
        if (close < 0) return null;
        end = close + tag.Length + 3;
        return text[start..end];
    }

    /// <summary>按 name="X" 定位"带名字的组件"（&lt;X ... name="X"&gt;…&lt;/X&gt;）——页签背景面板就是这种。</summary>
    private static string? ExtractNamedComponent(string text, string name, int from, out int start, out int end)
    {
        start = end = -1;
        if (from < 0) from = 0;
        var m = Regex.Match(text[from..], "name=\"" + Regex.Escape(name) + "\"", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        var at = from + m.Index;
        var open = text.LastIndexOf("<" + name, at, StringComparison.OrdinalIgnoreCase);
        if (open < 0) return null;
        var close = text.IndexOf("</" + name + ">", open, StringComparison.OrdinalIgnoreCase);
        if (close < 0) return null;
        start = open;
        end = close + name.Length + 3;
        return text[start..end];
    }

    /// <summary>按图片路径定位 &lt;component_image ... imagepath="…background_images_&lt;name&gt;.png"/&gt; 条目（属性跨多行）。</summary>
    private static string? ExtractComponentImage(string text, string name, int from, out int start, out int end)
    {
        start = end = -1;
        if (from < 0) from = 0;
        foreach (Match m in Regex.Matches(text[from..], "<component_image\\b[^>]*?/>", RegexOptions.IgnoreCase))
        {
            if (!m.Value.Contains("background_images_" + name + ".png", StringComparison.OrdinalIgnoreCase)) continue;
            start = from + m.Index;
            end = start + m.Length;
            return m.Value;
        }
        return null;
    }
}
