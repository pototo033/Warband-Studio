namespace WarbandStudio.Packfile;

/// <summary>
/// TW 允许**一个表分散在多个文件**里（例如 `db/main_units_tables/` 下多个文件），
/// 加载时按 key 覆盖、后加载的赢。这里把"读全目录 + 合并"这件反复要用的事收成一个入口。
/// </summary>
public static class TableFiles
{
    /// <summary>表在包里的全部文件（按包内索引顺序 = 游戏加载顺序）。</summary>
    public static List<PackEntry> EntriesFor(PackArchive pack, string tableName) =>
        pack.VisibleEntries
            .Where(e => e.Path.StartsWith($"db/{tableName}/", StringComparison.OrdinalIgnoreCase))
            .ToList();

    /// <summary>
    /// 解码并**拼接**（不按键覆盖）。给键列不唯一的表用：
    ///   · ui_links —— 同一个子组可以连到多个父组（实测原版就有）
    ///   · junction  —— 同一个兵可以同时属于多个组
    ///   · 升级路线 —— 同一个 upgrade_key 可以在多个文件里各写一条
    /// 按键合并会把这类行吃掉（实测 751 条连线被吃掉 110 条），所以这几张表必须走这里。
    /// </summary>
    public static DbTable? ReadConcat(PackArchive pack, string tableName, Schema schema, List<string>? notes = null)
    {
        var files = EntriesFor(pack, tableName);
        if (files.Count == 0) return null;

        DbTable? model = null;
        var rows = new List<DbValue[]>();
        var raws = new List<DbValue[]>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skipped = 0;

        foreach (var f in files)
        {
            var t = DbTable.Decode(pack.ReadDecoded(f), tableName, schema);
            if (model is null) model = t;
            else if (t.Definition.Fields.Count != model.Definition.Fields.Count || t.Version != model.Version)
            {
                skipped++;
                continue;
            }
            for (var i = 0; i < t.Rows.Count; i++)
            {
                var sig = string.Join('\u0001', t.Rows[i].Select(v => v.ToTsv()));
                if (!seen.Add(sig)) continue;                      // 完全相同的行只留一条
                rows.Add(t.Rows[i]);
                raws.Add(t.RawRows[i]);
            }
        }

        if (skipped > 0)
            notes?.Add($"{tableName}：有 {skipped} 个文件的定义/版本不同，已跳过。");

        return new DbTable
        {
            TableName = tableName,
            Version = model!.Version,
            Guid = model.Guid,
            MysteriousByte = model.MysteriousByte,
            Columns = model.Columns,
            Rows = rows,
            RawRows = raws,
            Definition = model.Definition,
        };
    }

    /// <summary>
    /// 解码并按键合并；后者覆盖前者。定义不一致（版本不同）的文件只认第一个、其余跳过，
    /// 跳过的情况通过 <paramref name="notes"/> 报出来。
    /// </summary>
    public static DbTable? ReadMerged(PackArchive pack, string tableName, Schema schema, List<string>? notes = null)
    {
        var files = EntriesFor(pack, tableName);
        if (files.Count == 0) return null;

        DbTable? model = null;
        var rows = new List<DbValue[]>();
        var raws = new List<DbValue[]>();
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var keyColumn = 0;
        var skipped = 0;

        foreach (var f in files)
        {
            var t = DbTable.Decode(pack.ReadDecoded(f), tableName, schema);
            if (model is null)
            {
                model = t;
                keyColumn = FindKeyColumn(t);
                for (var i = 0; i < t.Rows.Count; i++)
                {
                    rows.Add(t.Rows[i]); raws.Add(t.RawRows[i]);
                    index[t.Rows[i][keyColumn].ToTsv()] = rows.Count - 1;
                }
                continue;
            }
            if (t.Definition.Fields.Count != model.Definition.Fields.Count
                || t.Version != model.Version)
            {
                skipped++;
                continue;
            }
            for (var i = 0; i < t.Rows.Count; i++)
            {
                var key = t.Rows[i][keyColumn].ToTsv();
                if (index.TryGetValue(key, out var at)) { rows[at] = t.Rows[i]; raws[at] = t.RawRows[i]; }
                else { rows.Add(t.Rows[i]); raws.Add(t.RawRows[i]); index[key] = rows.Count - 1; }
            }
        }

        if (skipped > 0)
            notes?.Add($"{tableName}：有 {skipped} 个文件的定义/版本不同，已跳过（只合并了同定义的）。");

        return new DbTable
        {
            TableName = tableName,
            Version = model!.Version,
            Guid = model.Guid,
            MysteriousByte = model.MysteriousByte,
            Columns = model.Columns,
            Rows = rows,
            RawRows = raws,
            Definition = model.Definition,
        };
    }

    /// <summary>两张同定义的表按 key 合并（后者覆盖前者）——体检/画布都要"本包 + 原版"的并集。</summary>
    public static DbTable? Merge(DbTable? baseT, DbTable? over)
    {
        if (baseT is null) return over;
        if (over is null) return baseT;
        var key = FindKeyColumn(baseT);
        var rows = new List<DbValue[]>(baseT.Rows);
        var raws = new List<DbValue[]>(baseT.RawRows);
        var idx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < rows.Count; i++) idx[rows[i][key].ToTsv()] = i;
        for (var i = 0; i < over.Rows.Count; i++)
        {
            var k = over.Rows[i][key].ToTsv();
            if (idx.TryGetValue(k, out var at)) { rows[at] = over.Rows[i]; raws[at] = over.RawRows[i]; }
            else { idx[k] = rows.Count; rows.Add(over.Rows[i]); raws.Add(over.RawRows[i]); }
        }
        return new DbTable
        {
            TableName = baseT.TableName, Version = baseT.Version, Guid = baseT.Guid,
            MysteriousByte = baseT.MysteriousByte, Columns = baseT.Columns,
            Rows = rows, RawRows = raws, Definition = baseT.Definition,
        };
    }

    public static int FindKeyColumn(DbTable table)
    {
        var idx = table.Columns.FindIndex(c => c.Source?.IsKey == true);
        return idx >= 0 ? idx : 0;
    }

    /// <summary>
    /// 拼接读 + 按**多列组合键**去重（键相同以**后面的文件**为准）。
    /// 给"schema 的键只标了第一列、真实身份是多列"的表用：
    /// `units_to_groupings_military_permissions_tables` 的真实身份是 **(unit, military_group)** ——
    /// 一个兵可以同时挂多个军事组，用 <see cref="ReadMerged"/>（按第一列 unit 合并）会把其它组的行**静默吃掉**
    /// （实测原版 5340 行被并成 1945 条，表现就是"打开 mod 包以后兵全变成不属于当前军事组"）。
    /// </summary>
    public static DbTable? ReadComposite(PackArchive pack, string tableName, string[] keyCols, Schema schema,
                                         List<string>? notes = null)
    {
        var files = EntriesFor(pack, tableName);
        if (files.Count == 0) return null;

        DbTable? model = null;
        var rows = new List<DbValue[]>();
        var raws = new List<DbValue[]>();
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var cols = Array.Empty<int>();
        var skipped = 0;

        foreach (var f in files)
        {
            var t = DbTable.Decode(pack.ReadDecoded(f), tableName, schema);
            if (model is null)
            {
                model = t;
                cols = ResolveKeyColumns(t, keyCols);
            }
            else if (t.Definition.Fields.Count != model.Definition.Fields.Count || t.Version != model.Version)
            {
                skipped++;
                continue;
            }
            for (var i = 0; i < t.Rows.Count; i++)
            {
                var key = CompositeKey(t, t.Rows[i], cols);
                if (index.TryGetValue(key, out var at)) { rows[at] = t.Rows[i]; raws[at] = t.RawRows[i]; }
                else { rows.Add(t.Rows[i]); raws.Add(t.RawRows[i]); index[key] = rows.Count - 1; }
            }
        }
        if (skipped > 0)
            notes?.Add($"{tableName}：有 {skipped} 个文件的定义/版本不同，已跳过（只合并了同定义的）。");

        return new DbTable
        {
            TableName = tableName,
            Version = model!.Version,
            Guid = model.Guid,
            MysteriousByte = model.MysteriousByte,
            Columns = model.Columns,
            Rows = rows,
            RawRows = raws,
            Definition = model.Definition,
        };
    }

    /// <summary>
    /// 两张同定义的表按**多列组合键**合并（后者覆盖前者；键不同的一律并集）。
    /// 用途见 <see cref="ReadComposite"/>：军事组授权这类"组合键 + 一个 unit 多行"的表，
    /// 用 <see cref="Merge"/>（按第一列合并）会让本包的行**顶掉**原版同一个兵的其它组。
    /// </summary>
    public static DbTable? MergeComposite(DbTable? baseT, DbTable? over, string[] keyCols)
    {
        if (baseT is null) return over;
        if (over is null) return baseT;
        var cols = ResolveKeyColumns(baseT, keyCols);
        var rows = new List<DbValue[]>(baseT.Rows);
        var raws = new List<DbValue[]>(baseT.RawRows);
        var idx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < rows.Count; i++) idx[CompositeKey(baseT, rows[i], cols)] = i;
        for (var i = 0; i < over.Rows.Count; i++)
        {
            var k = CompositeKey(over, over.Rows[i], cols);
            if (idx.TryGetValue(k, out var at)) { rows[at] = over.Rows[i]; raws[at] = over.RawRows[i]; }
            else { idx[k] = rows.Count; rows.Add(over.Rows[i]); raws.Add(over.RawRows[i]); }
        }
        return new DbTable
        {
            TableName = baseT.TableName, Version = baseT.Version, Guid = baseT.Guid,
            MysteriousByte = baseT.MysteriousByte, Columns = baseT.Columns,
            Rows = rows, RawRows = raws, Definition = baseT.Definition,
        };
    }

    /// <summary>把列名解析成列号（找不到的列忽略；一个都没找到就退回 schema 标的键列）。</summary>
    private static int[] ResolveKeyColumns(DbTable t, string[] keyCols)
    {
        var list = new List<int>();
        foreach (var name in keyCols)
        {
            var i = t.Columns.FindIndex(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (i >= 0 && !list.Contains(i)) list.Add(i);
        }
        if (list.Count == 0) list.Add(FindKeyColumn(t));
        return list.ToArray();
    }

    private static string CompositeKey(DbTable t, DbValue[] row, int[] cols)
    {
        var parts = new string[cols.Length];
        for (var i = 0; i < cols.Length; i++)
            parts[i] = cols[i] >= 0 && cols[i] < row.Length ? row[cols[i]].ToTsv() : "";
        return string.Join("\u0001", parts);
    }
}
