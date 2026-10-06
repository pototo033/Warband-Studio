using System.Text.Json;
using WarbandStudio.Pack;
using WarbandStudio.Packfile;
using WarbandStudio.Rpfm;

namespace WarbandStudio.Probe;

/// <summary>
/// 「新建页签」的无头版：给一个**已经有组在用、本体却缺着**的分类（categories 行 / twui 块）补出全套结构。
///
/// 走的和工具「新建页签」**同一条管线**（`WarbandEdits.NewTabs` → `WarbandAmender` 步骤 7 → `WarbandNewTab`
/// 整套克隆 + categories 行），只是没有界面。两处按"补"的语义和界面不同（界面建的是全新页签，不存在这两件事）：
///   · 两张图**默认保留包里已有的** `background_images_&lt;key&gt;.png` / `button_upgrade_&lt;key&gt;.png`
///     （界面默认用母版的图；这里要是照抄就会把用户选好的图顶掉）；
///   · 该分类**已经有组**时不再塞 `studio_tab_&lt;key&gt;` 空组（空组只是为了让新页签在游戏里显示，
///     已经有组就多余；想强制加用 --empty-group）。
///
/// 用法：probe new-tab &lt;pack&gt; &lt;KEY&gt; [--donor MOD2] [--bg 包内路径|本地文件] [--btn …]
///                     [--vanilla 原版db.pack] [--out out.pack] [--empty-group]
///   · 不给 --out 就**写回原文件**（先存 &lt;pack&gt;.newtab.bak，写到 .saving 再替换）；
///   · KEY 在 twui 里已经有 `holder_tab_KEY` 时拒绝（已经有了就别补，免得两份）。
/// </summary>
public static class NewTabFix
{
    public static int Run(string[] a)
    {
        if (a.Length < 3)
        {
            Console.Error.WriteLine("用法: probe new-tab <pack> <KEY> [--donor MOD2] [--bg 路径] [--btn 路径] [--vanilla db.pack] [--out out.pack] [--empty-group]");
            return 1;
        }
        var src = Path.GetFullPath(a[1]);
        var key = a[2].Trim().ToUpperInvariant();
        string? donorArg = null, bgArg = null, btnArg = null, vanillaArg = null, outArg = null;
        var forceEmptyGroup = false;
        for (var i = 3; i < a.Length; i++)
            switch (a[i])
            {
                case "--donor": donorArg = ++i < a.Length ? a[i] : null; break;
                case "--bg": bgArg = ++i < a.Length ? a[i] : null; break;
                case "--btn": btnArg = ++i < a.Length ? a[i] : null; break;
                case "--vanilla": vanillaArg = ++i < a.Length ? a[i] : null; break;
                case "--out": outArg = ++i < a.Length ? a[i] : null; break;
                case "--empty-group": forceEmptyGroup = true; break;
                default:
                    Console.Error.WriteLine("不认识的参数：" + a[i]);
                    return 1;
            }

        var schema = WarbandStudio.Packfile.Schema.Load(RpfmCli.LocateSchema() ?? throw new FileNotFoundException("找不到 schema。"));
        var vanilla = vanillaArg is not null ? Path.GetFullPath(vanillaArg) : DetectVanilla();
        if (vanilla is null || !File.Exists(vanilla))
        {
            Console.Error.WriteLine("找不到原版 db.pack（用 --vanilla 指定，或确认 %APPDATA%\\WarbandStudio\\settings.json 里的 GameDir）。");
            return 1;
        }

        var wantInPlace = outArg is null;
        var dest = wantInPlace ? src + ".saving" : Path.GetFullPath(outArg!);
        var notes = new List<string>();

        string? donor;
        bool hasGroupsInCat;
        string? bgDefault, btnDefault;
        using (var pack = PackArchive.Open(src))
        {
            // ① 已经有了就别补
            var twuiEntry = pack.VisibleEntries.FirstOrDefault(e =>
                e.Path.EndsWith("warband_upgrades.twui.xml", StringComparison.OrdinalIgnoreCase));
            if (twuiEntry is null) { Console.Error.WriteLine("包里没有 warband_upgrades.twui.xml，没法建页签。"); return 1; }
            var twui = System.Text.Encoding.UTF8.GetString(pack.ReadDecoded(twuiEntry));
            if (twui.Contains("id=\"holder_tab_" + key + "\"", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"页签 {key} 的 twui 块已经存在，不需要补。");
                return 1;
            }

            // ② 母版：--donor 优先，否则和界面同一套规则（MOD2/MOD1/ART/OVN5/OVN1 → 最后一个）
            donor = donorArg ?? WarbandNewTab.PickDonor(pack);
            if (donor is null) { Console.Error.WriteLine("twui 里一个 holder_tab_* 都没有，没法克隆母版。"); return 1; }

            // ③ 两张图默认**保留包里已有的**（用户选过的图不能被母版顶掉）
            var low = key.ToLowerInvariant();
            string? Existing(string kind)
            {
                var p = $"ui/skins/default/warband_upgrades/{kind}{low}.png";
                return WarbandNewTab.FindLoose(pack, p) is not null ? p : null;
            }
            bgDefault = bgArg ?? Existing("background_images_");
            btnDefault = btnArg ?? Existing("button_upgrade_");

            // ④ 这个分类下已经有组没有？（有 → 不塞空组）
            hasGroupsInCat = false;
            var infos = TableFiles.ReadMerged(pack, "unit_upgrade_group_ui_infos_tables", schema, notes);
            if (infos is not null)
            {
                var c = infos.Columns.FindIndex(x => x.Name.Equals("category", StringComparison.OrdinalIgnoreCase));
                if (c >= 0)
                    hasGroupsInCat = infos.Rows.Any(r => r[c].ToTsv().Equals(key, StringComparison.OrdinalIgnoreCase));
            }

            // ⑤ 组装编辑集：新建页签（+ 需要时补空组）
            var e = new WarbandEdits();
            e.NewTabs.Add((key, donor!, bgDefault, btnDefault));
            var autoGroup = $"studio_tab_{low}";
            if (forceEmptyGroup || !hasGroupsInCat)
            {
                e.SetGroup(autoGroup, true);
                e.InfoEdits[autoGroup] = (0, 0, key);
            }

            Console.WriteLine($"[i] 包：{Path.GetFileName(src)}");
            Console.WriteLine($"[i] 页签 {key}：母版 {donor}；" +
                              $"背景 {(bgDefault is null ? "用母版的图（包里没有 " + "background_images_" + low + ".png）" : "保留 " + Path.GetFileName(bgDefault))}；" +
                              $"按钮 {(btnDefault is null ? "用母版的图" : "保留 " + Path.GetFileName(btnDefault))}");
            Console.WriteLine(hasGroupsInCat
                ? $"[i] 分类 {key} 已经有组 → 不补 {autoGroup} 空组{(forceEmptyGroup ? "（--empty-group 强制补）" : "")}"
                : $"[i] 分类 {key} 还没有组 → 自动补 {autoGroup} 空组（游戏里页签靠分类下有组才显示）");

            var rep = WarbandExporter.Export(src, vanilla, dest, schema, e);
            Console.WriteLine($"[i] 导出：{dest}（树里 {rep.UnitsInTree} 个兵；解锁 {rep.Unlocked}）");
            foreach (var n in rep.Notes) Console.WriteLine("      · " + n);
        }

        if (wantInPlace)
        {
            var bak = src + ".newtab.bak";
            if (!File.Exists(bak)) File.Copy(src, bak);       // 留第一次的备份（重复跑不会把好状态盖掉）
            File.Move(dest, src, overwrite: true);
            Console.WriteLine($"[i] 备份：{Path.GetFileName(bak)}");
            Console.WriteLine($"[✓] 已写回：{src}");
        }
        else Console.WriteLine($"[✓] 已写出：{dest}（原文件没动）");
        return 0;
    }

    /// <summary>从工具设置里找原版 db.pack（settings.json 的 GameDir）。</summary>
    private static string? DetectVanilla()
    {
        try
        {
            var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                 "WarbandStudio", "settings.json");
            if (!File.Exists(p)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(p));
            var game = doc.RootElement.TryGetProperty("GameDir", out var g) ? g.GetString() : null;
            if (string.IsNullOrWhiteSpace(game)) return null;
            var db = Path.Combine(game!, "data", "db.pack");
            return File.Exists(db) ? db : null;
        }
        catch { return null; }
    }
}
