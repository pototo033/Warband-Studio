using System.IO;
using System.Text.RegularExpressions;
using WarbandStudio.Pack;
using WarbandStudio.Packfile;

namespace WarbandStudio.Ui.Services;

public sealed partial class Backend
{
    private (Dictionary<string, byte[]> Bytes, WarbandEdits Snapshot) ProjectTables()
    {
        var session = _pack ?? throw new InvalidOperationException("先打开一个包。");
        var snapshot = Edits.Clone();
        snapshot.ProjectKey = session.ProjectKey ?? ProjectKey;
        var bytes = new Dictionary<string, byte[]>(snapshot.TableReplacements, StringComparer.OrdinalIgnoreCase);
        // 浏览原包不运行迁移、自愈或落表；只有已有待保存修改才需要投影。
        if (snapshot.IsEmpty) return (bytes, snapshot);
        using var vanilla = VanillaDbPackPath is { } path && File.Exists(path) ? PackArchive.Open(path) : null;
        WarbandAmender.Apply(session.Archive!, vanilla, snapshot, GetSchema(), bytes, []);
        return (bytes, snapshot);
    }
    private void CommitTables(Dictionary<string, byte[]> bytes, WarbandEdits previous)
    {
        var next = new WarbandEdits { ProjectKey = previous.ProjectKey };
        foreach (var (path, data) in bytes) next.TableReplacements[path] = data;
        next.RemoveFiles.AddRange(previous.RemoveFiles);
        next.OpenedTabs.AddRange(previous.OpenedTabs);
        next.TabRaceScopes.AddRange(previous.TabRaceScopes);
        next.TabScopes.AddRange(previous.TabScopes);
        foreach(var (tab,name) in previous.TabFileNames) next.TabFileNames[tab]=name;
        _editsByPack[Key] = next;
        InvalidateCanvas();
        RestagePendingArt();
    }
    private void RequireEditablePack(string packPath)
    {
        if (_pack?.PackPath is not { } current || !string.Equals(current, packPath, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("此表所属的包已切换，请重新打开表视图。");
        if (_pack.IsUnitPack || string.Equals(current, VanillaDbPackPath, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("原版或参考兵种包只读，请复制到可编辑工程后修改。");
    }
    public void EditTableCell(string packPath, string innerPath, int row, string column, string[] expected, string text)
    {
        Edit(() =>
        {
            RequireEditablePack(packPath);
            var (bytes, snapshot) = ProjectTables();
            using var view = PackArchive.OpenView(packPath, bytes, snapshot.RemoveFiles);
            var name = PackSession.TableNameOf(innerPath);
            var table = DbTable.Decode(view.ReadDecoded(innerPath), name, GetSchema());
            var at = table.Columns.FindIndex(c => c.Name == column);
            if (at < 0 || row < 0 || row >= table.Rows.Count || !table.Rows[row].Select(v => v.ToTsv()).SequenceEqual(expected)) throw new InvalidOperationException("表数据已经变化，请刷新表视图后重试。");
            if (expected[at] == text) return false;
            var field = table.Columns[at].Source!;
            var rawAt = table.Definition.Fields.IndexOf(field);
            var old = table.RawRows[row][rawAt].ToTsv();
            if (field.IsKey && name == "unit_upgrade_group_ui_categories_tables") throw new InvalidOperationException("页签 key 请使用画布页签的“重命名”，以同步图片和界面。");
            DbCellEditor.Set(table, row, at, text);
            var value = table.RawRows[row][rawAt].ToTsv();
            if (EffectiveKeys(table).Contains(field.Name))
            {
                if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException("主键不能为空。");
                if (name == "unit_upgrade_groups_tables" && !Regex.IsMatch(value, "^[A-Za-z0-9_]+$")) throw new ArgumentException("兵组 key 只能包含字母、数字和下划线。");
                if (EffectiveKeys(table).Length == 1) RenameKey(view, bytes, name, field.Name, old, value);
                else { EnsureUniqueComposite(view, innerPath, table, row); bytes[innerPath] = DbEncoder.Encode(table); }
            }
            else bytes[innerPath] = DbEncoder.Encode(table);
            CommitTables(bytes, snapshot);
            return true;
        });
    }
    public void RenameGroup(string oldKey, string newKey)
    {
        Edit(() =>
        {
            RequireEditablePack(_pack?.PackPath ?? "");
            newKey = newKey.Trim();
            if (!Regex.IsMatch(newKey, "^[A-Za-z0-9_]+$")) throw new ArgumentException("兵组 key 只能包含字母、数字和下划线。");
            if (oldKey == newKey) return false;
            var (bytes, snapshot) = ProjectTables();
            using var view = PackArchive.OpenView(_pack!.PackPath!, bytes, snapshot.RemoveFiles);
            RenameKey(view, bytes, "unit_upgrade_groups_tables", "unit_group", oldKey, newKey);
            CommitTables(bytes, snapshot);
            return true;
        });
    }
    private void RenameKey(PackArchive view, Dictionary<string, byte[]> bytes, string name, string column, string oldKey, string newKey)
    {
        var schema = GetSchema();
        var found = false;
        foreach (var entry in TableFiles.EntriesFor(view, name))
        {
            var table = DbTable.Decode(view.ReadDecoded(entry), name, schema);
            var at = table.Definition.Fields.FindIndex(f => f.Name == column);
            foreach (var row in table.RawRows)
            {
                var key = row[at].ToTsv();
                if (key.Equals(oldKey, StringComparison.OrdinalIgnoreCase)) found = true;
                else if (key.Equals(newKey, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("目标 key 已存在：" + newKey);
            }
        }
        if (!found) throw new InvalidDataException("找不到要重命名的 key：" + oldKey);
        var affected = new HashSet<(string Table, string Column)> { (name, column) };
        bool more;
        do
        {
            more = false;
            foreach (var (tableName, definitions) in schema.Definitions)
                foreach (var field in definitions.SelectMany(d => d.Fields))
                    if (field.IsReference is { } reference && affected.Contains((reference.Table.EndsWith("_tables") ? reference.Table : reference.Table + "_tables", reference.Column)))
                        more |= affected.Add((tableName, field.Name));
        } while (more);
        foreach (var entry in view.VisibleEntries.Where(e => e.Path.StartsWith("db/", StringComparison.OrdinalIgnoreCase)))
        {
            var tableName = PackSession.TableNameOf(entry.Path);
            bool Relevant(SchemaField f) => affected.Contains((tableName, f.Name));
            if (!schema.DefinitionsFor(tableName).Any(d => d.Fields.Any(Relevant))) continue;
            // 相关表解码失败则终止整个事务，避免留下半套引用。
            var table = DbTable.Decode(view.ReadDecoded(entry), tableName, schema);
            var columns = table.Definition.Fields.Select((f, i) => (f, i)).Where(x => Relevant(x.f)).Select(x => x.i).ToArray();
            var changed = false;
            for (var r = 0; r < table.RawRows.Count; r++)
                foreach (var at in columns)
                    if (table.RawRows[r][at].ToTsv().Equals(oldKey, StringComparison.OrdinalIgnoreCase))
                    { DbCellEditor.Set(table, r, table.Columns.FindIndex(c => c.Source == table.Definition.Fields[at]), newKey); changed = true; }
            if (changed) bytes[entry.Path] = DbEncoder.Encode(table);
        }
    }
    private void EnsureUniqueComposite(PackArchive view, string path, DbTable changed, int editedRow)
    {
        var keys = EffectiveKeys(changed);
        var wanted = keys.Select(k => changed.RawRows[editedRow][changed.Definition.Fields.FindIndex(f => f.Name == k)].ToTsv()).ToArray();
        foreach (var entry in TableFiles.EntriesFor(view, changed.TableName))
        {
            var table = entry.Path.Equals(path, StringComparison.OrdinalIgnoreCase) ? changed : DbTable.Decode(view.ReadDecoded(entry), changed.TableName, GetSchema());
            var indices = keys.Select(k => table.Definition.Fields.FindIndex(f => f.Name == k)).ToArray();
            for (var r = 0; r < table.RawRows.Count; r++)
                if (!(ReferenceEquals(table, changed) && r == editedRow) && indices.Select(i => table.RawRows[r][i].ToTsv()).SequenceEqual(wanted, StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("修改后会产生重复组合键。");
        }
    }
    private static string[] EffectiveKeys(DbTable table) => table.TableName switch
    {
        "units_to_groupings_military_permissions_tables" => ["unit", "military_group"],
        "units_to_exclusive_faction_permissions_tables" => ["key", "faction"],
        "resource_cost_pooled_resource_junctions_tables" => ["pooled_resource_factor", "resource_cost"],
        "unit_variants_tables" => ["faction", "unit"],
        _ => table.Definition.Fields.Where(f => f.IsKey).Select(f => f.Name).ToArray(),
    };
}
