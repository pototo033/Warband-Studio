using WarbandStudio.Packfile;

namespace WarbandStudio.Pack;

public static partial class WarbandAmender
{
    private static HashSet<string> CurrentRouteKeys(PackArchive pack, Schema schema, Dictionary<string, byte[]> replacements)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in TablePaths(pack, TabFileNaming.Routes, replacements))
        {
            if (CurrentBytes(pack, replacements, path) is not { } bytes) continue;
            var table = DbTable.Decode(bytes, TabFileNaming.Routes, schema);
            var column = table.Definition.Fields.FindIndex(field => field.Name == "upgrade_key");
            if (column < 0) throw new InvalidDataException("升级路线表缺少 upgrade_key：" + path);
            foreach (var row in table.RawRows) keys.Add(row[column].ToTsv());
        }
        return keys;
    }

    private static int DropRouteReferences(PackArchive pack, Schema schema, Dictionary<string, byte[]> replacements,
        List<string> notes, HashSet<string> removed)
    {
        if (removed.Count == 0) return 0;
        var changed = 0;
        foreach (var name in schema.Definitions.Where(item => item.Value.Any(definition => definition.Fields.Any(field =>
                     field.IsReference is ("unit_upgrade_to_unit_groups", "upgrade_key")))).Select(item => item.Key))
            changed += RewriteDropping(pack, name, schema, replacements, notes, (table, row) =>
                table.Definition.Fields.Where(field => field.IsReference is ("unit_upgrade_to_unit_groups", "upgrade_key"))
                    .Any(field => removed.Contains(Cell(table, row, field.Name))));
        return changed;
    }
}
