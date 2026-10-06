using WarbandStudio.Packfile;

namespace WarbandStudio.Pack;

/// <summary>导出报告。</summary>
public sealed record ExportReport(
    string DestPath,
    int UnitsInTree,
    int Unlocked,
    int AlreadyUnlocked,
    int MissingInVanilla,
    IReadOnlyList<string> AddedEntries,
    IReadOnlyList<string> Notes);

/// <summary>
/// 战帮导出（原生版）。
///
/// 第一条规则：**精英解锁** —— 升级树里出现的兵若在 `main_units_tables` 里
/// `restrict_xp_gain_in_campaign = true`（RoR 之类锁等级），游戏里升不上去。
/// 做法照旧工具：**不动模组原有文件**，而是往 `db/main_units_tables/` 里新增一个覆盖表文件
/// （TW 的表按 key 覆盖，后加载的赢），里面放这些兵的完整行、把该列改成 false。
/// </summary>
public static class WarbandExporter
{
    public const string UnlockTableFileName = "zzzz_studio_elite_unlock";
    private const string UnlockColumn = "restrict_xp_gain_in_campaign";
    private const string UnitsTable = "main_units_tables";
    private const string JunctionTable = "unit_to_unit_group_junctions_tables";

    /// <summary>
    /// 把 <paramref name="packPath"/> 导出成 <paramref name="destPath"/>：
    /// 原样搬运全部条目，外加"精英解锁"覆盖表。
    /// <paramref name="vanillaDbPack"/> 是原版 `data/db.pack`（要它提供兵的完整行）。
    /// </summary>
    public static ExportReport Export(string packPath, string vanillaDbPack, string destPath, Schema schema,
                                      WarbandEdits? edits = null)
    {
        var notes = new List<string>();
        var added = new List<string>();
        var replacements = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        using var pack = PackArchive.Open(packPath);
        using var vanilla = PackArchive.Open(vanillaDbPack);

        // 1) 树里的兵：兵组↔兵 关联表的 unit 列
        var units = CollectTreeUnits(pack, schema, notes);

        // 2) 原版 main_units_tables：拿这些兵的完整行
        var vanillaTable = TableFiles.ReadMerged(vanilla, UnitsTable, schema, notes)
                           ?? throw new InvalidDataException($"原版包里找不到 {UnitsTable}。");
        var keyColumn = TableFiles.FindKeyColumn(vanillaTable);
        var rowByKey = new Dictionary<string, DbValue[]>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < vanillaTable.Rows.Count; i++)
            rowByKey[vanillaTable.Rows[i][keyColumn].ToTsv()] = vanillaTable.RawRows[i];

        var lockIndex = vanillaTable.Definition.Fields.FindIndex(f => f.Name == UnlockColumn);
        if (lockIndex < 0) throw new InvalidDataException($"定义里没有 {UnlockColumn} 列。");

        // 2b) **包里自己已经定义过这些兵**（作者的 mod 表）→ 就地改包内那一行的解锁列，
        //     **不再**往 zzzz_studio_elite_unlock 里写一份"原版拷贝" —— 否则就是重复定义，
        //     而且我们的覆盖表（原版值）会顶掉作者对那一行的其它修改（用户实测：雪乃包里大量重复项目）。
        var packDefined = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unlockedInPack = 0;
        try
        {
            foreach (var ent in TableFiles.EntriesFor(pack, UnitsTable))
            {
                DbTable t;
                try { t = DbTable.Decode(pack.ReadDecoded(ent), UnitsTable, schema); } catch { continue; }
                var k = t.Columns.FindIndex(c => c.Name.Equals("unit", StringComparison.OrdinalIgnoreCase));
                if (k < 0) k = TableFiles.FindKeyColumn(t);
                var li = t.Columns.FindIndex(c => c.Name.Equals(UnlockColumn, StringComparison.OrdinalIgnoreCase));
                if (k < 0 || li < 0) continue;
                var raws = new List<DbValue[]>(t.RawRows.Count);
                var hit = 0;
                for (var i = 0; i < t.Rows.Count; i++)
                {
                    var u = t.Rows[i][k].ToTsv();
                    var raw = (DbValue[])t.RawRows[i].Clone();
                    if (u.Length > 0)
                    {
                        // **只要包里定义过就记下来**（去重用：覆盖表里同一兵的旧行也要剔掉，哪怕它已经不在树里）
                        packDefined.Add(u);
                        // 就地解锁只动"树里要用的兵"（别顺手改作者无关的行）
                        if (units.Contains(u) && raw[li].Kind is DbValueKind.Bool && raw[li].Bool) { raw[li] = DbValue.Of(false); hit++; }
                    }
                    raws.Add(raw);
                }
                if (hit > 0)
                {
                    replacements[ent.Path] = DbEncoder.Encode(new DbTable
                    {
                        TableName = UnitsTable, Version = t.Version, Guid = t.Guid,
                        MysteriousByte = t.MysteriousByte, Columns = t.Columns,
                        Rows = [], RawRows = raws, Definition = t.Definition,
                    });
                    unlockedInPack += hit;
                    added.Add($"{ent.Path}（{hit} 个兵就地解锁）");
                }
            }
            if (packDefined.Count > 0)
                notes.Add($"检测：{packDefined.Count} 个兵在**包里已有定义** → 不写进 zzzz_studio_elite_unlock（避免重复定义/顶掉作者的行）；" +
                          $"其中树里用到的就地改解锁列（{unlockedInPack} 行真的改了）。");
        }
        catch (Exception ex) { notes.Add("包内 main_units 就地解锁跳过：" + ex.Message); }

        // 3) 剩下的（包里没有定义的）兵：拷原版行、把锁等级列改成 false → 覆盖表

        var unlocked = 0;
        var already = 0;
        var missing = new List<string>();
        var newRows = new List<DbValue[]>();
        foreach (var unit in units.OrderBy(u => u, StringComparer.OrdinalIgnoreCase))
        {
            if (packDefined.Contains(unit)) { already++; continue; }      // 包内已定义 → 上面就地改过了，别再写重复行
            if (!rowByKey.TryGetValue(unit, out var row)) { missing.Add(unit); continue; }
            if (row[lockIndex].Kind is DbValueKind.Bool && !row[lockIndex].Bool) { already++; continue; }
            var copy = (DbValue[])row.Clone();
            copy[lockIndex] = DbValue.Of(false);
            newRows.Add(copy);
            unlocked++;
        }

        // 4) 生成覆盖表（同一个定义/版本，只放这些行）
        // 先把画布编辑那一步写好的同名覆盖表读进来（不能整块覆盖，否则会吃掉"加兵自动解锁"的行）
        {
            var unlockPath = $"db/{UnitsTable}/{UnlockTableFileName}";
            var pre = pack.Find(unlockPath);
            if (pre is not null)
            {
                var t0 = DbTable.Decode(pack.ReadDecoded(pre), UnitsTable, schema);
                var have0 = new HashSet<string>(t0.RawRows.Where(r => r.Length > 0)
                    .Select(r => r[0].RawStr ?? r[0].Str ?? ""), StringComparer.OrdinalIgnoreCase);
                var extra = newRows.Where(r => !have0.Contains(r.Length > 0 ? r[0].RawStr ?? r[0].Str ?? "" : "")).ToList();
                // **顺带清掉旧导出留下的重复定义**：覆盖表里那些"包里已经有定义"的行不再保留
                // （用显示行的 ToTsv 取 unit 列 —— RawStr/Str 在字符串列上不一定有值，实测会漏判）
                var kept = new List<DbValue[]>();
                var seenKept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < t0.RawRows.Count; i++)
                {
                    var u = i < t0.Rows.Count && t0.Rows[i].Length > 0 ? t0.Rows[i][0].ToTsv() : "";
                    if (u.Length > 0 && packDefined.Contains(u)) continue;      // 包里已定义 → 剔掉（就地解锁）
                    if (u.Length > 0 && !seenKept.Add(u)) continue;             // 同 unit 的旧重复行 → 只留第一行
                    kept.Add(t0.RawRows[i]);
                }
                if (kept.Count != t0.RawRows.Count)
                {
                    notes.Add($"zzzz_studio_elite_unlock：剔掉 {t0.RawRows.Count - kept.Count} 行重复定义（这些兵包里自己定义了，已就地解锁）。");
                    t0.RawRows.Clear();
                    t0.RawRows.AddRange(kept);
                    replacements[unlockPath] = DbEncoder.Encode(t0);
                }
                if (extra.Count > 0)
                {
                    newRows.AddRange(extra);
                    notes.Add($"精英解锁：并入包里已有的 {extra.Count} 行（画布上新增的兵）。");
                }
            }
        }
        // **同 unit 只留一行**：树里的兵和覆盖表里已有的行可能重上（旧导出还留下过重复行 —— 用户实测"main 表里每个兵两份"）
        if (newRows.Count > 0)
        {
            var seenU = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var uniq = new List<DbValue[]>();
            foreach (var r in newRows)
            {
                var u = r.Length > 0 ? r[0].ToTsv() : "";
                if (u.Length == 0 && r.Length > 0) u = r[0].RawStr ?? r[0].Str ?? "";
                if (u.Length > 0 && !seenU.Add(u)) continue;
                uniq.Add(r);
            }
            if (uniq.Count != newRows.Count)
            {
                notes.Add($"zzzz_studio_elite_unlock：去掉 {newRows.Count - uniq.Count} 行重复（同 unit 只留一行）。");
                newRows = uniq;
            }
        }
        if (newRows.Count > 0)
        {
            var unlockTable = new DbTable
            {
                TableName = UnitsTable,
                Version = vanillaTable.Version,
                Guid = Guid.NewGuid().ToString(),
                MysteriousByte = vanillaTable.MysteriousByte,
                Columns = Schema.ProcessedColumns(vanillaTable.Definition),
                Rows = [],              // 显示行用不到；写回只读 RawRows
                RawRows = newRows,
                Definition = vanillaTable.Definition,
            };
            var innerPath = $"db/{UnitsTable}/{UnlockTableFileName}";
            replacements[innerPath] = DbEncoder.Encode(unlockTable);
            added.Add($"{innerPath}（{newRows.Count} 行）");
            notes.Add($"覆盖表只含解锁行；游戏按 key 覆盖，模组原有文件一个都没动。");
        }
        else
        {
            notes.Add("没有需要解锁的兵（都已经是解锁状态，或原版表里找不到）。");
        }

        // 4a) 四神：保证四个混沌神文化都有一行 WARBANDS（原版表里只有 wh_main_chs_chaos 有）
        var gods = new[] { "wh3_main_kho_khorne", "wh3_main_nur_nurgle", "wh3_main_sla_slaanesh", "wh3_main_tze_tzeentch" };
        try
        {
            var ufc = TableFiles.ReadMerged(pack, "ui_features_to_cultures_tables", schema, notes);
            if (ufc is not null)
            {
                var cIdx = ufc.Columns.FindIndex(c => c.Name.Contains("culture", StringComparison.OrdinalIgnoreCase));
                var fIdx = ufc.Columns.FindIndex(c => c.Name.Contains("feature", StringComparison.OrdinalIgnoreCase));
                if (cIdx < 0 || fIdx < 0) { cIdx = TableFiles.FindKeyColumn(ufc); fIdx = cIdx == 0 ? 1 : 0; }

                var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < ufc.Rows.Count; i++)
                    if (ufc.Rows[i][fIdx].ToTsv().Equals("WARBANDS", StringComparison.OrdinalIgnoreCase))
                        have.Add(ufc.Rows[i][cIdx].ToTsv());

                var addRows = new List<DbValue[]>(); var addRaw = new List<DbValue[]>();
                foreach (var g in gods)
                {
                    if (have.Contains(g)) continue;
                    // 照抄一行同结构的（用第一行做模板，只改 culture/feature 两列）
                    var src = ufc.RawRows.FirstOrDefault(r => r.Length == ufc.Definition.Fields.Count);
                    if (src is null) break;
                    var raw = (DbValue[])src.Clone();
                    var ci = ufc.Definition.Fields.FindIndex(f => f.Name.Contains("culture", StringComparison.OrdinalIgnoreCase));
                    var fi = ufc.Definition.Fields.FindIndex(f => f.Name.Contains("feature", StringComparison.OrdinalIgnoreCase));
                    if (ci < 0 || fi < 0) break;
                    raw[ci] = DbValue.OfStr(g);
                    raw[fi] = DbValue.OfStr("WARBANDS");
                    addRaw.Add(raw);
                    var row = new DbValue[ufc.Columns.Count];
                    for (var k = 0; k < row.Length; k++) row[k] = DbValue.OfStr("");
                    addRows.Add(row);
                }
                if (addRaw.Count > 0)
                {
                    var t = new DbTable
                    {
                        TableName = "ui_features_to_cultures_tables",
                        Version = ufc.Version,
                        Guid = Guid.NewGuid().ToString(),
                        MysteriousByte = ufc.MysteriousByte,
                        Columns = Schema.ProcessedColumns(ufc.Definition),
                        Rows = addRows,
                        RawRows = addRaw,
                        Definition = ufc.Definition,
                    };
                    var path2 = "db/ui_features_to_cultures_tables/studio_gods";
                    replacements[path2] = DbEncoder.Encode(t);
                    added.Add($"{path2}（补 {addRaw.Count} 个神的 WARBANDS 行）");
                }
                else notes.Add("四神：四个文化都已有 WARBANDS 行，不用补。");
            }
        }
        catch (Exception e) { notes.Add("四神规则跳过：" + e.Message); }

        // 4b) 画布上的编辑（坐标/页签、加兵删兵、升级关系与连线、成本）—— 见 WarbandAmender
        if (edits is { IsEmpty: false })
        {
            try
            {
                var n = WarbandAmender.Apply(pack, vanilla, edits, schema, replacements, notes);
                added.Add($"画布编辑 → {n} 个文件（{edits.Summary()}）");
            }
            catch (Exception ex) { notes.Add("画布编辑落表失败：" + ex.Message); }
        }

        // 4c) 引用体检（导出前把关：解析不了的引用会让游戏报 Bad Mod）
        try
        {
            string[] checkTables = ["unit_upgrade_groups_tables", "unit_to_unit_group_junctions_tables",
                "unit_upgrade_group_ui_infos_tables", "unit_upgrade_group_ui_categories_tables",
                "unit_upgrade_group_ui_links_tables", "unit_upgrade_to_unit_groups_tables",
                "unit_upgrade_to_tech_requirements_tables", "resource_costs_tables",
                "resource_cost_pooled_resource_junctions_tables", "campaign_features_tables",
                "ui_features_to_cultures_tables", "units_to_groupings_military_permissions_tables",
                "main_units_tables", "land_units_tables"];
            DbTable? Resolve(string name)
            {
                var n2 = name.EndsWith("_tables", StringComparison.OrdinalIgnoreCase) ? name : name + "_tables";
                try
                {
                    var t = TableFiles.ReadMerged(vanilla, n2, schema, null);
                    t = TableFiles.Merge(t, TableFiles.ReadMerged(pack, n2, schema, null));
                    return t;
                }
                catch { return null; }   // 没有定义的目标表按"缺来源"处理，不让它打断整轮体检
            }
            var rep = ReferenceChecker.Check(checkTables, schema, Resolve, notes);
            if (rep.Ok) notes.Add($"引用体检：{rep.Checked} 处引用全部解析得了。");
            else
            {
                notes.Add($"引用体检：{rep.Checked} 处里有 {rep.Issues.Count} 处解析不了（游戏可能报 Bad Mod）——" +
                          string.Join("；", rep.Issues.Take(3).Select(i => $"{i.Table}.{i.Column}={i.Value}→{i.TargetTable}")));
                foreach (var extra in rep.Issues.Skip(3).Take(5))
                    notes.Add("   还有：" + $"{extra.Table}.{extra.Column}={extra.Value}→{extra.TargetTable}");
            }
        }
        catch (Exception e) { notes.Add("引用体检跳过：" + e.Message); }

        // 5) 写包（其余条目原样搬运）
        PackWriter.WriteFrom(pack, replacements, destPath, dropPaths: edits?.RemoveFiles);   // 文件树里删掉的条目跳过去

        return new ExportReport(destPath, units.Count, unlocked, already, missing.Count, added, notes.Concat(missing.Take(5).Select(m => "原版表里没有：" + m)).ToList());
    }

    /// <summary>树里的兵 = 兵组↔兵 关联表里的 unit 键（战帮页上每一张兵牌挂的兵）。</summary>
    private static HashSet<string> CollectTreeUnits(PackArchive pack, Schema schema, List<string> notes)
    {
        var units = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var table = TableFiles.ReadMerged(pack, JunctionTable, schema, notes);
        if (table is null)
        {
            notes.Add($"这个包里没有 {JunctionTable}，按「无兵可解锁」处理。");
            return units;
        }
        // 列名里找 unit 那一列（定义可能叫 unit / unit_key）
        var idx = table.Columns.FindIndex(c => c.Name.Equals("unit", StringComparison.OrdinalIgnoreCase)
                                            || c.Name.Equals("unit_key", StringComparison.OrdinalIgnoreCase)
                                            || c.Name.EndsWith("_unit", StringComparison.OrdinalIgnoreCase));
        if (idx < 0) { notes.Add($"{JunctionTable} 里找不到 unit 列（列：{string.Join(", ", table.Columns.Select(c => c.Name).Take(5))}…）。"); return units; }
        foreach (var row in table.Rows)
        {
            var v = row[idx].ToTsv();
            if (v.Length > 0) units.Add(v);
        }
        notes.Add($"{JunctionTable}：{table.Rows.Count} 行 → 去重后 {units.Count} 个兵。");
        return units;
    }

}
