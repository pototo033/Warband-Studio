using System.IO;
using System.Text.Json.Nodes;
using WarbandStudio.Pack;
using WarbandStudio.Packfile;

namespace WarbandStudio.Ui.Services;

public sealed partial class Backend
{
    // 归属按军事组和派系专属许可两条渠道判定；建筑连接只作为招募入口数据。
    private static Dictionary<string, HashSet<string>> ResolveMilitaryPermissions(DbTable? table, WarbandEdits edits)
    {
        var uc = table is null ? -1 : Col(table, "unit");
        var gc = table is null ? -1 : Col(table, "military_group");
        var rows = table is null || uc < 0 || gc < 0 ? Enumerable.Empty<(string Unit, string Group)>()
            : table.Rows.Select(row => (row[uc].ToTsv(), row[gc].ToTsv()));
        return MilitaryPermissions.Resolve(rows, edits.RemoveUnitGroup.ToArray(), edits.AddUnitGroup.ToArray());
    }

    private List<string> SelectedMilitaryGroups(string? race, string? faction)
    {
        var (byFaction, byRace) = MilGroups();
        if (faction?.StartsWith("generic:", StringComparison.OrdinalIgnoreCase) == true)
        {
            race = faction[8..]; // 兼容旧入口，全部并入种族。
            faction = null;
        }
        if (!string.IsNullOrWhiteSpace(faction))
            return byFaction.TryGetValue(faction, out var info) && info.Group.Length > 0 ? [info.Group] : [];
        return !string.IsNullOrWhiteSpace(race) && byRace.TryGetValue(race, out var groups) ? groups.ToList() : [];
    }

    private Dictionary<string, HashSet<string>> ReadVanillaMilitaryPermissions()
    {
        var path = VanillaDbPackPath;
        if (path is null || !File.Exists(path)) return new(StringComparer.OrdinalIgnoreCase);
        using var vanilla = PackArchive.Open(path);
        var table = TableFiles.ReadComposite(vanilla, "units_to_groupings_military_permissions_tables", ["unit", "military_group"], GetSchema());
        return ResolveMilitaryPermissions(table, new WarbandEdits());
    }

    private static Dictionary<string, HashSet<string>> ResolveExclusivePermissions(DbTable? table, WarbandEdits? edits = null)
    {
        var uc = table is null ? -1 : Col(table, "key");
        var fc = table is null ? -1 : Col(table, "faction");
        var ac = table is null ? -1 : Col(table, "allowed");
        var rows = table is null || uc < 0 || fc < 0 || ac < 0
            ? Enumerable.Empty<(string Unit, string Faction, bool Allowed)>()
            : table.Rows.Select(row => (row[uc].ToTsv(), row[fc].ToTsv(),
                row[ac].ToTsv().Equals("true", StringComparison.OrdinalIgnoreCase) || row[ac].ToTsv() == "1"));
        return FactionPermissions.ResolvePositive(rows, edits?.RemoveExclusive.ToArray(), edits?.AddExclusive.ToArray());
    }

    private Dictionary<string, HashSet<string>> ReadVanillaExclusivePermissions()
    {
        var path = VanillaDbPackPath;
        if (path is null || !File.Exists(path)) return new(StringComparer.OrdinalIgnoreCase);
        using var vanilla = PackArchive.Open(path);
        return ResolveExclusivePermissions(TableFiles.ReadComposite(vanilla,
            "units_to_exclusive_faction_permissions_tables", ["key", "faction"], GetSchema()));
    }

    private JsonObject BuildMilitaryAssignmentData()
    {
        var result = new JsonObject { ["known"] = false };
        try
        {
            var pack = _pack?.Archive ?? throw new InvalidOperationException("没有打开的包");
            var schema = GetSchema();
            var path = VanillaDbPackPath;
            using var vanilla = path is not null && File.Exists(path) ? PackArchive.Open(path) : null;
            string[] keys = ["unit", "military_group"];
            var table = TableFiles.MergeComposite(
                vanilla is null ? null : TableFiles.ReadComposite(vanilla, "units_to_groupings_military_permissions_tables", keys, schema),
                TableFiles.ReadComposite(pack, "units_to_groupings_military_permissions_tables", keys, schema), keys);
            var units = new JsonObject();
            foreach (var (unit, groups) in ResolveMilitaryPermissions(table, Edits))
                units[unit] = ToJsonArray(groups.OrderBy(group => group, StringComparer.OrdinalIgnoreCase));
            string[] exclusiveKeys = ["key", "faction"];
            var exclusiveTable = TableFiles.MergeComposite(
                vanilla is null ? null : TableFiles.ReadComposite(vanilla, "units_to_exclusive_faction_permissions_tables", exclusiveKeys, schema),
                TableFiles.ReadComposite(pack, "units_to_exclusive_faction_permissions_tables", exclusiveKeys, schema), exclusiveKeys);
            var exclusive = new JsonObject();
            foreach (var (unit, permittedFactions) in ResolveExclusivePermissions(exclusiveTable, Edits))
                exclusive[unit] = ToJsonArray(permittedFactions.OrderBy(faction => faction, StringComparer.OrdinalIgnoreCase));
            var (byFaction, byRace) = MilGroups();
            var factions = new JsonObject();
            var factionRaces = new JsonObject();
            foreach (var (faction, info) in byFaction)
            {
                factions[faction] = info.Group;
                factionRaces[faction] = info.Race;
            }
            // 专属许可不要求派系拥有军事组，种族映射也要保留这些派系。
            var factionTable = TableFiles.Merge(
                vanilla is null ? null : TableFiles.ReadMerged(vanilla, "factions_tables", schema),
                TableFiles.ReadMerged(pack, "factions_tables", schema));
            var factionKey = factionTable is null ? -1 : Col(factionTable, "key");
            var raceKey = factionTable is null ? -1 : Col(factionTable, "subculture");
            if (factionTable is not null && factionKey >= 0 && raceKey >= 0)
                foreach (var row in factionTable.Rows)
                {
                    var faction = row[factionKey].ToTsv();
                    if (faction.Length > 0)
                        factionRaces[faction] = row[raceKey].ToTsv().Replace("_pro_sc_", "_sc_", StringComparison.OrdinalIgnoreCase);
                }
            var races = new JsonObject();
            foreach (var (race, groups) in byRace)
            {
                races[race] = ToJsonArray(groups);
            }
            result["units"] = units;
            result["exclusive"] = exclusive;
            result["factionRaces"] = factionRaces;
            result["factions"] = factions;
            result["races"] = races;
            result["known"] = byFaction.Count > 0;
        }
        catch (Exception ex) { Log?.Invoke("派系归属角标数据读取失败：" + ex.Message); }
        return result;
    }

    private List<string> UnitsWithMilitaryGroups(IEnumerable<string> candidates, IEnumerable<string> groups, JsonObject? data = null)
    {
        var wanted = new HashSet<string>(groups, StringComparer.OrdinalIgnoreCase);
        var assignments = (data ?? BuildMilitaryAssignmentData())["units"] as JsonObject;
        return candidates.Where(unit => assignments?[unit] is JsonArray assigned
            && assigned.Any(group => group is not null && wanted.Contains(group.GetValue<string>())))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(unit => unit, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
