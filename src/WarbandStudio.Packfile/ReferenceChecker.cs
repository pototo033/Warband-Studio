namespace WarbandStudio.Packfile;

/// <summary>一条解析不了的引用（会让游戏报 Bad Mod 的那类）。</summary>
public sealed record RefIssue(string Table, string Column, string Value, string TargetTable, string TargetColumn);

/// <summary>引用体检的结果。</summary>
public sealed record RefReport(int Checked, IReadOnlyList<RefIssue> Issues, IReadOnlyList<string> MissingTargets)
{
    public bool Ok => Issues.Count == 0;
}

/// <summary>
/// 引用体检：按 schema 里每个字段的 <c>is_reference=(表,列)</c> 元数据，
/// 检查"这一列的取值有没有对应的记录"，目标表的合法值集合由调用方给的解析器提供
/// （实际就是"本包 → 原版 data/db.pack"跨来源合并，缺来源就报出来，不装作解析成功）。
/// </summary>
public static class ReferenceChecker
{
    public static RefReport Check(IEnumerable<string> tableNames, Schema schema,
                                  Func<string, DbTable?> resolveTable,
                                  List<string>? notes = null)
    {
        var checkedCells = 0;
        var issues = new List<RefIssue>();
        var missingTargets = new List<string>();
        var keyCache = new Dictionary<string, HashSet<string>?>(StringComparer.OrdinalIgnoreCase);

        HashSet<string>? KeysOf(string targetTable, string targetColumn)
        {
            if (keyCache.TryGetValue(targetTable, out var cached)) return cached;
            var t = resolveTable(targetTable);
            HashSet<string>? keys = null;
            if (t is not null)
            {
                var col = t.Columns.FindIndex(c => c.Name.Equals(targetColumn, StringComparison.OrdinalIgnoreCase));
                if (col < 0) col = TableFiles.FindKeyColumn(t);
                keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var row in t.Rows) keys.Add(row[col].ToTsv());
            }
            else missingTargets.Add(targetTable);
            keyCache[targetTable] = keys;
            return keys;
        }

        foreach (var name in tableNames)
        {
            var table = resolveTable(name);
            if (table is null) { notes?.Add($"体检跳过 {name}（本包与原版都没有）"); continue; }

            for (var f = 0; f < table.Definition.Fields.Count && f < table.Columns.Count; f++)
            {
                var field = table.Definition.Fields[f];
                if (field.IsReference is not { } target) continue;
                // 被后处理过的列（位展开 / 枚举换名 / 颜色合并）在显示行里位置会错位，跳过
                if (field.IsBitwise > 1 || field.EnumValues.Count > 0 || field.IsPartOfColour is not null) continue;

                // 后处理过的列（位展开/枚举换名/颜色合并）位置会错位，这里只查常规列（名字对得上）
                var col = table.Columns.FindIndex(c => c.Name.Equals(field.Name, StringComparison.OrdinalIgnoreCase));
                if (col < 0) continue;

                var keys = KeysOf(target.Table, target.Column);
                if (keys is null) continue;   // 目标表缺来源，已在 MissingTargets 里报过

                foreach (var row in table.Rows)
                {
                    var v = row[col].ToTsv();
                    if (v.Length == 0) continue;
                    checkedCells++;
                    if (!keys.Contains(v)) issues.Add(new RefIssue(name, field.Name, v, target.Table, target.Column));
                }
            }
        }
        return new RefReport(checkedCells, issues, missingTargets);
    }
}
