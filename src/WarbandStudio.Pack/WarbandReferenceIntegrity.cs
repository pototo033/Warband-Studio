using WarbandStudio.Packfile;
using WarbandStudio.Rpfm;

namespace WarbandStudio.Pack;

/// <summary>RPFM 的原版引用缓存无法排除被同路径覆盖删除的行；在真实有效文件上补查战帮结构引用。</summary>
public static class WarbandReferenceIntegrity
{
    private static bool GraphReference(SchemaField field) => field.IsReference is
        ("unit_upgrade_groups", "unit_group") or ("unit_upgrade_to_unit_groups", "upgrade_key");

    public static RpfmDiagnosticReport Validate(PackArchive pack, PackArchive vanilla, Schema schema,
        IEnumerable<PackArchive>? dependencies = null)
    {
        var relevant = schema.Definitions.Where(item => item.Value.Any(definition => definition.Fields.Any(GraphReference)))
            .Select(item => item.Key).Append(TabFileNaming.Groups).Append(TabFileNaming.Routes).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = new Dictionary<string, (PackArchive Pack, PackEntry Entry)>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in new[] { vanilla }.Concat(dependencies ?? []).Append(pack))
            foreach (var entry in source.VisibleEntries.Where(entry => entry.Path.StartsWith("db/", StringComparison.OrdinalIgnoreCase)
                         && relevant.Contains(PackSession.TableNameOf(entry.Path))))
                files[entry.Path] = (source, entry); // 整张同路径文件接管，包括合法的零行表。
        var tables = files.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase).Select(item =>
            (Path: item.Key, Source: item.Value.Pack.FilePath, Table: DbTable.Decode(item.Value.Pack.ReadDecoded(item.Value.Entry),
                PackSession.TableNameOf(item.Key), schema))).ToArray();
        HashSet<string> Keys(string name, string column) => tables.Where(item => item.Table.TableName == name)
            .SelectMany(item => item.Table.RawRows.Select(row => row[item.Table.Definition.Fields.FindIndex(field => field.Name == column)].ToTsv()))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var groups = Keys(TabFileNaming.Groups, "unit_group");
        var routes = Keys(TabFileNaming.Routes, "upgrade_key");
        var issues = new List<RpfmDiagnostic>();
        foreach (var item in tables)
            for (var column = 0; column < item.Table.Definition.Fields.Count; column++)
            {
                var field = item.Table.Definition.Fields[column];
                if (!GraphReference(field)) continue;
                var valid = field.IsReference!.Value.Table == "unit_upgrade_groups" ? groups : routes;
                var displayColumn = item.Table.Columns.FindIndex(candidate => candidate.Name == field.Name);
                for (var rowIndex = 0; rowIndex < item.Table.RawRows.Count; rowIndex++)
                {
                    var row = item.Table.RawRows[rowIndex];
                    var value = row[column].ToTsv();
                    if (value.Length == 0 || valid.Contains(value)) continue;
                    var keys = item.Table.Definition.Fields.Select((candidate, index) => (candidate, index))
                        .Where(candidate => candidate.candidate.IsKey || candidate.candidate.Name == "unit")
                        .Select(candidate => candidate.candidate.Name + "=" + row[candidate.index].ToTsv());
                    issues.Add(new(DiagnosticSeverity.Error, item.Path, "InvalidReference", "最终有效战帮数据缺少 " + field.IsReference.Value.Table + "." + field.IsReference.Value.Column)
                    {
                        Field = field.Name, Value = value, Cells = [(rowIndex, displayColumn)],
                        RowContext = "最终覆盖校验；来源包=" + Path.GetFileName(item.Source) + "；" + string.Join(", ", keys)
                    });
                }
            }
        return new(issues);
    }

    public static RpfmDiagnosticReport Combine(RpfmDiagnosticReport report, string staged, string vanillaPath, Schema schema,
        IEnumerable<string>? references = null)
    {
        using var pack = PackArchive.Open(staged);
        using var vanilla = PackArchive.Open(vanillaPath);
        var dependencies = new List<PackArchive>();
        try
        {
            var paths = (references ?? []).Concat(pack.Dependencies.Select(dependency =>
            {
                var name = Path.GetFileName(dependency.Replace('\\', '/'));
                return new[] { Path.Combine(Path.GetDirectoryName(staged)!, name), Path.Combine(Path.GetDirectoryName(vanillaPath)!, name) }
                    .FirstOrDefault(File.Exists);
            }).OfType<string>()).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths)
                if (!path.Equals(Path.GetFullPath(staged), StringComparison.OrdinalIgnoreCase)
                    && !path.Equals(Path.GetFullPath(vanillaPath), StringComparison.OrdinalIgnoreCase)) dependencies.Add(PackArchive.Open(path));
            var issues = Validate(pack, vanilla, schema, dependencies);
            var items = report.Items.ToList();
            foreach (var issue in issues.Items)
                if (!items.Any(existing => existing.Path.Equals(issue.Path, StringComparison.OrdinalIgnoreCase)
                    && existing.Type == issue.Type && existing.Field == issue.Field && existing.Value == issue.Value
                    && issue.Cells.All(cell => existing.Cells.Contains(cell)))) items.Add(issue);
            return new(items);
        }
        finally { foreach (var dependency in dependencies) dependency.Dispose(); }
    }
}
