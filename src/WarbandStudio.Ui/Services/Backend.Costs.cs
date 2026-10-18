using System.IO;
using WarbandStudio.Pack;
using WarbandStudio.Packfile;

namespace WarbandStudio.Ui.Services;

public sealed partial class Backend
{
    public sealed record PooledResource(string Key, string Name, string IconUrl, string IconFile);
    public sealed record CostResource(string PoolId, string Resource, long Amount, string Context, string UiPooled, string IconFile)
    {
        public string Label => $"{Resource}：{Amount}（池 {PoolId}）";
    }
    public sealed record WarbandCost(string Id, long Gold, string Resource, long ResourceAmount, string PoolId, string ResourceIcon)
    {
        public IReadOnlyList<CostResource> Resources { get; init; } = [];
        public string ResourceSummary => string.Join("；", Resources.Select(r => r.Label));
    }

    public List<PooledResource> PooledResources()
    {
        using var iconBatch = Icons.BeginBatch();
        var table = CostTable("pooled_resources_tables");
        if (_pack?.Archive is { } active) PrepareIcons(active);
        if (_resourceListCache is not null && _resourceListRevision == _rev) return _resourceListCache.ToList();
        var result = new Dictionary<string, PooledResource>(StringComparer.OrdinalIgnoreCase);
        if (table is not null)
            for (var i = 0; i < table.Rows.Count; i++)
            {
                var key = CostCell(table, i, "key");
                if (key.Length == 0) continue;
                var name = Loc.Text(CostCell(table, i, "display_name")) ?? key;
                var icon = CostCell(table, i, "optional_icon_path");
                var iconKey = "res_" + IconResolver.Stem(icon);
                result[key] = new(key, name, icon.Length > 0 ? Icons.UrlByPngName(icon, iconKey) ?? "" : "",
                    icon.Length > 0 ? Icons.IconFileFor(icon, iconKey) ?? "" : "");
            }
        var factors = CostTable("pooled_resource_factor_junctions_tables");
        if (factors is not null)
            for (var i = 0; i < factors.Rows.Count; i++)
            {
                var key = CostCell(factors, i, "resource");
                if (key.Length > 0 && !result.ContainsKey(key)) result[key] = new(key, key, "", "");
            }
        foreach (var factor in Edits.PoolFactors)
            if (!result.ContainsKey(factor.Resource)) result[factor.Resource] = new(factor.Resource, factor.Resource, "", "");
        _resourceListRevision = _rev;
        _resourceListCache = result.Values.OrderBy(r => r.Name, StringComparer.CurrentCulture).ToList();
        return _resourceListCache.ToList();
    }

    public List<WarbandCost> CostList()
    {
        using var iconBatch = Icons.BeginBatch();
        var costs = CostTable("resource_costs_tables", modOnly: true);
        if (_pack?.Archive is { } active) PrepareIcons(active);
        if (_costListCache is not null && _costListRevision == _rev) return _costListCache.ToList();
        var golds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        if (costs is not null)
            for (var i = 0; i < costs.Rows.Count; i++) golds[CostCell(costs, i, "id")] = CostNumber(costs, i, "treasury_cost");
        foreach (var removed in Edits.RemoveCost) golds.Remove(removed);
        foreach (var added in Edits.AddCost) golds[added.Key] = (long)added.Value;
        var links = new Dictionary<string, (string Pool, string Cost, long Amount, string Context, string Ui)>(StringComparer.OrdinalIgnoreCase);
        var table = CostTable("resource_cost_pooled_resource_junctions_tables", ["pooled_resource_factor", "resource_cost"]);
        if (table is not null)
            for (var i = 0; i < table.Rows.Count; i++)
            {
                var pool = CostCell(table, i, "pooled_resource_factor");
                var cost = CostCell(table, i, "resource_cost");
                links[pool + "\u0001" + cost] = (pool, cost, CostNumber(table, i, "amount"), CostCell(table, i, "context"), CostCell(table, i, "ui_resource_transaction_pooled_resource"));
            }
        foreach (var key in links.Keys.ToArray())
            if (Edits.RemovePoolCost.Contains(links[key].Cost, StringComparer.OrdinalIgnoreCase)) links.Remove(key);
        foreach (var removed in Edits.RemovePoolLinks) links.Remove(removed.PoolFactor + "\u0001" + removed.ResourceCost);
        foreach (var added in Edits.PoolCosts) links[added.PoolFactor + "\u0001" + added.ResourceCost] = (added.PoolFactor, added.ResourceCost, added.Amount, added.Context, added.UiPooled);
        var list = new List<WarbandCost>();
        foreach (var (id, gold) in golds)
        {
            var resources = links.Values.Where(l => l.Cost.Equals(id, StringComparison.OrdinalIgnoreCase)).Select(l =>
            {
                var resource = ResourceOfPool(l.Pool);
                return new CostResource(l.Pool, resource, l.Amount, l.Context, l.Ui, ResourceIconFile(resource));
            }).OrderBy(r => r.PoolId, StringComparer.OrdinalIgnoreCase).ToArray();
            var first = resources.FirstOrDefault();
            list.Add(new(id, gold, first?.Resource ?? "", first?.Amount ?? 0, first?.PoolId ?? "", first?.IconFile ?? "") { Resources = resources });
        }
        _costListRevision = _rev;
        _costListCache = list.OrderBy(c => c.Id, StringComparer.OrdinalIgnoreCase).ToList();
        return _costListCache.ToList();
    }

    private static bool ValidCostId(string id) => id.Length > 0 && id.All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-');
    private void SetCostGold(string id, double gold)
    {
        Edits.RemoveCost.RemoveAll(x => x.Equals(id, StringComparison.OrdinalIgnoreCase));
        Edits.AddCost[id] = gold;
    }
    private string EnsureCostPool(string resource, string? reuse)
    {
        var pool = string.IsNullOrWhiteSpace(reuse) ? resource + "_warband_upgrade" : reuse;
        var table = CostTable("pooled_resource_factor_junctions_tables");
        var present = table is not null && Enumerable.Range(0, table.Rows.Count).Any(i => CostCell(table, i, "unique_id").Equals(pool, StringComparison.OrdinalIgnoreCase));
        if (!present && !Edits.PoolFactors.Any(f => f.UniqueId.Equals(pool, StringComparison.OrdinalIgnoreCase)))
            Edits.PoolFactors.Add((pool, "other", resource, -2147483647L, 2147483647L, "", 0));
        return pool;
    }
    private (string Context, string Ui) PoolJunctionProto()
    {
        var table = CostTable("resource_cost_pooled_resource_junctions_tables", ["pooled_resource_factor", "resource_cost"], modOnly: true);
        return table is not null && table.Rows.Count > 0
            ? (CostCell(table, 0, "context"), CostCell(table, 0, "ui_resource_transaction_pooled_resource"))
            : ("absolute", "default");
    }

    public string CreateWarbandCost(string costId, double gold, string? resourceKey, long resourceAmount)
    {
        costId = costId.Trim();
        if (!ValidCostId(costId)) return "成本 id 只能用字母/数字/下划线/短横线，且不能为空";
        if (_pack?.Archive is null) return "先打开一个包";
        if (CostList().Any(c => c.Id.Equals(costId, StringComparison.OrdinalIgnoreCase))) return $"已经有成本 id「{costId}」了";
        SetCostGold(costId, gold);
        if (!string.IsNullOrWhiteSpace(resourceKey))
        {
            var pool = EnsureCostPool(resourceKey, null);
            var proto = PoolJunctionProto();
            Edits.SetPoolCost(pool, costId, resourceAmount, proto.Context, proto.Ui);
        }
        InvalidateCanvas();
        return $"成本工坊：已创建 {costId}（金币 {gold}）　待导出";
    }

    public string UpdateWarbandCost(string costId, double gold, string? resourceKey, long resourceAmount, bool resourceChanged,
        string? reusePoolId = null, string? replacePoolId = null, bool addResource = false)
    {
        costId = costId.Trim();
        if (costId.Length == 0) return "成本 id 不能为空";
        if (_pack?.Archive is null) return "先打开一个包";
        var hit = CostList().FirstOrDefault(c => c.Id.Equals(costId, StringComparison.OrdinalIgnoreCase));
        if (hit is null) return "没有这条成本，请先创建";
        var selectedPool = replacePoolId ?? reusePoolId;
        if (resourceChanged && !addResource && selectedPool is null)
        {
            if (hit.Resources.Count > 1) return "这条成本有多个资源，请先选择要修改的资源关联";
            selectedPool = hit.Resources.FirstOrDefault()?.PoolId;
        }
        var previous = hit.Resources.FirstOrDefault(r => r.PoolId.Equals(selectedPool, StringComparison.OrdinalIgnoreCase));
        if (resourceChanged)
        {
            if (!addResource && previous is not null) Edits.RemovePoolLink(previous.PoolId, costId);
            if (!string.IsNullOrWhiteSpace(resourceKey))
            {
                var pool = EnsureCostPool(resourceKey, reusePoolId);
                var proto = PoolJunctionProto();
                Edits.SetPoolCost(pool, costId, resourceAmount, previous?.Context ?? proto.Context, previous?.UiPooled ?? proto.Ui);
            }
        }
        SetCostGold(costId, gold);
        InvalidateCanvas();
        return $"成本工坊（修改）：{costId}，金币 {gold}；只修改选定资源，其余关联保留　待导出";
    }

    public string RemoveWarbandCostResource(string costId, string poolId)
    {
        if (_pack?.Archive is null) return "先打开一个包";
        Edits.RemovePoolLink(poolId, costId);
        InvalidateCanvas();
        return $"成本工坊：已移除 {costId} 的资源池 {poolId}，其他资源保留　待导出";
    }

    public string DeleteWarbandCost(string costId)
    {
        if (_pack?.Archive is null) return "先打开一个包";
        var references = CostReferences(costId);
        if (references.Count > 0) return "无法删除：该成本仍被使用：" + string.Join("；", references.Take(12));
        Edits.AddCost.Remove(costId);
        Edits.PoolCosts.RemoveAll(x => x.ResourceCost.Equals(costId, StringComparison.OrdinalIgnoreCase));
        if (!Edits.RemoveCost.Contains(costId, StringComparer.OrdinalIgnoreCase)) Edits.RemoveCost.Add(costId);
        if (!Edits.RemovePoolCost.Contains(costId, StringComparer.OrdinalIgnoreCase)) Edits.RemovePoolCost.Add(costId);
        InvalidateCanvas();
        return $"成本工坊（删除）：{costId}（成本行及资源关联移除，资源池保留）　待导出";
    }

    public string RenameWarbandCost(string oldId, string newId)
    {
        oldId = oldId.Trim(); newId = newId.Trim();
        if (!ValidCostId(newId)) return "新成本 id 无效";
        if (oldId.Equals(newId, StringComparison.Ordinal)) return "新旧 id 完全一样";
        if (_pack?.Archive is null) return "先打开一个包";
        var hit = CostList().FirstOrDefault(c => c.Id.Equals(oldId, StringComparison.OrdinalIgnoreCase));
        if (hit is null) return "没有这条成本";
        if (!oldId.Equals(newId, StringComparison.OrdinalIgnoreCase) && CostList().Any(c => c.Id.Equals(newId, StringComparison.OrdinalIgnoreCase))) return "新成本 id 已存在";
        var otherReferences = CostReferences(oldId).Where(r => !r.StartsWith("unit_upgrade_to_unit_groups_tables:", StringComparison.Ordinal)).ToArray();
        if (otherReferences.Length > 0) return "无法重命名：还被其他消费表引用，请先调整引用：" + string.Join("；", otherReferences.Take(12));
        var routes = CostTable("unit_upgrade_to_unit_groups_tables", modOnly: true);
        if (routes is not null)
            for (var i = 0; i < routes.Rows.Count; i++)
            {
                var key = CostCell(routes, i, "upgrade_key");
                if (Edits.RemoveRoute.Contains(key, StringComparer.OrdinalIgnoreCase) || Edits.AddRoute.Any(r => r.UpgradeKey.Equals(key, StringComparison.OrdinalIgnoreCase))) continue;
                if (!CostCell(routes, i, "resource_cost").Equals(oldId, StringComparison.OrdinalIgnoreCase)) continue;
                Edits.SetRoute(new WarbandEdits.RouteEdit(key, CostCell(routes, i, "base_unit_group"), CostCell(routes, i, "target_unit_group"), newId,
                    (int)CostNumber(routes, i, "required_rank"), (int)CostNumber(routes, i, "subtracted_rank")), true);
            }
        for (var i = 0; i < Edits.AddRoute.Count; i++)
            if (Edits.AddRoute[i].Cost.Equals(oldId, StringComparison.OrdinalIgnoreCase)) Edits.AddRoute[i] = Edits.AddRoute[i] with { Cost = newId };
        Edits.AddCost.Remove(oldId);
        if (!Edits.RemoveCost.Contains(oldId, StringComparer.OrdinalIgnoreCase)) Edits.RemoveCost.Add(oldId);
        if (!Edits.RemovePoolCost.Contains(oldId, StringComparer.OrdinalIgnoreCase)) Edits.RemovePoolCost.Add(oldId);
        Edits.PoolCosts.RemoveAll(x => x.ResourceCost.Equals(oldId, StringComparison.OrdinalIgnoreCase));
        Edits.AddCost[newId] = hit.Gold;
        foreach (var r in hit.Resources) Edits.SetPoolCost(r.PoolId, newId, r.Amount, r.Context, r.UiPooled);
        InvalidateCanvas();
        return $"成本工坊（重命名）：{oldId} → {newId}（全部资源及升级路线引用一起迁移）　待导出";
    }
}
