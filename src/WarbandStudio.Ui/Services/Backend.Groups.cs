using WarbandStudio.Pack;
using WarbandStudio.Packfile;

namespace WarbandStudio.Ui.Services;

public sealed partial class Backend
{
    public int SetGroupsCategory(IEnumerable<(string Group, int X, int Y)> groups, string category)
    {
        var count = 0;
        foreach (var item in groups.Where(g => !string.IsNullOrWhiteSpace(g.Group)).DistinctBy(g => g.Group, StringComparer.OrdinalIgnoreCase))
        {
            var position = Edits.InfoEdits.TryGetValue(item.Group, out var current) ? (current.X, current.Y) : (item.X, item.Y);
            Edits.InfoEdits[item.Group] = (position.Item1, position.Item2, category);
            count++;
        }
        if (count > 0) InvalidateCanvas();
        Log?.Invoke($"批量改页签：{count} 个组 → {category}（单次提交，坐标保留）　待导出");
        return count;
    }

    private void ClearMergedGroupConnections(IEnumerable<string> groups)
    {
        var keys = groups.ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool Touches(string a, string b) => keys.Contains(a) || keys.Contains(b);
        var routes = Edits.AddRoute.Where(r => Touches(r.Base, r.Target)).ToList();
        var links = Edits.AddLink.Where(l => Touches(l.Child, l.Parent)).ToList();
        if (_pack?.Archive is { } pack)
        {
            var routeTable = TableFiles.ReadConcat(pack, "unit_upgrade_to_unit_groups_tables", GetSchema());
            if (routeTable is not null)
                for (var i = 0; i < routeTable.Rows.Count; i++)
                {
                    var b = CostCell(routeTable, i, "base_unit_group");
                    var t = CostCell(routeTable, i, "target_unit_group");
                    if (Touches(b, t)) routes.Add(new(CostCell(routeTable, i, "upgrade_key"), b, t, "", 0, 0));
                }
            var linkTable = TableFiles.ReadConcat(pack, "unit_upgrade_group_ui_links_tables", GetSchema());
            if (linkTable is not null)
                for (var i = 0; i < linkTable.Rows.Count; i++)
                {
                    var c = CostCell(linkTable, i, "child_key");
                    var p = CostCell(linkTable, i, "parent_key");
                    if (Touches(c, p)) links.Add(new(c, p, 0, 0, 0, 0, 0));
                }
        }
        foreach (var route in routes) Edits.SetRoute(route, false);
        foreach (var link in links) Edits.SetLinkPair(link, false);
        Log?.Invoke($"合并清理：{keys.Count} 个参与组，清除原升级 {routes.Select(r => r.UpgradeKey).Distinct(StringComparer.OrdinalIgnoreCase).Count()} 条、原连线 {links.Count} 条（不继承）");
    }
}
