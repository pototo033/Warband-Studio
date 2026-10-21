using System.IO;
using WarbandStudio.Pack;
using WarbandStudio.Packfile;

namespace WarbandStudio.Ui.Services;

public sealed partial class Backend
{
    private (DbTable? Military, DbTable? Exclusive) GrantSources()
    {
        var session = _pack ?? throw new InvalidOperationException("先打开一个包。");
        using var view = PackArchive.OpenView(session.PackPath!,Edits.TableReplacements,Edits.RemoveFiles);
        using var vanilla = VanillaDbPackPath is { } path && File.Exists(path) ? PackArchive.Open(path) : null;
        var schema = GetSchema();
        DbTable? Read(string name,string[] keys) => TableFiles.MergeComposite(
            vanilla is null ? null : TableFiles.ReadComposite(vanilla,name,keys,schema),
            TableFiles.ReadComposite(view,name,keys,schema),keys);
        return (Read("units_to_groupings_military_permissions_tables",["unit","military_group"]),
                Read("units_to_exclusive_faction_permissions_tables",["key","faction"]));
    }

    private static void NormalizeGrantScope(ref string? race,ref string? faction)
    {
        if (faction?.StartsWith("generic:",StringComparison.OrdinalIgnoreCase)==true)
        { race=faction[8..]; faction=null; }
    }

    /// <summary>Automatic placement preserves existing scoped permissions; explicit GrantUnit can broaden them.</summary>
    public int EnsureUnitPermissions(string unit,string? race,string? faction)
    {
        NormalizeGrantScope(ref race,ref faction);
        var source=GrantSources();
        var military=ResolveMilitaryPermissions(source.Military,Edits);
        var exclusive=ResolveExclusivePermissions(source.Exclusive,Edits);
        var groups=SelectedMilitaryGroups(race,faction);
        var hasMilitary=military.TryGetValue(unit,out var assigned) && groups.Any(assigned.Contains);
        var hasExclusive=exclusive.TryGetValue(unit,out var factions) &&
            (!string.IsNullOrWhiteSpace(faction) ? factions.Contains(faction) :
                !string.IsNullOrWhiteSpace(race) && MilGroups().ByFaction.Values.Any(f=>
                    f.Race.Equals(race,StringComparison.OrdinalIgnoreCase) && factions.Contains(f.Faction)));
        if (hasMilitary || hasExclusive) return 0;
        return GrantMissingPermissions(unit,race,faction,source);
    }

    private int GrantMissingPermissions(string unit,string? race,string? faction,(DbTable? Military,DbTable? Exclusive) source)
    {
        if (string.IsNullOrWhiteSpace(unit)) return 0;
        bool Queue(string target,bool exists,List<(string Unit,string Target)> additions,List<(string Unit,string Target)> removals)
        {
            bool Same((string Unit,string Target) x) => x.Unit.Equals(unit,StringComparison.OrdinalIgnoreCase) && x.Target.Equals(target,StringComparison.OrdinalIgnoreCase);
            var changed=removals.RemoveAll(Same)>0;
            if (exists) return additions.RemoveAll(Same)>0 || changed;
            if (additions.Any(Same)) return changed;
            additions.Add((unit,target)); return true;
        }
        var count=0;
        if (!string.IsNullOrWhiteSpace(faction))
        {
            var existing=ResolveExclusivePermissions(source.Exclusive);
            if (Queue(faction,existing.TryGetValue(unit,out var permitted) && permitted.Contains(faction),Edits.AddExclusive,Edits.RemoveExclusive)) count++;
        }
        else
        {
            var existing=ResolveMilitaryPermissions(source.Military,new WarbandEdits());
            foreach (var group in SelectedMilitaryGroups(race,null).Distinct(StringComparer.OrdinalIgnoreCase))
                if (Queue(group,existing.TryGetValue(unit,out var assigned) && assigned.Contains(group),Edits.AddUnitGroup,Edits.RemoveUnitGroup)) count++;
        }
        if (count>0) InvalidateCanvas();
        Log?.Invoke($"归属分配：{unit}，实际变更 {count} 项（已有授权不重复写入）。");
        return count;
    }
}
