using WarbandStudio.Pack;
using WarbandStudio.Packfile;
using WarbandStudio.Rpfm;

namespace WarbandStudio.Probe;

/// <summary>
/// 一次性整理：把"编辑写在覆盖表里"的老布局，按**现行落表口径**重排一遍（改的是包本身，不是编辑集）。
///
///   · **已有行改值**（坐标/页签、金额/等级）→ 就地改包里**已经有这一行**的那个文件
///     （只覆盖"这次编辑真正改的那几列"，别的列照旧；category 为空 = 不改页签）；
///   · **新增**（兵↔组、组、路线、连线、军事组授权、新页签分类）→ `zzzz_studio_edits`；
///   · **新组的坐标行** → `zzzz_studio_layout`；精英解锁 → `zzzz_studio_elite_unlock`；
///   · 旧名覆盖表（`studio_edits` / `studio_layout` / `studio_elite_unlock`）整份并入新名后**删掉**。
///
/// 用法：probe tidy-overrides &lt;pack&gt; [out.pack]
///   · 不给 out 就**写回原文件**（先 .bak 备份，写到 .saving 再替换）；
///   · 给了 out 就写到 out（原文件不动，用来先看效果）。
/// </summary>
public static class TidyOverrides
{
    /// <summary>ValueCols = null 表示"这张表只有新增"（整行并进覆盖表，没有就地改的说法）。</summary>
    private sealed record Spec(string Table, string NewFile, string[] KeyCols, string[]? ValueCols, bool InPlace = true);

    private static readonly Spec[] Specs =
    [
        // 坐标/页签：已有行就地改 x/y（category 非空才改页签）；新组补 zzzz_studio_layout
        new("unit_upgrade_group_ui_infos_tables", WarbandAmender.LayoutFileName,
            ["unit_upgrade_group"], ["x", "y", "category"]),
        // 升级路线：已有的 upgrade_key 就地改（改金额/等级/换方向）；新 key 走 zzzz_studio_edits
        new("unit_upgrade_to_unit_groups_tables", WarbandAmender.EditFileName,
            ["upgrade_key"], ["base_unit_group", "target_unit_group", "resource_cost", "required_rank", "subtracted_rank"]),
        // 成本：已有的 id 就地改金额（expenditure/income 那几列保留作者原样）
        new("resource_costs_tables", WarbandAmender.EditFileName, ["id"], ["treasury_cost"]),
        // 精英解锁：改的是**原版** main_units 的行（不在本包里）→ 只能整行进覆盖表；旧名并进来
        new("main_units_tables", WarbandExporter.UnlockTableFileName, ["unit"], null, InPlace: false),
        // 纯新增的表（键不唯一 / 只有"加一条"的语义）
        new("unit_to_unit_group_junctions_tables", WarbandAmender.EditFileName, [], null, InPlace: false),
        new("unit_upgrade_group_ui_links_tables", WarbandAmender.EditFileName, [], null, InPlace: false),
        new("units_to_groupings_military_permissions_tables", WarbandAmender.EditFileName, [], null, InPlace: false),
        new("unit_upgrade_groups_tables", WarbandAmender.EditFileName, ["unit_group"], null, InPlace: false),
        new("unit_upgrade_group_ui_categories_tables", WarbandAmender.EditFileName, [], null, InPlace: false),
    ];

    public static int Run(string[] a)
    {
        if (a.Length < 2) { Console.Error.WriteLine("用法: probe tidy-overrides <pack> [out.pack]"); return 1; }
        var src = Path.GetFullPath(a[1]);
        var wantInPlace = a.Length < 3;
        var dest = wantInPlace ? src + ".saving" : Path.GetFullPath(a[2]);   // 包开着的时候不能写它自己 → 先写 .saving

        var repl = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var drop = new List<string>();
        var notes = new List<string>();
        var schema = WarbandStudio.Packfile.Schema.Load(RpfmCli.LocateSchema() ?? throw new FileNotFoundException("找不到 schema。"));
        using (var pack = PackArchive.Open(src))
        {
            foreach (var spec in Specs) TidyTable(pack, schema, spec, repl, drop, notes);
            PackWriter.WriteFrom(pack, repl, dest, dropPaths: drop);
        }
        if (wantInPlace)
        {
            // 单独一个备份名：别和工具保存时写的 .bak 抢（那个可能是更早的状态）
            var bak = src + ".pretidy.bak";
            if (!File.Exists(bak)) File.Copy(src, bak);
            File.Move(dest, src, overwrite: true);
            Console.WriteLine($"[i] 备份：{Path.GetFileName(bak)}");
        }
        foreach (var n in notes) Console.WriteLine("      · " + n);
        Console.WriteLine($"[✓] 整理完成：{(wantInPlace ? src : dest)}");
        return 0;
    }

    private static void TidyTable(PackArchive pack, WarbandStudio.Packfile.Schema schema, Spec spec,
                                  Dictionary<string, byte[]> repl, List<string> drop, List<string> notes)
    {
        var entries = TableFiles.EntriesFor(pack, spec.Table);
        if (entries.Count == 0) return;
        static bool IsOurs(string p)
        {
            var n = Path.GetFileName(p);
            return n.StartsWith("studio_", StringComparison.OrdinalIgnoreCase)
                || n.StartsWith("zzzz_studio", StringComparison.OrdinalIgnoreCase);
        }
        static bool IsOld(string p) => Path.GetFileName(p).StartsWith("studio_", StringComparison.OrdinalIgnoreCase);

        var overEntries = entries.Where(e => IsOurs(e.Path)).OrderBy(e => IsOld(e.Path) ? 0 : 1).ToList();  // 老名先、新名后
        var modEntries = entries.Where(e => !IsOurs(e.Path)).ToList();
        if (overEntries.Count == 0) return;

        // ① 作者文件：解出来 + 建 key 索引（就地改要落回"已经有这一行"的那个文件）
        var modFiles = new List<(string Path, DbTable T, Dictionary<string, int> At)>();
        foreach (var e in modEntries)
        {
            DbTable t;
            try { t = DbTable.Decode(pack.ReadDecoded(e), spec.Table, schema); } catch { continue; }
            var at = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var kc in spec.KeyCols)
            {
                var c = t.Columns.FindIndex(x => x.Name.Equals(kc, StringComparison.OrdinalIgnoreCase));
                if (c < 0) continue;
                for (var i = 0; i < t.Rows.Count; i++)
                {
                    var v = t.Rows[i][c].ToTsv();
                    if (v.Length > 0) at[kc + "=" + v] = i;
                }
            }
            modFiles.Add((e.Path, t, at));
        }

        // ② 逐行处理覆盖表
        var kept = new List<DbValue[]>();
        var keptAt = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inPlace = 0;
        DbTable? proto = null;
        foreach (var e in overEntries)
        {
            DbTable t;
            try { t = DbTable.Decode(pack.ReadDecoded(e), spec.Table, schema); } catch { continue; }
            proto ??= t;
            for (var i = 0; i < t.Rows.Count; i++)
            {
                var raw = t.RawRows[i];
                var keyVals = spec.KeyCols
                    .Select(kc => t.Columns.FindIndex(x => x.Name.Equals(kc, StringComparison.OrdinalIgnoreCase)) is var c && c >= 0
                                  ? t.Rows[i][c].ToTsv() : "")
                    .ToList();
                var keyed = spec.KeyCols.Length > 0 && keyVals.All(v => v.Length > 0);
                var sig = keyed ? string.Join("\u0001", keyVals) : string.Join("\u0001", raw.Select(v => v.ToTsv()));

                var placed = false;
                if (spec.InPlace && spec.ValueCols is not null && keyed)
                {
                    foreach (var (mp, mt, at) in modFiles)
                    {
                        var hit = -1;
                        for (var ci = 0; ci < spec.KeyCols.Length; ci++)
                        {
                            if (!at.TryGetValue(spec.KeyCols[ci] + "=" + keyVals[ci], out var r)) { hit = -1; break; }
                            if (ci == 0) hit = r; else if (hit != r) { hit = -1; break; }   // 组合键必须是同一行
                        }
                        if (hit < 0) continue;

                        var nr = (DbValue[])mt.RawRows[hit].Clone();
                        var changed = false;
                        foreach (var vc in spec.ValueCols)
                        {
                            var si = t.Definition.Fields.FindIndex(f => f.Name.Equals(vc, StringComparison.OrdinalIgnoreCase));
                            var di = mt.Definition.Fields.FindIndex(f => f.Name.Equals(vc, StringComparison.OrdinalIgnoreCase));
                            if (si < 0 || di < 0) continue;
                            var v = raw[si];
                            if (vc.Equals("category", StringComparison.OrdinalIgnoreCase) && v.ToTsv().Length == 0) continue;  // 空 = 不改页签
                            if (nr[di].ToTsv().Equals(v.ToTsv(), StringComparison.OrdinalIgnoreCase)) continue;             // 已经就是这个值
                            nr[di] = v;
                            changed = true;
                        }
                        mt.RawRows[hit] = nr;
                        if (changed)
                        {
                            if (hit < mt.Rows.Count)
                                for (var c = 0; c < mt.Columns.Count && c < mt.Rows[hit].Length; c++)
                                {
                                    var ri = mt.Definition.Fields.FindIndex(f => f.Name.Equals(mt.Columns[c].Name, StringComparison.OrdinalIgnoreCase));
                                    if (ri >= 0 && ri < nr.Length) mt.Rows[hit][c] = nr[ri];
                                }
                            touched.Add(mp);
                        }
                        inPlace++;               // 不管值有没有变，这一行都不再需要留在覆盖表里
                        placed = true;
                        break;
                    }
                }
                if (placed) continue;
                if (sig.Length > 0 && keptAt.TryGetValue(sig, out var old)) { kept[old] = raw; continue; }   // 后写的为准
                if (sig.Length > 0) keptAt[sig] = kept.Count;
                kept.Add(raw);
            }
        }
        if (proto is null) return;

        // ③ 就地改过的那几个作者文件写回
        foreach (var (mp, mt, _) in modFiles)
            if (touched.Contains(mp)) repl[mp] = EncodeLike(mt, mt.RawRows);

        // ④ 新覆盖表：还有行就写，没有就删
        var newPath = $"db/{spec.Table}/{spec.NewFile}";
        if (kept.Count > 0)
        {
            repl[newPath] = EncodeLike(proto, kept);
            notes.Add($"{spec.Table}：就地改 {inPlace} 行" +
                      (touched.Count > 0 ? $"（改了 {touched.Count} 个作者文件：{string.Join("、", touched.Select(Path.GetFileName).Distinct())}）" : "（值本来就对，不用改）") +
                      $"；{spec.NewFile} 保留 {kept.Count} 行（新键）");
        }
        else
        {
            if (!drop.Contains(newPath, StringComparer.OrdinalIgnoreCase)) drop.Add(newPath);
            notes.Add($"{spec.Table}：就地改 {inPlace} 行；{spec.NewFile} 没有剩余行 → 删掉");
        }
        // ⑤ 旧名文件整份并入后删掉（新名文件由 repl 覆盖，不用 drop）
        foreach (var e in overEntries)
        {
            var isNewName = e.Path.Replace('/', '\\').EndsWith("\\" + spec.NewFile, StringComparison.OrdinalIgnoreCase);
            if (!isNewName && !drop.Contains(e.Path, StringComparer.OrdinalIgnoreCase)) drop.Add(e.Path);
        }
    }

    /// <summary>按"这张表原来长什么样"重编（保留它自己的定义与 GUID）。</summary>
    private static byte[] EncodeLike(DbTable t, List<DbValue[]> raws)
    {
        var nt = new DbTable
        {
            TableName = t.TableName,
            Version = t.Version,
            Guid = t.Guid,
            MysteriousByte = t.MysteriousByte,
            Columns = t.Columns,
            Rows = raws.Select(r =>
            {
                var row = new DbValue[Math.Max(t.Columns.Count, r.Length)];
                for (var i = 0; i < row.Length; i++) row[i] = i < r.Length ? r[i] : DbValue.OfStr("");
                return row;
            }).ToList(),
            RawRows = raws,
            Definition = t.Definition,
        };
        return DbEncoder.Encode(nt);
    }
}
