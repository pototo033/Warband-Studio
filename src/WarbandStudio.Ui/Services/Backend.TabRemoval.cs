using System.IO;
using System.Text;
using WarbandStudio.Pack;
using WarbandStudio.Packfile;

namespace WarbandStudio.Ui.Services;

public sealed partial class Backend
{
    public string DeleteTab(string category) => Edit(() =>
    {
        var path = _pack?.PackPath ?? "";
        RequireEditablePack(path);
        if (string.IsNullOrWhiteSpace(category)) throw new ArgumentException("页签 key 不能为空。", nameof(category));
        var (bytes, snapshot) = ProjectTables();
        using var view = DuplicateUpgradeArchive.OpenView(path, bytes, snapshot.RemoveFiles);
        var schema = GetSchema();
        var comparison = StringComparison.OrdinalIgnoreCase;

        IEnumerable<(PackEntry Entry, DbTable Table)> Read(string table) =>
            TableFiles.EntriesFor(view, table).Select(entry => (entry, DbTable.Decode(view.ReadDecoded(entry), table, schema)));
        var groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var otherGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, table) in Read(TabFileNaming.Infos))
        {
            var tabColumn = PageColumn(table, "category");
            var groupColumn = PageColumn(table, "unit_upgrade_group");
            foreach (var row in table.RawRows)
            {
                var key = row[groupColumn].ToTsv();
                if (key.Length == 0) continue;
                (row[tabColumn].ToTsv().Equals(category, comparison) ? groups : otherGroups).Add(key);
            }
        }
        var hadGroups = groups.Count > 0;
        groups.ExceptWith(otherGroups);
        var routes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, table) in Read(TabFileNaming.Routes))
        {
            var source = PageColumn(table, "base_unit_group");
            var target = PageColumn(table, "target_unit_group");
            var key = PageColumn(table, "upgrade_key");
            foreach (var row in table.RawRows)
                if (groups.Contains(row[source].ToTsv()) || groups.Contains(row[target].ToTsv()))
                    routes.Add(row[key].ToTsv());
        }

        var twui = view.VisibleEntries.FirstOrDefault(entry => entry.Path.EndsWith("warband_upgrades.twui.xml", comparison));
        var hasHolder = false;
        if (twui is not null)
        {
            var original = Encoding.UTF8.GetString(view.ReadDecoded(twui));
            var xml = original.TrimStart('\uFEFF');
            var updated = WarbandTabRemoval.RemoveXml(xml, category);
            hasHolder = updated != xml;
            if (hasHolder) bytes[twui.Path] = Encoding.UTF8.GetBytes(original.StartsWith('\uFEFF') ? "\uFEFF" + updated : updated);
        }

        var hadCategory = false;
        foreach (var entry in view.VisibleEntries.Where(e => e.Path.StartsWith("db/", comparison)))
        {
            var tableName = PackSession.TableNameOf(entry.Path);
            Predicate<string>? RemovalTest(SchemaField field)
            {
                if ((tableName == TabFileNaming.Cats || tableName == TabFileNaming.Infos) && field.Name == "category")
                    return value => value.Equals(category, comparison);
                if (groups.Count > 0 && (tableName == TabFileNaming.Groups && field.Name == "unit_group"
                    || tableName == TabFileNaming.Links && field.Name is "child_key" or "parent_key"
                    || field.IsReference is ("unit_upgrade_groups", "unit_group")))
                    return groups.Contains;
                if (routes.Count > 0 && (tableName == TabFileNaming.Routes && field.Name == "upgrade_key"
                    || field.IsReference is ("unit_upgrade_to_unit_groups", "upgrade_key")))
                    return routes.Contains;
                return null;
            }
            if (!schema.DefinitionsFor(tableName).Any(definition => definition.Fields.Any(field => RemovalTest(field) is not null))) continue;
            var table = DbTable.Decode(view.ReadDecoded(entry), tableName, schema);
            var checks = table.Definition.Fields.Select((field, index) => (Index: index, Test: RemovalTest(field)))
                .Where(check => check.Test is not null).ToArray();
            if (checks.Length == 0) continue;
            var rows = table.RawRows.Where(row => !checks.Any(check => check.Test!(row[check.Index].ToTsv()))).ToList();
            if (rows.Count == table.RawRows.Count) continue;
            hadCategory |= tableName == TabFileNaming.Cats;
            if (rows.Count > 0) bytes[entry.Path] = PageBytes(table, rows);
            else
            {
                bytes.Remove(entry.Path);
                if (!snapshot.RemoveFiles.Contains(entry.Path, StringComparer.OrdinalIgnoreCase))
                    snapshot.RemoveFiles.Add(entry.Path);
            }
        }
        if (!hadCategory && !hadGroups && !hasHolder)
            throw new InvalidOperationException($"当前 Pack 中找不到页签「{category}」。");

        var names = TabFileBindings.Read(view);
        if (names.Remove(category)) bytes[TabFileBindings.MetadataPath] = TabFileBindings.Encode(names);
        snapshot.TabFileNames.Remove(category);
        snapshot.OpenedTabs.RemoveAll(key => key.Equals(category, comparison));
        snapshot.TabScopes.RemoveAll(scope => scope.Category.Equals(category, comparison));
        snapshot.TabRaceScopes.RemoveAll(scope => scope.Category.Equals(category, comparison));
        CommitTables(bytes, snapshot);
        return $"已删除页签「{category}」，移除 {groups.Count} 个兵组、{routes.Count} 条升级，待保存。";
    });
}
