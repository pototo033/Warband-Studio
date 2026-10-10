using WarbandStudio.Pack;
using WarbandStudio.Packfile;
using WarbandStudio.Rpfm;

namespace WarbandStudio.Probe;

/// <summary>
/// 一次性迁移（v1.5.9）：把工具**页签内容表**在 zzzz 覆盖表里的内容，按"这一行属于哪个页签"拆进
/// `&lt;项目key&gt;_Upgrade_&lt;页签&gt;` 文件（见 <see cref="TabFileNaming"/>），搬空后把 zzzz 文件删掉；
/// 另外可以用 --drop 顺手删掉指定的包内文件（本次：作者那份和 zzzz 逐行重复的 `Yukino_Upgrade_SKV_B` 十份）。
///
/// 只处理五张"页签内容"表：组 / 兵↔组 / 坐标 / 连线 / 路线。
/// **页签本体、授权、成本不动**（跨页签共用，继续留在 zzzz_studio_edits）。
///
/// 用法：probe tab-split &lt;pack&gt; [out.pack] --key Yukino [--drop &lt;包内路径&gt;]... [--dry-run]
///   · 不给 out 就写回原文件（先写 .pretabsplit.bak 备份，再写 .saving 替换）；
///   · --dry-run 只打印计划，不写盘（先看一遍再真跑）。
/// </summary>
public static class TabSplit
{
    private static readonly string[] ScopedTables =
    [
        TabFileNaming.Groups,
        TabFileNaming.Junc,
        TabFileNaming.Infos,
        TabFileNaming.Links,
        TabFileNaming.Routes,
    ];

    public static int Run(string[] a)
    {
        string? src = null, dest = null, key = null;
        var drops = new List<string>();
        var dry = false;
        for (var i = 1; i < a.Length; i++)
        {
            switch (a[i])
            {
                case "--key": key = a[++i]; break;
                case "--drop": drops.Add(a[++i].Replace('\\', '/')); break;
                case "--dry-run": dry = true; break;
                default:
                    if (src is null) src = Path.GetFullPath(a[i]);
                    else if (dest is null) dest = Path.GetFullPath(a[i]);
                    else { Console.Error.WriteLine("多余的参数：" + a[i]); return 1; }
                    break;
            }
        }
        if (src is null)
        {
            Console.Error.WriteLine("用法: probe tab-split <pack> [out.pack] --key <项目key> [--drop <包内路径>]... [--dry-run]");
            return 1;
        }
        var inPlace = dest is null;
        var outPath = inPlace ? src + ".saving" : dest;

        var schema = WarbandStudio.Packfile.Schema.Load(RpfmCli.LocateSchema() ?? throw new FileNotFoundException("找不到 schema。"));
        var repl = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var dropPaths = new List<string>(drops);
        var notes = new List<string>();

        using (var pack = PackArchive.Open(src))
        {
            // --drop 的路径存在性先验一遍（写错名字要当场发现，别到写包才静默没删）
            foreach (var d in drops)
            {
                var hit = pack.Find(d) ?? pack.VisibleEntries.FirstOrDefault(x =>
                    x.Path.Replace('\\', '/').Equals(d, StringComparison.OrdinalIgnoreCase));
                if (hit is null) { Console.Error.WriteLine($"[!] --drop 的路径在包里不存在：{d}"); return 1; }
            }

            var edits = new WarbandEdits { ProjectKey = key ?? "" };
            var res = new TabResolver(pack, edits, schema, repl);

            Console.WriteLine($"[i] 包：{src}（{pack.Entries.Count} 条）　项目 key：{TabFileNaming.Sanitize(key, "studio")}");
            foreach (var table in ScopedTables)
                SplitTable(pack, schema, res, table, repl, dropPaths, notes);

            foreach (var n in notes) Console.WriteLine("      · " + n);

            foreach (var d in drops)
                if (dropPaths.Contains(d, StringComparer.OrdinalIgnoreCase))
                    Console.WriteLine($"[drop] {d}");

            if (!Verify(pack, schema, repl, dropPaths, out var problems))
            {
                Console.Error.WriteLine("[!] 行数校验没过，已中止（没写盘）：");
                foreach (var p in problems) Console.Error.WriteLine("    " + p);
                return 3;
            }

            if (dry)
            {
                Console.WriteLine($"[i] --dry-run：{repl.Count} 个文件会写出、{dropPaths.Count} 个文件会删掉（没有落盘）");
                return 0;
            }
            PackWriter.WriteFrom(pack, repl, outPath, dropPaths: dropPaths);
        }

        if (inPlace)
        {
            var bak = src + ".pretabsplit.bak";
            if (!File.Exists(bak)) File.Copy(src, bak);
            File.Move(outPath, src, overwrite: true);
            Console.WriteLine($"[i] 备份：{Path.GetFileName(bak)}（迁移前的原包，随时可回退）");
        }
        Console.WriteLine($"[✓] 迁移完成：{(inPlace ? src : outPath)}");

        // 写回后再用原生读一遍，确认新文件都在、zzzz 都没了
        using (var check = PackArchive.Open(inPlace ? src : outPath))
        {
            var bad = 0;
            foreach (var table in ScopedTables)
                foreach (var e in TableFiles.EntriesFor(check, table))
                {
                    var n = Path.GetFileName(e.Path);
                    if (n.StartsWith("zzzz_studio", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.Error.WriteLine($"[!] 迁移后仍残留：{e.Path}");
                        bad++;
                    }
                }
            foreach (var d in drops)
                if (check.Find(d) is not null) { Console.Error.WriteLine($"[!] --drop 的文件还在：{d}"); bad++; }
            Console.WriteLine(bad == 0 ? "[✓] 读回校验：zzzz 覆盖表已清空、指定删除的文件都没了" : $"[!] 读回校验发现 {bad} 个问题");
            return bad == 0 ? 0 : 8;
        }
    }

    // ───────────────────────── 拆表 ─────────────────────────

    private static void SplitTable(PackArchive pack, WarbandStudio.Packfile.Schema schema, TabResolver res, string table,
                                   Dictionary<string, byte[]> repl, List<string> dropPaths, List<string> notes)
    {
        var all = TableFiles.EntriesFor(pack, table)
            .Where(e => Path.GetFileName(e.Path).StartsWith("zzzz_studio", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (all.Count == 0) return;

        // 先全部解码：任何一份解不开就**不搬它、也不删它**（宁可不迁移，不能丢内容）
        var sources = new List<(string Path, DbTable T)>();
        foreach (var e in all)
        {
            try { sources.Add((e.Path, DbTable.Decode(pack.ReadDecoded(e), table, schema))); }
            catch (Exception ex) { notes.Add($"⚠ {e.Path} 解码失败，跳过（文件保留）：{ex.Message}"); }
        }
        if (sources.Count == 0) return;

        var buckets = new Dictionary<string, List<(DbValue[] Raw, DbTable Src)>>(StringComparer.OrdinalIgnoreCase);
        var unresolved = 0;
        foreach (var (path, t) in sources)
        {
            for (var i = 0; i < t.Rows.Count; i++)
            {
                var tab = RowTab(t, i, table, res);
                if (tab is null) unresolved++;
                var target = res.PathForTab(table, tab);
                if (!buckets.TryGetValue(target, out var lst)) buckets[target] = lst = [];
                lst.Add(((DbValue[])t.RawRows[i].Clone(), t));
            }
        }

        foreach (var (targetPath, rows) in buckets.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            var existEntry = pack.Find(targetPath) ?? pack.VisibleEntries.FirstOrDefault(x =>
                x.Path.Replace('\\', '/').Equals(targetPath, StringComparison.OrdinalIgnoreCase));
            DbTable? model = null;
            if (existEntry is not null)
            {
                try { model = DbTable.Decode(pack.ReadDecoded(existEntry), table, schema); } catch { model = null; }
            }
            var dstDef = model?.Definition ?? rows[0].Src.Definition;
            var raws = model is null ? new List<DbValue[]>() : model.RawRows.Select(r => (DbValue[])r.Clone()).ToList();
            var at = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < raws.Count; i++)
            {
                var k = Ident(dstDef, raws[i], table);
                if (k.Length > 0 && !at.ContainsKey(k)) at[k] = i;
            }
            var added = 0;
            var merged = 0;
            foreach (var (raw, srcT) in rows)
            {
                var mapped = MapRow(raw, srcT.Definition, dstDef);
                var k = Ident(dstDef, mapped, table);
                if (k.Length > 0 && at.TryGetValue(k, out var oi)) { raws[oi] = mapped; merged++; continue; }
                if (k.Length > 0) at[k] = raws.Count;
                raws.Add(mapped);
                added++;
            }
            var proto = model ?? rows[0].Src;
            repl[targetPath] = EncodeLike(proto, dstDef, raws);
            var what = model is null ? $"新建 {added} 行" : $"并入 {added} 行（原有 {raws.Count - added} 行使同键覆盖 {merged} 行）";
            notes.Add($"{table}：{what} → {targetPath}");
        }

        foreach (var (path, _) in sources)
            if (!dropPaths.Contains(path, StringComparer.OrdinalIgnoreCase)) dropPaths.Add(path);
        if (unresolved > 0)
            notes.Add($"⚠ {table}：{unresolved} 行认不出页签 → 落「{TabFileNaming.FallbackTab}」文件（迁移后核对一下这几行）");
    }

    /// <summary>这一行属于哪个页签：坐标表看自己的 category，其余靠组 →（解析器：包里的 infos / 本会话编辑 / 名字）。</summary>
    private static string? RowTab(DbTable t, int i, string table, TabResolver res)
    {
        string Cell(string col)
        {
            var c = t.Columns.FindIndex(x => x.Name.Equals(col, StringComparison.OrdinalIgnoreCase));
            return c < 0 || i >= t.Rows.Count ? "" : t.Rows[i][c].ToTsv();
        }
        switch (table)
        {
            case TabFileNaming.Infos:
            {
                var cat = Cell("category");
                return cat.Length > 0 ? cat : res.TabOf(Cell("unit_upgrade_group"));
            }
            case TabFileNaming.Groups:
            case TabFileNaming.Junc:
                return res.TabOf(Cell("unit_group"));
            case TabFileNaming.Links:
                return res.TabOf(Cell("child_key")) ?? res.TabOf(Cell("parent_key"));
            case TabFileNaming.Routes:
                return res.TabOf(Cell("base_unit_group")) ?? res.TabOf(Cell("target_unit_group"));
            default:
                return null;
        }
    }

    /// <summary>身份键：键表用键列；键不唯一的表（兵↔组、连线）按整行（迁移只搬不改，别把合法重复吃掉）。</summary>
    private static string Ident(SchemaDefinition def, DbValue[] raw, string table)
    {
        var cols = table switch
        {
            TabFileNaming.Groups => new[] { "unit_group" },
            TabFileNaming.Infos => new[] { "unit_upgrade_group" },
            TabFileNaming.Routes => new[] { "upgrade_key" },
            _ => [],
        };
        if (cols.Length == 0) return string.Join("\u0001", raw.Select(v => v.ToTsv()));
        var parts = new List<string>();
        foreach (var c in cols)
        {
            var i = def.Fields.FindIndex(f => f.Name.Equals(c, StringComparison.OrdinalIgnoreCase));
            parts.Add(i >= 0 && i < raw.Length ? raw[i].ToTsv() : "");
        }
        return string.Join("\u0001", parts);
    }

    /// <summary>源表的行搬进目标定义：字段名对不上的列才需要重排（同表不同文件可以有不同的字段子集）。</summary>
    private static DbValue[] MapRow(DbValue[] src, SchemaDefinition srcDef, SchemaDefinition dstDef)
    {
        if (srcDef.Fields.Count == dstDef.Fields.Count)
        {
            var same = true;
            for (var i = 0; i < srcDef.Fields.Count && same; i++)
                same = srcDef.Fields[i].Name.Equals(dstDef.Fields[i].Name, StringComparison.OrdinalIgnoreCase)
                       && srcDef.Fields[i].Type == dstDef.Fields[i].Type;
            if (same) return src;
        }
        var outp = new DbValue[dstDef.Fields.Count];
        for (var i = 0; i < outp.Length; i++)
        {
            var f = dstDef.Fields[i];
            var si = srcDef.Fields.FindIndex(x => x.Name.Equals(f.Name, StringComparison.OrdinalIgnoreCase));
            outp[i] = si >= 0 && si < src.Length && srcDef.Fields[si].Type == f.Type ? src[si] : DefaultOf(f);
        }
        return outp;
    }

    private static DbValue DefaultOf(SchemaField f) => f.Type switch
    {
        SchemaFieldType.Boolean => DbValue.Of(false),
        SchemaFieldType.F32 or SchemaFieldType.F64 => DbValue.Of(0d),
        SchemaFieldType.I16 or SchemaFieldType.I32 or SchemaFieldType.I64 => DbValue.Of(0L),
        SchemaFieldType.ColourRGB => DbValue.OfColour("FFFFFF"),
        SchemaFieldType.OptionalStringU8 or SchemaFieldType.OptionalStringU16 =>
            new DbValue(DbValueKind.Str, s: "", present: false),
        SchemaFieldType.OptionalI16 or SchemaFieldType.OptionalI32 or SchemaFieldType.OptionalI64 =>
            new DbValue(DbValueKind.Int, i: 0, present: false),
        _ => DbValue.OfStr(""),
    };

    /// <summary>按 <paramref name="proto"/> 的文件头（GUID/神秘字节）与 <paramref name="def"/> 的字段编出一份新表字节。</summary>
    private static byte[] EncodeLike(DbTable proto, SchemaDefinition def, List<DbValue[]> raws)
    {
        var cols = WarbandStudio.Packfile.Schema.ProcessedColumns(def);
        var t = new DbTable
        {
            TableName = def.TableName,
            Version = def.Version,
            Guid = proto.Guid ?? Guid.NewGuid().ToString(),
            MysteriousByte = proto.MysteriousByte,
            Columns = cols,
            Rows = raws.Select(r => ToDisplay(r, cols.Count)).ToList(),
            RawRows = raws,
            Definition = def,
        };
        return DbEncoder.Encode(t);
    }

    private static DbValue[] ToDisplay(DbValue[] raw, int n)
    {
        var row = new DbValue[Math.Max(n, raw.Length)];
        for (var i = 0; i < row.Length; i++) row[i] = i < raw.Length ? raw[i] : DbValue.OfStr("");
        return row;
    }

    // ───────────────────────── 校验 ─────────────────────────

    /// <summary>"迁移不丢内容"：每张表迁移前后的**身份键集合**必须完全一致（重复份被并掉、行数减少是正常的）。</summary>
    private static bool Verify(PackArchive pack, WarbandStudio.Packfile.Schema schema, Dictionary<string, byte[]> repl,
                               List<string> dropPaths, out List<string> problems)
    {
        problems = [];
        foreach (var table in ScopedTables)
        {
            HashSet<string> KeysOf(Func<string, byte[]?> bytesOf)
            {
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in TableFiles.EntriesFor(pack, table))
                {
                    var b = bytesOf(e.Path);
                    if (b is null) continue;
                    try
                    {
                        var t = DbTable.Decode(b, table, schema);
                        for (var i = 0; i < t.Rows.Count; i++) set.Add(Ident(t.Definition, t.RawRows[i], table));
                    }
                    catch { }
                }
                return set;
            }

            var before = KeysOf(p => pack.ReadDecoded(
                pack.Find(p) ?? pack.VisibleEntries.First(x => x.Path.Equals(p, StringComparison.OrdinalIgnoreCase))));

            var after = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var afterRows = 0;
            foreach (var e in TableFiles.EntriesFor(pack, table))
            {
                if (dropPaths.Contains(e.Path, StringComparer.OrdinalIgnoreCase)) continue;
                var b = repl.TryGetValue(e.Path, out var nb) ? nb : pack.ReadDecoded(e);
                try
                {
                    var t = DbTable.Decode(b, table, schema);
                    afterRows += t.Rows.Count;
                    for (var i = 0; i < t.Rows.Count; i++) after.Add(Ident(t.Definition, t.RawRows[i], table));
                }
                catch { }
            }
            var prefix = $"db/{table}/";
            foreach (var kv in repl)
            {
                if (!kv.Key.Replace('\\', '/').StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                var isDrop = dropPaths.Contains(kv.Key, StringComparer.OrdinalIgnoreCase);
                var already = TableFiles.EntriesFor(pack, table).Any(x => x.Path.Equals(kv.Key, StringComparison.OrdinalIgnoreCase));
                if (isDrop || already) continue;    // 已在上面算过（repl 覆盖）或被删
                try
                {
                    var t = DbTable.Decode(kv.Value, table, schema);
                    afterRows += t.Rows.Count;
                    for (var i = 0; i < t.Rows.Count; i++) after.Add(Ident(t.Definition, t.RawRows[i], table));
                }
                catch { }
            }

            var missing = before.Except(after).ToList();
            var extra = after.Except(before).ToList();
            var ok = missing.Count == 0 && extra.Count == 0;
            Console.WriteLine($"[验] {table}：迁移前 {before.Count} 键 → 迁移后 {after.Count} 键 / {afterRows} 行" + (ok ? "  ✓" : "  ✗"));
            if (!ok)
            {
                problems.Add($"{table}：丢失 {missing.Count} 键 / 多出 {extra.Count} 键");
                foreach (var k in missing.Take(3)) problems.Add("    - " + k.Replace("\u0001", " / "));
                foreach (var k in extra.Take(3)) problems.Add("    + " + k.Replace("\u0001", " / "));
            }
        }
        return problems.Count == 0;
    }
}
