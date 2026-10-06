using WarbandStudio.Pack;
using WarbandStudio.Packfile;
using WarbandStudio.Rpfm;

namespace WarbandStudio.Probe;

/// <summary>
/// 直接往包里补成本（走和工具"创建成本"同一条落表管线：resource_costs_tables +
/// 资源池 pooled_resource_factor_junctions_tables + 成本↔池 resource_cost_pooled_resource_junctions_tables）。
///
/// 用法：probe add-cost &lt;pack&gt; "&lt;id&gt;|&lt;金币&gt;[|&lt;资源&gt;[|&lt;数量&gt;]]" [更多条…] [--out out.pack]
///   · 不给 --out 就**写回原文件**（先存 &lt;pack&gt;.addcost.bak，写到 .saving 再替换）；
///   · 池 id 用 &lt;资源&gt;_warband_upgrade（已有就复用，不重复建池）。
/// </summary>
public static class AddCost
{
    public static int Run(string[] a)
    {
        if (a.Length < 3)
        {
            Console.Error.WriteLine("用法: probe add-cost <pack> \"<id>|<金币>|<资源>|<数量>\" [更多条…] [--out out.pack]");
            return 1;
        }
        var src = Path.GetFullPath(a[1]);
        string? outArg = null;
        var specs = new List<string>();
        for (var i = 2; i < a.Length; i++)
        {
            if (a[i] == "--out") { outArg = ++i < a.Length ? a[i] : null; continue; }
            specs.Add(a[i]);
        }
        if (specs.Count == 0) { Console.Error.WriteLine("没有要补的成本"); return 1; }

        var schema = WarbandStudio.Packfile.Schema.Load(RpfmCli.LocateSchema() ?? throw new FileNotFoundException("找不到 schema。"));
        var vanilla = DetectVanilla() ?? throw new FileNotFoundException("找不到原版 db.pack（%APPDATA%\\WarbandStudio\\settings.json 的 GameDir）");
        var notes = new List<string>();

        // 池 → 已有？（池表里查一遍，已有就复用不重建）；顺带抄 junction 的 context/ui 两列
        string ctx = "absolute", ui = "default";
        var havePools = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var pack = PackArchive.Open(src))
        {
            var ft = TableFiles.ReadMerged(pack, "pooled_resource_factor_junctions_tables", schema, notes);
            if (ft is not null)
            {
                var ui2 = ft.Columns.FindIndex(c => c.Name.Equals("unique_id", StringComparison.OrdinalIgnoreCase));
                if (ui2 < 0) ui2 = TableFiles.FindKeyColumn(ft);
                if (ui2 >= 0) foreach (var r in ft.Rows) { var v = r[ui2].ToTsv(); if (v.Length > 0) havePools.Add(v); }
            }
            var jt = TableFiles.ReadConcat(pack, "resource_cost_pooled_resource_junctions_tables", schema, notes);
            if (jt is not null && jt.Rows.Count > 0)
            {
                var ci = jt.Columns.FindIndex(c => c.Name.Equals("context", StringComparison.OrdinalIgnoreCase));
                var ii = jt.Columns.FindIndex(c => c.Name.Equals("ui_resource_transaction_pooled_resource", StringComparison.OrdinalIgnoreCase));
                if (ci >= 0 && jt.Rows[0][ci].ToTsv().Length > 0) ctx = jt.Rows[0][ci].ToTsv();
                if (ii >= 0 && jt.Rows[0][ii].ToTsv().Length > 0) ui = jt.Rows[0][ii].ToTsv();
            }
        }

        var e = new WarbandEdits();
        foreach (var spec in specs)
        {
            var p = spec.Split('|');
            var id = p[0].Trim();
            if (id.Length == 0) { Console.Error.WriteLine("id 不能为空：" + spec); return 1; }
            var gold = p.Length > 1 && double.TryParse(p[1].Trim(), out var g) ? g : 0;
            e.AddCost[id] = gold;
            var res = p.Length > 2 ? p[2].Trim() : "";
            var amt = p.Length > 3 && long.TryParse(p[3].Trim(), out var am) ? am : 0;
            var line = $"{id}：金币 {gold:0}";
            if (res.Length > 0)
            {
                var pool = res + "_warband_upgrade";
                if (!havePools.Contains(pool) && !e.PoolFactors.Any(x => x.UniqueId.Equals(pool, StringComparison.OrdinalIgnoreCase)))
                    e.PoolFactors.Add((pool, "other", res, -2147483647L, 2147483647L, "", 0));
                e.PoolCosts.Add((pool, id, amt, ctx, ui));
                line += $" + {amt} 点 {res}（池 {pool}）";
            }
            Console.WriteLine("[i] " + line);
        }

        var wantInPlace = outArg is null;
        var dest = wantInPlace ? src + ".saving" : Path.GetFullPath(outArg!);
        var rep = WarbandExporter.Export(src, vanilla, dest, schema, e);
        foreach (var n in rep.Notes) Console.WriteLine("      · " + n);
        if (wantInPlace)
        {
            var bak = src + ".addcost.bak";
            if (!File.Exists(bak)) File.Copy(src, bak);
            File.Move(dest, src, overwrite: true);
            Console.WriteLine($"[i] 备份：{Path.GetFileName(bak)}");
        }
        Console.WriteLine($"[✓] 已写入：{(wantInPlace ? src : dest)}");
        return 0;
    }

    private static string? DetectVanilla()
    {
        try
        {
            var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WarbandStudio", "settings.json");
            if (!File.Exists(p)) return null;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(p));
            var game = doc.RootElement.TryGetProperty("GameDir", out var g) ? g.GetString() : null;
            if (string.IsNullOrWhiteSpace(game)) return null;
            var db = Path.Combine(game!, "data", "db.pack");
            return File.Exists(db) ? db : null;
        }
        catch { return null; }
    }
}
