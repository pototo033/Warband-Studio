using System.IO;
using WarbandStudio.Packfile;

namespace WarbandStudio.Ui.Services;

public sealed partial class Backend
{
    private PackArchive? _costIndexArchive;
    private Schema? _costIndexSchema;
    private string _costGameStamp = "";
    private readonly Dictionary<string, DbTable?> _costTables = new();
    private List<WarbandCost>? _costListCache;
    private List<PooledResource>? _resourceListCache;
    private int _costListRevision = -1, _resourceListRevision = -1;

    private DbTable? CostTable(string name, string[]? keys = null, bool modOnly = false)
    {
        var archive = _pack?.Archive;
        if (archive is null) return null;
        var schema = GetSchema();
        var game = VanillaDbPackPath;
        var file = game is null ? null : new FileInfo(game);
        var stamp = file?.Exists == true ? file.FullName + ":" + file.Length + ":" + file.LastWriteTimeUtc.Ticks : "";
        if (!ReferenceEquals(archive, _costIndexArchive) || !ReferenceEquals(schema, _costIndexSchema) || stamp != _costGameStamp)
        {
            _costTables.Clear(); _costListCache = null; _resourceListCache = null;
            _costIndexArchive = archive; _costIndexSchema = schema; _costGameStamp = stamp;
        }
        var key = name + ":" + modOnly + ":" + string.Join(",", keys ?? []);
        if (_costTables.TryGetValue(key, out var cached)) return cached;
        using var vanilla = !modOnly && file?.Exists == true ? PackArchive.Open(game!) : null;
        var table = keys is null
            ? TableFiles.Merge(vanilla is null ? null : TableFiles.ReadMerged(vanilla, name, schema), TableFiles.ReadMerged(archive, name, schema))
            : TableFiles.MergeComposite(vanilla is null ? null : TableFiles.ReadComposite(vanilla, name, keys, schema), TableFiles.ReadComposite(archive, name, keys, schema), keys);
        _costTables[key] = table;
        return table;
    }
    private static string CostCell(DbTable table, int row, string column)
    {
        var c = Col(table, column);
        return c < 0 ? "" : table.Rows[row][c].ToTsv();
    }
    private static long CostNumber(DbTable table, int row, string column)
    {
        var c = Col(table, column);
        return c < 0 ? 0 : (long)table.Rows[row][c].Num;
    }
    private string ResourceOfPool(string pool)
    {
        var pending = Edits.PoolFactors.LastOrDefault(x => x.UniqueId.Equals(pool, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(pending.Resource)) return pending.Resource;
        var table = CostTable("pooled_resource_factor_junctions_tables");
        if (table is not null)
            for (var i = 0; i < table.Rows.Count; i++)
                if (CostCell(table, i, "unique_id").Equals(pool, StringComparison.OrdinalIgnoreCase)) return CostCell(table, i, "resource");
        return "";
    }
    private string ResourceIconFile(string resource)
    {
        var table = CostTable("pooled_resources_tables");
        if (table is null) return "";
        for (var i = 0; i < table.Rows.Count; i++)
        {
            if (!CostCell(table, i, "key").Equals(resource, StringComparison.OrdinalIgnoreCase)) continue;
            var icon = CostCell(table, i, "optional_icon_path");
            return icon.Length == 0 ? "" : Icons.IconFileFor(icon, "res_" + IconResolver.Stem(icon)) ?? "";
        }
        return "";
    }
    public List<string> CostReferences(string costId)
    {
        var result = new List<string>();
        if (_pack?.Archive is not { } archive) return result;
        foreach (var (name, definitions) in GetSchema().Definitions)
        {
            if (name == "resource_cost_pooled_resource_junctions_tables") continue;
            var columns = definitions.SelectMany(d => d.Fields).Where(f =>
                f.IsReference is { } reference && reference.Table.Replace("_tables", "", StringComparison.OrdinalIgnoreCase).Equals("resource_costs", StringComparison.OrdinalIgnoreCase))
                .Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (columns.Length == 0 || !TableFiles.EntriesFor(archive, name).Any()) continue;
            // 无法解码的消费表必须上报，不能把它当成没有引用而允许删除。
            var table = TableFiles.ReadConcat(archive, name, GetSchema());
            if (table is null) continue;
            for (var i = 0; i < table.Rows.Count; i++)
            {
                if (name == "unit_upgrade_to_unit_groups_tables")
                {
                    var routeKey = CostCell(table, i, "upgrade_key");
                    if (Edits.RemoveRoute.Contains(routeKey, StringComparer.OrdinalIgnoreCase) || Edits.AddRoute.Any(r => r.UpgradeKey.Equals(routeKey, StringComparison.OrdinalIgnoreCase))) continue;
                    if (Edits.RemoveGroup.Contains(CostCell(table, i, "base_unit_group"), StringComparer.OrdinalIgnoreCase)
                        || Edits.RemoveGroup.Contains(CostCell(table, i, "target_unit_group"), StringComparer.OrdinalIgnoreCase)) continue;
                }
                foreach (var column in columns)
                    if (CostCell(table, i, column).Equals(costId, StringComparison.OrdinalIgnoreCase)) result.Add($"{name}:{column}（行 {i + 1}）");
            }
        }
        result.AddRange(Edits.AddRoute.Where(r => r.Cost.Equals(costId, StringComparison.OrdinalIgnoreCase)).Select(r => "unit_upgrade_to_unit_groups_tables:" + r.UpgradeKey));
        return result;
    }
}
