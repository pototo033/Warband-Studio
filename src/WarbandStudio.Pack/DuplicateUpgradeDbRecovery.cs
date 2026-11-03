using WarbandStudio.Packfile;

namespace WarbandStudio.Pack;

/// <summary>把“已验证的编译基准 → 当前文件”的外部编辑应用到作者快照，绝不把未改的内部预算当作真实成本。</summary>
internal static class DuplicateUpgradeDbRecovery
{
    public static byte[]? Rebase(string path, DuplicateUpgradeArchive.FileState state, byte[] current, Schema schema)
    {
        var name = PackSession.TableNameOf(path);
        if (!DuplicateUpgradeCompiler.Keys.TryGetValue(name, out var keys))
            throw new InvalidDataException("此表缺少安全恢复所需组合键，原包未修改：" + path);
        var compiled = DbTable.Decode(state.Compiled!, name, schema);
        var external = DbTable.Decode(current, name, schema);
        var original = state.Original is null ? null : DbTable.Decode(state.Original, name, schema);
        foreach (var table in new[] { external, original }.OfType<DbTable>())
            if (table.Version != compiled.Version || !table.Definition.Fields.Select(f => (f.Name, f.Type))
                    .SequenceEqual(compiled.Definition.Fields.Select(f => (f.Name, f.Type))))
                throw new InvalidDataException("外部表的版本/字段已变化，不能安全自动合并：" + path);
        var indices = keys.Select(k => compiled.Definition.Fields.FindIndex(f => f.Name == k)).ToArray();
        if (indices.Any(i => i < 0)) throw new InvalidDataException("外部表缺少组合键列：" + path);
        string Key(DbValue[] row) => string.Join('\u0001', indices.Select(i => row[i].ToTsv()));
        Dictionary<string, DbValue[]> Rows(DbTable? t)
        {
            var result = new Dictionary<string, DbValue[]>(StringComparer.Ordinal);
            foreach (var row in t?.RawRows ?? [])
                if (!result.TryAdd(Key(row), row)) throw new InvalidDataException("外部表存在重复组合键，无法安全恢复：" + path);
            return result;
        }
        var authored = Rows(original); var baseline = Rows(compiled); var changed = Rows(external);
        var removed = new HashSet<string>(); var patches = new List<DbValue[]>();
        foreach (var key in baseline.Keys.Where(k => !changed.ContainsKey(k)))
        {
            if (!authored.ContainsKey(key))
                throw new InvalidDataException("外部删除了自动生成的兼容行，无法推断作者路线的删除意图；原包未修改：" + path + " / " + key);
            removed.Add(key);
        }
        foreach (var (key, row) in changed)
        {
            var prior = baseline.GetValueOrDefault(key);
            if (prior is not null && row.Zip(prior).All(p => Equal(p.First, p.Second))) continue;
            var source = authored.GetValueOrDefault(key);
            var patch = source is not null && prior is not null
                ? source.Select((cell, i) => Equal(row[i], prior[i]) ? cell : row[i]).ToArray()
                : row.ToArray();
            // 新行/被外部明确改成作者成本的桥接行可以保留；无法解释的内部预算引用必须拒绝。
            if (patch.Any(v => v.Kind == DbValueKind.Str && IsBudget(v.RawStr ?? v.Str ?? "")))
                throw new InvalidDataException("外部编辑仍引用内部预算成本/资源，需先选择作者成本，原包未修改：" + path + " / " + key);
            patches.Add(patch);
        }
        var template = original ?? compiled;
        DbTable FromRows(List<DbValue[]> rows) => DbTable.Decode(DbEncoder.Encode(new DbTable
        {
            TableName = name, Version = template.Version, Guid = template.Guid, MysteriousByte = template.MysteriousByte,
            Definition = template.Definition, Columns = template.Columns, Rows = [], RawRows = rows
        }), name, schema);
        var retained = authored.Where(p => !removed.Contains(p.Key)).Select(p => p.Value).ToList();
        // 全部覆盖均走正式组合键合并，成本↔资源池不会退化成单列键。
        var merged = TableFiles.MergeComposite(FromRows(retained), FromRows(patches), keys)!;
        return original is null && merged.RawRows.Count == 0 ? null : DbEncoder.Encode(merged);
    }

    private static bool IsBudget(string value) => value.StartsWith("studio_du_cost_", StringComparison.Ordinal)
        || value.StartsWith("studio_du_factions_", StringComparison.Ordinal)
        || value.StartsWith("studio_wb_duplicate_", StringComparison.Ordinal)
        || System.Text.RegularExpressions.Regex.IsMatch(value, "^studio_du_[0-9a-f]{16}(?:_sync|_pay)?$");

    private static bool Equal(DbValue a, DbValue b) => a.Kind == b.Kind && a.Present == b.Present && a.Kind switch
    {
        DbValueKind.Bool => a.Bool == b.Bool, DbValueKind.Int => a.Int == b.Int,
        DbValueKind.Float => BitConverter.DoubleToInt64Bits(a.Float) == BitConverter.DoubleToInt64Bits(b.Float),
        DbValueKind.Str => (a.RawStr ?? a.Str) == (b.RawStr ?? b.Str),
        DbValueKind.Colour => a.Str == b.Str,
        DbValueKind.Bytes => (a.Raw ?? []).AsSpan().SequenceEqual(b.Raw ?? []), _ => false
    };
}
