using System.IO;
using System.Text.Json;
using System.Xml.Linq;
using WarbandStudio.Pack;
using WarbandStudio.Packfile;

namespace WarbandStudio.Ui.Services;

public sealed partial class Backend
{
    private const string NativeWarbandMetadata = "warband-studio/native-warband.v1.json";
    private static readonly string[] NativeWarbandTables = [TabFileNaming.Infos, TabFileNaming.Groups,
        TabFileNaming.Junc, TabFileNaming.Links, TabFileNaming.Routes, TabFileNaming.Cats, CopyTech, CopyBuildings,
        "effect_bonus_value_unit_upgrade_to_unit_group_junctions_tables", "campaign_bonus_value_ids_unit_upgrade_to_unit_groups_tables"];
    private sealed record NativeWarbandData(string Stamp, Dictionary<string, byte[]> Files,
        Dictionary<string, string> Groups, HashSet<string> Categories, DbTable? Costs);
    private sealed record NativeWarbandPlan(Dictionary<string, byte[]> Overlay, HashSet<string> Categories);
    private NativeWarbandData? _nativeWarband;

    private NativeWarbandData? NativeWarband()
    {
        if (VanillaDbPackPath is not { } path || !File.Exists(path)) return null;
        var file = new FileInfo(path);
        var stamp = file.FullName + "|" + file.Length + "|" + file.LastWriteTimeUtc.Ticks;
        if (_nativeWarband?.Stamp == stamp) return _nativeWarband;
        using var source = PackArchive.Open(path);
        var schema = GetSchema();
        var files = source.VisibleEntries.Where(e => NativeWarbandTables.Contains(PackSession.TableNameOf(e.Path)) && e.Path.StartsWith("db/"))
            .ToDictionary(e => e.Path, e => source.ReadDecoded(e), StringComparer.OrdinalIgnoreCase);
        var groups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var infos = TableFiles.ReadMerged(source, TabFileNaming.Infos, schema);
        if (infos is not null)
            foreach (var row in infos.RawRows)
            {
                var category = PageCell(infos, row, "category");
                if (category.Length > 0) groups[PageCell(infos, row, "unit_upgrade_group")] = category;
            }
        return _nativeWarband = new(stamp, files, groups, groups.Values.ToHashSet(StringComparer.OrdinalIgnoreCase),
            TableFiles.ReadMerged(source, "resource_costs_tables", schema));
    }

    private NativeWarbandPlan NativePlan(PackArchive source, NativeWarbandData data)
    {
        var schema = GetSchema();
        var inherited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var materialized = source.Find(NativeWarbandMetadata) is not null;
        if (source.Find(NativeWarbandMetadata) is { } metadata)
        {
            using var json = JsonDocument.Parse(source.ReadDecoded(metadata));
            if (json.RootElement.GetProperty("format").GetInt32() != 1) throw new InvalidDataException("不支持的原版战帮覆盖记录。");
            foreach (var item in json.RootElement.GetProperty("inheritedCategories").EnumerateArray()) inherited.Add(item.GetString()!);
        }
        var replaced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var authored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ownInfos = TableFiles.EntriesFor(source, TabFileNaming.Infos).Select(entry =>
            (entry.Path, Table: DbTable.Decode(source.ReadDecoded(entry), TabFileNaming.Infos, schema))).ToArray();
        foreach (var (path, table) in ownInfos.Where(info => !data.Files.ContainsKey(info.Path)))
            foreach (var row in table.RawRows)
            {
                var category = PageCell(table, row, "category");
                authored.Add(PageCell(table, row, "unit_upgrade_group"));
                if (data.Categories.Contains(category) && !inherited.Contains(category)) replaced.Add(category);
            }
        if (source.VisibleEntries.FirstOrDefault(entry => entry.Path.EndsWith("warband_upgrades.twui.xml", StringComparison.OrdinalIgnoreCase)) is { } ui)
        {
            var xml = XDocument.Parse(System.Text.Encoding.UTF8.GetString(source.ReadDecoded(ui)).TrimStart('\uFEFF'));
            var declared = xml.Descendants().Where(element => element.Name.LocalName == "property" && (string?)element.Attribute("name") == "God")
                .Select(element => (string?)element.Attribute("value") ?? "").Where(value => value.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (declared.Count > 0) replaced.UnionWith(data.Categories.Except(declared, StringComparer.OrdinalIgnoreCase));
        }
        inherited = data.Categories.Except(replaced, StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var overlay = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var visible = new HashSet<string>(authored, StringComparer.OrdinalIgnoreCase);
        var relocated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // 已有同路径文件是完整覆盖；其他文件中的同页内容表示作者重做该页，不再补回原版组。
        foreach (var (path, bytes) in data.Files.Where(file => PackSession.TableNameOf(file.Key) == TabFileNaming.Infos))
        {
            var own = source.Find(path);
            var table = DbTable.Decode(own is null ? bytes : source.ReadDecoded(own), TabFileNaming.Infos, schema);
            var rows = table.RawRows.Where(row =>
            {
                var group = PageCell(table, row, "unit_upgrade_group");
                var category = PageCell(table, row, "category");
                if (own is null) return inherited.Contains(category) && !authored.Contains(group);
                return !data.Groups.TryGetValue(group, out var original) || !original.Equals(category, StringComparison.OrdinalIgnoreCase)
                    || !materialized || !replaced.Contains(category) || authored.Contains(group);
            }).ToList();
            overlay[path] = PageBytes(table, rows);
            foreach (var row in rows)
            {
                var group = PageCell(table, row, "unit_upgrade_group");
                visible.Add(group);
                if (own is not null && data.Groups.TryGetValue(group, out var originalCategory)
                    && !originalCategory.Equals(PageCell(table, row, "category"), StringComparison.OrdinalIgnoreCase)) relocated.Add(group);
            }
        }
        var routes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool Eligible(string group, bool fallback) => visible.Contains(group) && (!fallback || relocated.Contains(group)
            || !data.Groups.TryGetValue(group, out var category) || !replaced.Contains(category));
        foreach (var (path, bytes) in data.Files.Where(file => PackSession.TableNameOf(file.Key) == TabFileNaming.Routes))
        {
            var fallback = source.Find(path) is null || materialized;
            var table = DbTable.Decode(source.Find(path) is { } own ? source.ReadDecoded(own) : bytes, TabFileNaming.Routes, schema);
            var rows = table.RawRows.Where(row => Eligible(PageCell(table, row, "base_unit_group"), fallback)
                && Eligible(PageCell(table, row, "target_unit_group"), fallback)).ToList();
            overlay[path] = PageBytes(table, rows);
            foreach (var row in rows) routes.Add(PageCell(table, row, "upgrade_key"));
        }
        foreach (var (path, bytes) in data.Files.Where(file => !overlay.ContainsKey(file.Key)))
        {
            var name = PackSession.TableNameOf(path);
            var fallback = source.Find(path) is null || materialized;
            var table = DbTable.Decode(source.Find(path) is { } own ? source.ReadDecoded(own) : bytes, name, schema);
            bool Keep(DbValue[] row) => name switch
            {
                TabFileNaming.Groups => Eligible(PageCell(table, row, "unit_group"), fallback),
                TabFileNaming.Junc => Eligible(PageCell(table, row, "unit_group"), fallback),
                TabFileNaming.Links => Eligible(PageCell(table, row, "child_key"), fallback) && Eligible(PageCell(table, row, "parent_key"), fallback),
                TabFileNaming.Cats => inherited.Contains(PageCell(table, row, "category")) || replaced.Contains(PageCell(table, row, "category")),
                CopyTech or CopyBuildings => routes.Contains(PageCell(table, row, "unit_upgrade")),
                _ => table.Definition.Fields.Select((field, index) => (field, index)).All(item => item.field.IsReference switch
                {
                    ("unit_upgrade_groups", "unit_group") => Eligible(row[item.index].ToTsv(), fallback),
                    ("unit_upgrade_to_unit_groups", "upgrade_key") => routes.Contains(row[item.index].ToTsv()),
                    _ => true
                })
            };
            overlay[path] = PageBytes(table, table.RawRows.Where(Keep).ToList());
        }
        return new(overlay, inherited);
    }

    private PackArchive OpenCanvasSource(IReadOnlyDictionary<string, byte[]>? replacements = null, IEnumerable<string>? removed = null)
    {
        var path = _pack?.PackPath ?? throw new InvalidOperationException("先打开一个包。");
        var buffers = new Dictionary<string, byte[]>(replacements ?? Edits.TableReplacements, StringComparer.OrdinalIgnoreCase);
        var drops = (removed ?? Edits.RemoveFiles).ToArray();
        var restored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var source = DuplicateUpgradeArchive.OpenView(path, buffers, drops);
        if (NativeWarband() is { } native && native.Groups.Count > 0 && !_pack!.IsUnitPack && !path.Equals(VanillaDbPackPath, StringComparison.OrdinalIgnoreCase))
            foreach (var (file, bytes) in NativePlan(source, native).Overlay)
                // 已删除的覆盖文件在游戏里会回退原版，因此预览也重新提供底层文件。
            {
                buffers[file] = bytes;
                restored.Add(file);
            }
        return DuplicateUpgradeArchive.OpenView(path, buffers, drops.Where(drop => !restored.Contains(drop)));
    }

    private void EnsureNativeGroups(params string[] groups)
    {
        if (NativeWarband() is not { } data) return;
        EnsureNativeCategories(groups.Where(data.Groups.ContainsKey).Select(group => data.Groups[group]).ToArray());
    }

    private void EnsureNativeCategories(params string?[] categories)
    {
        if (_pack?.Archive is not { } source || _pack.IsUnitPack || NativeWarband() is not { } data) return;
        if (!categories.Any(category => category is not null && data.Categories.Contains(category))) return;
        var plan = NativePlan(source, data);
        if (!categories.Any(category => category is not null && plan.Categories.Contains(category))) return;
        var changed = false;
        foreach (var (path, bytes) in data.Files)
            if (source.Find(path) is null && !Edits.RemoveFiles.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                // 保留底层组定义供原版引用；成员、路线及条件只承接仍生效的页，不能把作者替换掉的路线重新提升为本包路线。
                Edits.TableReplacements[path] = PackSession.TableNameOf(path) == TabFileNaming.Groups ? bytes : plan.Overlay[path];
                changed = true;
            }
        if (!changed) return;
        // 编辑原版后记录继承关系，后续新增同页兵组不能被误当成整页替换而隐藏其余原版组。
        Edits.TableReplacements[NativeWarbandMetadata] = JsonSerializer.SerializeToUtf8Bytes(new { format = 1,
            inheritedCategories = plan.Categories.Order(StringComparer.OrdinalIgnoreCase).ToArray() });
        InvalidateCanvas();
        Log?.Invoke("编辑原版战帮：在当前 MOD 暂存原表覆盖，原版文件保持不变；保存前可撤销。");
    }
}
