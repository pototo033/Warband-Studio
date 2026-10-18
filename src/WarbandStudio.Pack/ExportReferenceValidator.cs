using System.Xml.Linq;
using WarbandStudio.Packfile;

namespace WarbandStudio.Pack;

/// <summary>校验最终缓冲而非旧包；无法验证也必须阻止提交，不能伪装成引用正确。</summary>
public static class ExportReferenceValidator
{
    public static RefReport Validate(PackArchive pack, PackArchive vanilla, Schema schema,
        IReadOnlyDictionary<string, byte[]> replacements, IReadOnlyCollection<string>? removed = null)
    {
        var dropped = new HashSet<string>((removed ?? []).Select(Norm), StringComparer.OrdinalIgnoreCase);
        var final = pack.VisibleEntries.Where(e => IsDb(e.Path) && !dropped.Contains(Norm(e.Path)))
            .ToDictionary(e => Norm(e.Path), e => (Func<byte[]>)(() => pack.ReadDecoded(e)), StringComparer.OrdinalIgnoreCase);
        foreach (var (path, bytes) in replacements)
            if (IsDb(path) && !dropped.Contains(Norm(path))) final[Norm(path)] = () => bytes;
        var decoded = new Dictionary<string, DbTable>(StringComparer.OrdinalIgnoreCase);
        DbTable Decode(string path, Func<byte[]> bytes)
        {
            if (decoded.TryGetValue(path, out var t)) return t;
            try { return decoded[path] = DbTable.Decode(bytes(), TableName(path), schema); }
            catch (Exception ex) { throw new InvalidDataException("引用校验无法读取 " + path + "：" + ex.Message, ex); }
        }
        var sources = final.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => (kv.Key, Table: Decode(kv.Key, kv.Value))).ToList();
        var parentPacks = new List<PackArchive>();
        try
        {
            foreach (var dependency in pack.Dependencies)
            {
                var name = Path.GetFileName(dependency.Replace('\\', '/'));
                var path = new[] { Path.Combine(Path.GetDirectoryName(pack.FilePath)!, name), Path.Combine(Path.GetDirectoryName(vanilla.FilePath)!, name) }
                    .FirstOrDefault(File.Exists);
                if (path is not null && !path.Equals(pack.FilePath, StringComparison.OrdinalIgnoreCase)) parentPacks.Add(PackArchive.Open(path));
            }
            var cache = new Dictionary<string, HashSet<string>?>(StringComparer.OrdinalIgnoreCase);
            HashSet<string>? Keys(string table, string column)
            {
                table = table.EndsWith("_tables", StringComparison.OrdinalIgnoreCase) ? table : table + "_tables";
                var identity = table + "\u0001" + column;
                if (cache.TryGetValue(identity, out var known)) return known;
                var rowsByKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var found = false;
                void Collect(DbTable data)
                {
                    var at = data.Definition.Fields.FindIndex(f => f.Name.Equals(column, StringComparison.OrdinalIgnoreCase));
                    if (at < 0) throw new InvalidDataException($"引用目标 {table} 缺少列 {column}。");
                    found = true;
                    var names = table.ToLowerInvariant() switch
                    {
                        "units_to_groupings_military_permissions_tables" => new[] { "unit", "military_group" },
                        "units_to_exclusive_faction_permissions_tables" => ["key", "faction"],
                        "resource_cost_pooled_resource_junctions_tables" => ["pooled_resource_factor", "resource_cost"],
                        "unit_variants_tables" => ["faction", "unit"],
                        _ => data.Definition.Fields.Where(f => f.IsKey).Select(f => f.Name).ToArray()
                    };
                    var keys = names.Select(n => data.Definition.Fields.FindIndex(f => f.Name.Equals(n, StringComparison.OrdinalIgnoreCase))).ToArray();
                    if (keys.Any(i => i < 0)) throw new InvalidDataException("引用目标组合键列缺失：" + table);
                    foreach (var row in data.RawRows)
                    {
                        var id = keys.Length > 0 ? string.Join('\u0001', keys.Select(k => row[k].ToTsv())) : Guid.NewGuid().ToString();
                        rowsByKey[id] = row[at].ToTsv();
                    }
                }
                foreach (var source in new[] { vanilla }.Concat(parentPacks))
                    foreach (var entry in TableFiles.EntriesFor(source, table))
                        Collect(Decode(source.FilePath + "|" + entry.Path, () => source.ReadDecoded(entry)));
                foreach (var item in sources.Where(s => TableName(s.Key).Equals(table, StringComparison.OrdinalIgnoreCase))) Collect(item.Table);
                if (!found)
                {
                    // 这些是真实 AK 码表（如 campaign_feature_ids），缺失时不能随便当作合法值放行。
                    var raw = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(vanilla.FilePath))!, "assembly_kit", "raw_data", "db", table[..^7] + ".xml");
                    if (File.Exists(raw))
                    {
                        var document = XDocument.Load(raw);
                        var records = document.Root!.Elements().Where(e => e.Name.LocalName == table[..^7]).ToList();
                        if (records.Any(r => r.Elements().Any(e => e.Name.LocalName == column)))
                        {
                            found = true;
                            foreach (var record in records)
                            {
                                var value = record.Elements().FirstOrDefault(e => e.Name.LocalName == column)?.Value;
                                if (value is not null) rowsByKey[Guid.NewGuid().ToString()] = value;
                            }
                        }
                    }
                }
                return cache[identity] = found ? new(rowsByKey.Values, StringComparer.OrdinalIgnoreCase) : null;
            }
            var issues = new List<RefIssue>();
            var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var count = 0;
            foreach (var (path, table) in sources)
                for (var f = 0; f < table.Definition.Fields.Count; f++)
                {
                    var field = table.Definition.Fields[f];
                    if (field.IsReference is not { } target) continue;
                    if (table.TableName.Equals("unit_to_unit_group_junctions_tables", StringComparison.OrdinalIgnoreCase)
                        && field.Name.Equals("unit_group", StringComparison.OrdinalIgnoreCase)
                        && table.RawRows.Any(r => r[f].ToTsv().Length == 0))
                        issues.Add(new RefIssue(path, field.Name, "（空组 key）", target.Table, target.Column));
                    var values = table.RawRows.Select(r => r[f].ToTsv()).Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    if (values.Length == 0) continue;
                    var keys = Keys(target.Table, target.Column);
                    if (keys is null) { missing.Add(target.Table + "." + target.Column); continue; }
                    foreach (var value in values)
                    {
                        count++;
                        if (!keys.Contains(value)) issues.Add(new RefIssue(path, field.Name, value, target.Table, target.Column));
                    }
                }
            return new RefReport(count, issues, missing.ToList());
        }
        finally { foreach (var parent in parentPacks) parent.Dispose(); }
    }

    private static string Norm(string path) => path.Replace('\\', '/');
    private static bool IsDb(string path) => Norm(path).StartsWith("db/", StringComparison.OrdinalIgnoreCase) && Norm(path).Split('/').Length >= 3;
    private static string TableName(string path) => Norm(path[(path.LastIndexOf('|') + 1)..]).Split('/')[1];
}
