using WarbandStudio.Pack;
using WarbandStudio.Packfile;
using WarbandStudio.Rpfm;

namespace WarbandStudio.Probe;

/// <summary>
/// 清理军事组授权表里的**重复授权**：同一 (unit, military_group) 对同时出现在**作者文件**和**我们的覆盖表**里 →
/// 把覆盖表里那份剔掉（作者文件是"他的地盘"，保留他的；我们只在"他没有这一对"时补行）。
///
/// 用法：probe dedupe-grants &lt;pack&gt; [--out out.pack]
///   · 不给 --out 就写回原文件（先存 &lt;pack&gt;.dedupe.bak，写到 .saving 再替换）。
/// </summary>
public static class DedupeGrants
{
    public static int Run(string[] a)
    {
        if (a.Length < 2) { Console.Error.WriteLine("用法: probe dedupe-grants <pack> [--out out.pack]"); return 1; }
        var src = Path.GetFullPath(a[1]);
        string? outArg = null;
        for (var i = 2; i < a.Length; i++) if (a[i] == "--out") outArg = ++i < a.Length ? a[i] : null;

        var schema = WarbandStudio.Packfile.Schema.Load(RpfmCli.LocateSchema() ?? throw new FileNotFoundException("找不到 schema。"));
        var notes = new List<string>();
        var repl = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        const string Table = "units_to_groupings_military_permissions_tables";

        using (var pack = PackArchive.Open(src))
        {
            var files = TableFiles.EntriesFor(pack, Table).ToList();
            if (files.Count == 0) { Console.Error.WriteLine("包里没有 " + Table); return 1; }
            static bool IsOurs(string p)
            {
                var n = Path.GetFileName(p);
                return n.StartsWith("studio", StringComparison.OrdinalIgnoreCase)
                    || n.StartsWith("zzzz_studio", StringComparison.OrdinalIgnoreCase);
            }
            // 作者文件里的 (unit, group) 对
            var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in files.Where(x => !IsOurs(x.Path)))
            {
                var t = DbTable.Decode(pack.ReadDecoded(f), Table, schema);
                var u = t.Columns.FindIndex(c => c.Name.Equals("unit", StringComparison.OrdinalIgnoreCase));
                var g = t.Columns.FindIndex(c => c.Name.Equals("military_group", StringComparison.OrdinalIgnoreCase));
                if (u < 0 || g < 0) continue;
                foreach (var r in t.Rows) have.Add(r[u].ToTsv() + "\u0001" + r[g].ToTsv());
            }
            // 覆盖表里跟作者重复的行剔掉（同文件内的重复也一并去）
            var dropped = 0;
            foreach (var f in files.Where(x => IsOurs(x.Path)))
            {
                var t = DbTable.Decode(pack.ReadDecoded(f), Table, schema);
                var u = t.Columns.FindIndex(c => c.Name.Equals("unit", StringComparison.OrdinalIgnoreCase));
                var g = t.Columns.FindIndex(c => c.Name.Equals("military_group", StringComparison.OrdinalIgnoreCase));
                if (u < 0 || g < 0) continue;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var raws = new List<DbValue[]>();
                var hit = 0;
                for (var i = 0; i < t.Rows.Count; i++)
                {
                    var key = t.Rows[i][u].ToTsv() + "\u0001" + t.Rows[i][g].ToTsv();
                    var dup = have.Contains(key) || !seen.Add(key);
                    if (dup) { hit++; continue; }
                    raws.Add(t.RawRows[i]);
                }
                if (hit > 0)
                {
                    repl[f.Path] = DbEncoder.Encode(new DbTable
                    {
                        TableName = Table, Version = t.Version, Guid = t.Guid, MysteriousByte = t.MysteriousByte,
                        Columns = t.Columns, Rows = [], RawRows = raws, Definition = t.Definition,
                    });
                    dropped += hit;
                    notes.Add($"{f.Path}：剔掉 {hit} 行重复授权（作者文件里已有同一对 / 同文件内重复）");
                }
            }
            if (dropped == 0) { Console.WriteLine("[i] 没有重复授权，无需修改。"); return 0; }
            var dest = outArg is null ? src + ".saving" : Path.GetFullPath(outArg);
            PackWriter.WriteFrom(pack, repl, dest);   // 先写 .saving（包还开着，不能直接替换原文件）
            foreach (var n in notes) Console.WriteLine("      · " + n);
            Console.WriteLine($"[✓] 共剔掉 {dropped} 行重复授权 → {dest}");
            if (outArg is null) return FinishInPlace(src, dest);
            return 0;
        }
    }

    /// <summary>**必须在 using 外面**调用：读包的句柄还开着时替换原文件会被自己挡住（"Access is denied"，§7.20 那个坑）。</summary>
    private static int FinishInPlace(string src, string dest)
    {
        var bak = src + ".dedupe.bak";
        if (!File.Exists(bak)) File.Copy(src, bak);
        File.Move(dest, src, overwrite: true);
        Console.WriteLine("[i] 备份：" + Path.GetFileName(bak));
        Console.WriteLine("[✓] 已写回：" + src);
        return 0;
    }
}
