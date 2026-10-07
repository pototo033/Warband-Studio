using WarbandStudio.Pack;
using WarbandStudio.Packfile;
using WarbandStudio.Rpfm;

namespace WarbandStudio.Probe;

/// <summary>
/// 页签图/背景状态验收（v1.4 的"换图没生效 / 在用标记指错文件"修复）：
///
///   probe tab-art &lt;pack&gt;                          列出 twui 里每个页签**实际用的图**（解析结果）
///   probe tab-art &lt;pack&gt; &lt;旧key&gt; &lt;新key&gt; &lt;导出.pack&gt;  跑一遍改名落表：
///        · 图文件名跟着新 key（撞名 _N，不覆盖）          · twui 里 holder/按钮组件/背景状态/God 值/图路径全改
///        · 同时先"换图"（本地 PNG 当来源）→ 换的那张必须**跟着改名走**（不能被改名整块吃掉）
///   probe tab-art &lt;pack&gt; --heal &lt;导出.pack&gt;        跑一遍背景状态自愈（有组在用的页签都得有同名状态）
/// </summary>
public static class TabArtCheck
{
    public static int Run(string[] a)
    {
        if (a.Length < 2) { Console.Error.WriteLine("用法: probe tab-art <pack> [旧key 新key 导出.pack | --heal 导出.pack]"); return 1; }
        var schema = WarbandStudio.Packfile.Schema.Load(RpfmCli.LocateSchema() ?? throw new FileNotFoundException("找不到 schema。"));
        var packPath = Path.GetFullPath(a[1]);

        if (a.Length == 2)
        {
            using var pack = PackArchive.Open(packPath);
            var text = TwuiText(pack);
            var map = TwuiTabs.Parse(text);
            Console.WriteLine($"页签 {map.Count} 个（holder_tab_*）：");
            foreach (var (k, art) in map.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
                Console.WriteLine($"  {k,-10} 背景={art.BgFile ?? "-",-34} 按钮={art.BtnFile ?? "-",-34} 状态={art.PanelState ?? "-",-10} 按钮组件={art.BtnComponent ?? "-"}");
            return 0;
        }

        var dest = Path.GetFullPath(a[^1]);
        var notes = new List<string>();
        using var src = PackArchive.Open(packPath);
        var repl = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var e = new WarbandEdits();

        if (a[2] == "--heal")
        {
            // 让"有组在用的页签"全都进 want 集合：直接照抄 infos 表的 category
            var infos = TableFiles.ReadMerged(src, "unit_upgrade_group_ui_infos_tables", schema, notes)
                        ?? throw new InvalidDataException("包里没有 ui_infos");
            var gcol = infos.Columns.FindIndex(c => c.Name.Equals("unit_upgrade_group", StringComparison.OrdinalIgnoreCase));
            var ccol = infos.Columns.FindIndex(c => c.Name.Equals("category", StringComparison.OrdinalIgnoreCase));
            for (var i = 0; i < infos.Rows.Count; i++)
                e.InfoEdits[infos.Rows[i][gcol].ToTsv()] = (i, i, infos.Rows[i][ccol].ToTsv());
        }
        else
        {
            var oldKey = a[2];
            var newKey = a[3];
            e.TabRenames.Add((oldKey, newKey));
            // 顺手"换图"：拿一张本地 PNG 当这个页签背景的来源（验证改名要把换的图带走）
            var text0 = TwuiText(src);
            var art0 = TwuiTabs.Of(text0, oldKey);
            var bgInner = TwuiTabs.SkinDir + (art0?.BgFile ?? TwuiTabs.ConventionName("background_images_", oldKey));
            var png = Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFAAH/q842iQAAAABJRU5ErkJggg==");
            var local = Path.Combine(Path.GetTempPath(), "warbandstudio_tabart.png");
            File.WriteAllBytes(local, png);
            e.FileReplacements.Add((bgInner, local));
            Console.WriteLine($"[i] 先换图：{bgInner} ← {local}（{png.Length}B）");
        }

        var n = WarbandAmender.Apply(src, null, e, schema, repl, notes);
        PackWriter.WriteFrom(src, repl, dest, dropPaths: e.RemoveFiles);   // 真·改名：旧条目要在这一步跳过（和正式导出同一条路）
        Console.WriteLine($"[✓] 落表 {n} 个文件 → {dest}");
        foreach (var line in notes) Console.WriteLine("    · " + line);

        // 读回导出包核对：页签状态名 / 图名 / 字节
        using var outp = PackArchive.Open(dest);
        var text1 = TwuiText(outp);
        var map1 = TwuiTabs.Parse(text1);
        Console.WriteLine("导出包里：");
        foreach (var (k, art) in map1.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            Console.WriteLine($"  {k,-10} 背景={art.BgFile ?? "-",-34} 按钮={art.BtnFile ?? "-",-34} 状态={art.PanelState ?? "-",-10} 按钮组件={art.BtnComponent ?? "-"}");
        var states = TwuiTabs.PanelStates(text1);
        Console.WriteLine($"背景状态 {states.Count} 个：{string.Join("、", states.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))}");
        foreach (var path in outp.VisibleEntries.Select(x => x.Path.Replace(Path.DirectorySeparatorChar, '/'))
                     .Where(p => p.StartsWith(TwuiTabs.SkinDir, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            Console.WriteLine($"    图 {Path.GetFileName(path)}（{outp.ReadDecoded(outp.Find(path)!).Length}B）");
        return 0;
    }

    private static string? TwuiText(PackArchive pack)
    {
        var e = pack.VisibleEntries.FirstOrDefault(x =>
            x.Path.EndsWith("warband_upgrades.twui.xml", StringComparison.OrdinalIgnoreCase));
        return e is null ? null : System.Text.Encoding.UTF8.GetString(pack.ReadDecoded(e));
    }
}
