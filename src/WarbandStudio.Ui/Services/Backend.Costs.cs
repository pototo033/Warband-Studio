using System.IO;
using WarbandStudio.Pack;
using WarbandStudio.Packfile;
using WarbandStudio.Rpfm;

namespace WarbandStudio.Ui.Services;

// 成本读写独立维护，复用原门面状态与资源链。
public sealed partial class Backend
{
    // ───────────────────────── 成本工坊 ─────────────────────────

    /// <summary>资源池里的"资源"清单（成本工坊的「选择资源」下拉）：原版打底 + 本包覆盖。</summary>
    public List<PooledResource> PooledResources()
    {
        var list = new List<PooledResource>();
        try
        {
            var schema = GetSchema();
            var notes = new List<string>();
            PackArchive? vp = null;
            var vanilla = VanillaDbPackPath;
            if (vanilla is not null && File.Exists(vanilla)) vp = PackArchive.Open(vanilla);
            try
            {
                var t = TableFiles.Merge(
                    vp is null ? null : TableFiles.ReadMerged(vp, "pooled_resources_tables", schema, notes),
                    TableFiles.ReadMerged(pack: _pack?.Archive!, "pooled_resources_tables", schema, notes));
                if (t is not null)
                {
                    var k = Col(t, "key");
                    if (k < 0) k = TableFiles.FindKeyColumn(t);
                    var dn = Col(t, "display_name");
                    var ic = Col(t, "optional_icon_path");
                    foreach (var r in t.Rows)
                    {
                        var key = k >= 0 ? r[k].ToTsv() : "";
                        if (key.Length == 0) continue;
                        var name = dn >= 0 ? (Loc.Text(r[dn].ToTsv()) ?? "") : "";
                        var icon = ic >= 0 ? r[ic].ToTsv() : "";
                        var iconKey = "res_" + IconResolver.Stem(icon);
                        list.Add(new PooledResource(key,
                            name.Length > 0 ? name : key,
                            icon.Length > 0 ? (Icons.UrlByPngName(icon, iconKey) ?? "") : "",
                            icon.Length > 0 ? (Icons.IconFileFor(icon, iconKey) ?? "") : ""));
                    }
                }
            }
            finally { vp?.Dispose(); }
        }
        catch (Exception ex) { Log?.Invoke("资源清单跳过：" + ex.Message); }
        // 包里新建的资源 / 池子引用到但资源表里暂时查不到的资源，也补进下拉（"保持可读取"）
        try
        {
            var t2 = TableFiles.ReadMerged(_pack?.Archive!, "pooled_resource_factor_junctions_tables", GetSchema(), null);
            if (t2 is not null)
            {
                var rs = Col(t2, "resource");
                if (rs >= 0)
                    foreach (var r in t2.Rows)
                    {
                        var key = r[rs].ToTsv();
                        if (key.Length == 0 || list.Any(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase))) continue;
                        list.Add(new PooledResource(key, key, "", ""));
                    }
            }
        }
        catch { }
        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCulture));
        return list;
    }

    /// <summary>成本工坊的资源项（key / 显示名 / 图标 url）。</summary>
    public sealed record PooledResource(string Key, string Name, string IconUrl, string IconFile);

    /// <summary>已有成本（成本工坊的"选择修改"下拉）：id + 金币 +（有的话）额外资源、数量与**图标**。</summary>
    public sealed record WarbandCost(string Id, long Gold, string Resource, long ResourceAmount, string PoolId, string ResourceIcon);

    /// <summary>
    /// 已有成本清单：**只列打开包里有的成本 id**（用户口径：下拉里只该出现"这个 mod 自己的"成本，
    /// 原版 db.pack 那两千多条只是噪音；v0.146 以前是"原版打底 + 本包覆盖"）。
    /// 资源/池的查找仍按"原版打底 + 本包覆盖"（作者的成本可能引用原版的池）。
    /// 顺带把每条成本的"额外资源"查出来，并解析它的**图标文件**（下拉里资源显示成图标 + 数量，不再显示 key）。
    /// </summary>
    public List<WarbandCost> CostList()
    {
        var list = new List<WarbandCost>();
        try
        {
            var schema = GetSchema();
            var notes = new List<string>();
            PackArchive? vp = null;
            var vanilla = VanillaDbPackPath;
            if (vanilla is not null && File.Exists(vanilla)) vp = PackArchive.Open(vanilla);
            try
            {
                // 池 id → 资源 key（工具自己建的池叫 <资源>_warband_upgrade；作者自己起的名字也能查出来）
                var poolRes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var ft = TableFiles.Merge(
                    vp is null ? null : TableFiles.ReadMerged(vp, "pooled_resource_factor_junctions_tables", schema, notes),
                    TableFiles.ReadMerged(_pack?.Archive!, "pooled_resource_factor_junctions_tables", schema, notes));
                if (ft is not null)
                {
                    var ui = Col(ft, "unique_id");
                    if (ui < 0) ui = TableFiles.FindKeyColumn(ft);
                    var rs = Col(ft, "resource");
                    if (ui >= 0 && rs >= 0)
                        foreach (var r in ft.Rows)
                        {
                            var id = r[ui].ToTsv();
                            if (id.Length > 0) poolRes[id] = r[rs].ToTsv();
                        }
                }
                // 成本 id → (池 id, 数量)
                var pools = new Dictionary<string, (string Pool, long Amt)>(StringComparer.OrdinalIgnoreCase);
                var jt = TableFiles.MergeComposite(
                    vp is null ? null : TableFiles.ReadComposite(vp, "resource_cost_pooled_resource_junctions_tables",
                        ["pooled_resource_factor", "resource_cost"], schema, notes),
                    TableFiles.ReadComposite(_pack?.Archive!, "resource_cost_pooled_resource_junctions_tables",
                        ["pooled_resource_factor", "resource_cost"], schema, notes),
                    ["pooled_resource_factor", "resource_cost"]);
                if (jt is not null)
                {
                    var pf = Col(jt, "pooled_resource_factor");
                    var pc = Col(jt, "resource_cost");
                    var pa = Col(jt, "amount");
                    if (pf >= 0 && pc >= 0)
                        foreach (var r in jt.Rows)
                        {
                            var cost = r[pc].ToTsv();
                            if (cost.Length > 0) pools[cost] = (r[pf].ToTsv(), pa >= 0 ? (long)r[pa].Num : 0);
                        }
                }
                // 资源 key → optional_icon_path（先只记路径；图标**用到才解析** —— 免得为几百种资源白抽一遍图）
                var resIconPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var rt = TableFiles.Merge(
                    vp is null ? null : TableFiles.ReadMerged(vp, "pooled_resources_tables", schema, notes),
                    TableFiles.ReadMerged(_pack?.Archive!, "pooled_resources_tables", schema, notes));
                if (rt is not null)
                {
                    var rk = Col(rt, "key");
                    if (rk < 0) rk = TableFiles.FindKeyColumn(rt);
                    var ri = Col(rt, "optional_icon_path");
                    if (rk >= 0 && ri >= 0)
                        foreach (var r in rt.Rows)
                        {
                            var key = r[rk].ToTsv();
                            var icon = r[ri].ToTsv();
                            if (key.Length > 0 && icon.Length > 0) resIconPath[key] = icon;
                        }
                }
                var iconDone = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                string IconOf(string resKey)
                {
                    if (resKey.Length == 0 || !resIconPath.TryGetValue(resKey, out var png)) return "";
                    if (iconDone.TryGetValue(resKey, out var hit)) return hit;
                    var f = Icons.IconFileFor(png, "res_" + IconResolver.Stem(png)) ?? "";
                    iconDone[resKey] = f;
                    return f;
                }
                // **只读本包**的 resource_costs_tables（原版那两千多条不再进下拉）
                var t = TableFiles.ReadMerged(_pack?.Archive!, "resource_costs_tables", schema, notes);
                if (t is not null)
                {
                    var k = Col(t, "id");
                    if (k < 0) k = TableFiles.FindKeyColumn(t);
                    var g = Col(t, "treasury_cost");
                    if (k >= 0)
                        foreach (var r in t.Rows)
                        {
                            var id = r[k].ToTsv();
                            if (id.Length == 0) continue;
                            pools.TryGetValue(id, out var pool);
                            var res = pool.Pool is { Length: > 0 } pid && poolRes.TryGetValue(pid, out var rk2) ? rk2 : "";
                            list.Add(new WarbandCost(id, g >= 0 ? (long)r[g].Num : 0, res, pool.Amt, pool.Pool ?? "",
                                                     IconOf(res)));
                        }
                }
                // —— 合并"待导出编辑"（repl 优先的口径）：新建/改过/删过的成本要**立刻**在清单里体现 ——
                // 工坊"创建完下拉里马上能看到新 key"就靠这里；以前只读包 → 新成本要保存后才出现（用户实测）。
                foreach (var id in Edits.RemoveCost)
                    list.RemoveAll(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                foreach (var kv in Edits.AddCost)
                {
                    var gold = (long)kv.Value;
                    var i = list.FindIndex(x => x.Id.Equals(kv.Key, StringComparison.OrdinalIgnoreCase));
                    if (i >= 0) list[i] = list[i] with { Gold = gold };
                    else list.Add(new WarbandCost(kv.Key, gold, "", 0, "", ""));
                }
                // 资源关联：先清"改过资源"的旧关联，再把本轮要写的关联并上（池 → 资源 也认本轮新建的池）
                foreach (var id in Edits.RemovePoolCost)
                {
                    var i = list.FindIndex(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                    if (i >= 0) list[i] = list[i] with { Resource = "", ResourceAmount = 0, PoolId = "", ResourceIcon = "" };
                }
                foreach (var (poolFactor, costId, amount, _, _) in Edits.PoolCosts)
                {
                    var i = list.FindIndex(x => x.Id.Equals(costId, StringComparison.OrdinalIgnoreCase));
                    if (i < 0) continue;
                    var res = "";
                    if (poolRes.TryGetValue(poolFactor, out var rk3)) res = rk3;
                    else
                    {
                        var pf = Edits.PoolFactors.FirstOrDefault(f => f.UniqueId.Equals(poolFactor, StringComparison.OrdinalIgnoreCase));
                        res = pf.Resource ?? "";
                    }
                    list[i] = list[i] with { Resource = res, ResourceAmount = amount, PoolId = poolFactor, ResourceIcon = IconOf(res) };
                }
            }
            finally { vp?.Dispose(); }
        }
        catch (Exception ex) { Log?.Invoke("成本清单跳过：" + ex.Message); }
        list.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    /// <summary>
    /// 重命名成本 id：**删旧 + 写新**（旧那条连着它的资源关联一起删，资源关联原样搬到新 id 下）。
    /// 保存后包里不会再留旧 id —— 用户口径："改名后保存要覆盖掉改名前的成本"。
    /// </summary>
    public string RenameWarbandCost(string oldId, string newId)
    {
        oldId = (oldId ?? "").Trim(); newId = (newId ?? "").Trim();
        if (newId.Length == 0) return "新 id 不能为空";
        if (!newId.All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-')) return "成本 id 只能用字母/数字/下划线/短横线";
        if (_pack?.Archive is null) return "先打开一个包";
        // **只按"完全一样"拦**：只改大小写（小写改大写）是合法的改名（用户实测：以前被当成"一样"拒掉）
        if (oldId.Equals(newId, StringComparison.Ordinal)) return "新旧 id 完全一样，不用改";
        // 重名检查要把"自己"排除掉（大小写不同也算同一个 key）——不然"只改大小写"会被当成撞名
        var caseOnly = oldId.Equals(newId, StringComparison.OrdinalIgnoreCase);
        if (Edits.AddCost.ContainsKey(newId) && !caseOnly) return $"已经有成本 id「{newId}」了";
        if (ResourceCostExists(newId) && !caseOnly) return $"已经有成本 id「{newId}」了";
        var gold = 0.0;
        if (Edits.AddCost.TryGetValue(oldId, out var g0))
        {
            // 本会话新建的（还没落盘）：直接改 key，关联行跟着改 id
            gold = g0;
            Edits.AddCost.Remove(oldId);
            for (var i = 0; i < Edits.PoolCosts.Count; i++)
                if (Edits.PoolCosts[i].ResourceCost.Equals(oldId, StringComparison.OrdinalIgnoreCase))
                    Edits.PoolCosts[i] = (Edits.PoolCosts[i].PoolFactor, newId, Edits.PoolCosts[i].Amount,
                                          Edits.PoolCosts[i].Context, Edits.PoolCosts[i].UiPooled);
        }
        else
        {
            var hit = CostList().FirstOrDefault(c => c.Id.Equals(oldId, StringComparison.OrdinalIgnoreCase));
            if (hit is null) return $"包里没有成本 id「{oldId}」（先从下拉里选一个）";
            gold = hit.Gold;
            if (!Edits.RemoveCost.Contains(oldId, StringComparer.OrdinalIgnoreCase)) Edits.RemoveCost.Add(oldId);
            if (!Edits.RemovePoolCost.Contains(oldId, StringComparer.OrdinalIgnoreCase)) Edits.RemovePoolCost.Add(oldId);
            if (hit.PoolId.Length > 0)
            {
                // 资源关联原样搬到新 id（池不动、数量照旧）
                var (ctx, ui) = PoolJunctionProto();
                Edits.PoolCosts.Add((hit.PoolId, newId, hit.ResourceAmount, ctx, ui));
            }
        }
        // **本会话新建的路线**（还在待导出里）也要跟着改名：它们不在包里，上面那段读包读不到 ✗
        // （用户实测：新建的升级 + 重命名成本 → 升级里还是旧成本）
        for (var i = 0; i < Edits.AddRoute.Count; i++)
            if (Edits.AddRoute[i].Cost.Equals(oldId, StringComparison.OrdinalIgnoreCase))
                Edits.AddRoute[i] = Edits.AddRoute[i] with { Cost = newId };

        // **路线表里引用旧 id 的成本要跟着改名**：不然升级会指向一个不存在的成本
        // （`beastmen_dummy` 在用户包里被 16 条路线引用 —— 改名不带它们走就会留一堆悬空引用）
        try
        {
            var sch = GetSchema();
            var notes = new List<string>();
            var routes = TableFiles.ReadConcat(_pack!.Archive!, "unit_upgrade_to_unit_groups_tables", sch, notes);
            if (routes is not null)
            {
                var rk = Col(routes, "upgrade_key");
                var rc = Col(routes, "resource_cost");
                var rr = Col(routes, "required_rank");
                var rs = Col(routes, "subtracted_rank");
                if (rk >= 0 && rc >= 0)
                    foreach (var row in routes.Rows)
                    {
                        if (!row[rc].ToTsv().Equals(oldId, StringComparison.OrdinalIgnoreCase)) continue;
                        var rkey = row[rk].ToTsv();
                        if (rkey.Length == 0) continue;
                        // **带整行**（base/target/等级照抄作者那一行）：只带成本列要靠"空=不动"的约定，
                        // 一旦约定没生效就会把两端/等级写坏（用户实测：改名后升级变成"不写成本"）
                        var rb2 = Col(routes, "base_unit_group");
                        var rt2 = Col(routes, "target_unit_group");
                        Edits.SetRoute(new WarbandEdits.RouteEdit(rkey,
                            rb2 >= 0 ? row[rb2].ToTsv() : "", rt2 >= 0 ? row[rt2].ToTsv() : "", newId,
                            rr >= 0 ? (int)row[rr].Int : 0, rs >= 0 ? (int)row[rs].Int : 0), true);
                    }
            }
        }
        catch (Exception ex) { Log?.Invoke("重命名：路线表跟随失败：" + ex.Message); }

        // **先删旧的再写新的**：`AddCost` 字典是"忽略大小写"的，不先删的话"只改大小写"会保留旧写法
        // （`dict[新写法] = 值` 命中旧 key 时只更新值、**key 还是旧的**）→ 用户看到的"改大写又变回小写"。
        Edits.AddCost.Remove(oldId);
        Edits.AddCost[newId] = gold;
        InvalidateCanvas();
        var msg = $"成本工坊（重命名）：{oldId} → {newId}（旧的那条连同资源关联一起删）　待导出";
        Log?.Invoke(msg);
        return msg;
    }

    /// <summary>
    /// 修改已有成本：金币就地改（`resource_costs_tables`）；资源那块**先把这条成本已有的关联清掉**，
    /// 再按需要写新的一条（`resourceChanged=false` 就完全不碰资源，保留作者原来的池/关联）。
    /// </summary>
    public string UpdateWarbandCost(string costId, double gold, string? resourceKey, long resourceAmount, bool resourceChanged,
                                    string? reusePoolId = null)
    {
        costId = (costId ?? "").Trim();
        if (costId.Length == 0) return "成本 id 不能为空";
        if (_pack?.Archive is null) return "先打开一个包";
        Edits.AddCost[costId] = gold;
        var parts = new List<string> { $"成本 {costId} → 金币 {gold:0}" };
        if (resourceChanged)
        {
            Edits.RemovePoolCost.Add(costId);        // 清掉这条成本原来的资源关联（改原文件）
            if (!string.IsNullOrWhiteSpace(resourceKey))
            {
                // 资源没变（只改数量）时**复用原来那个池**（作者自己起的池名别丢）；换资源才用 <资源>_warband_upgrade
                var poolId = !string.IsNullOrWhiteSpace(reusePoolId) ? reusePoolId! : resourceKey + "_warband_upgrade";
                if (!PoolFactorExists(poolId) && !Edits.PoolFactors.Any(x => x.UniqueId.Equals(poolId, StringComparison.OrdinalIgnoreCase)))
                    Edits.PoolFactors.Add((poolId, "other", resourceKey!, -2147483647L, 2147483647L, "", 0));
                var (ctx, ui) = PoolJunctionProto();
                Edits.PoolCosts.Add((poolId, costId, resourceAmount, ctx, ui));
                parts.Add($"改扣 {resourceAmount} 点 {resourceKey}");
            }
            else parts.Add("清掉了额外资源");
        }
        InvalidateCanvas();
        var msg = "成本工坊（修改）：" + string.Join(" + ", parts) + "　待导出";
        Log?.Invoke(msg);
        return msg;
    }

    /// <summary>删成本：`resource_costs_tables` 那一行 + 它的资源关联（都是改原文件）。</summary>
    public string DeleteWarbandCost(string costId)
    {
        costId = (costId ?? "").Trim();
        if (costId.Length == 0) return "先选一个成本 id";
        if (_pack?.Archive is null) return "先打开一个包";
        Edits.AddCost.Remove(costId);                 // 本轮刚建的也撤掉
        Edits.PoolCosts.RemoveAll(x => x.ResourceCost.Equals(costId, StringComparison.OrdinalIgnoreCase));
        if (!Edits.RemoveCost.Contains(costId, StringComparer.OrdinalIgnoreCase)) Edits.RemoveCost.Add(costId);
        if (!Edits.RemovePoolCost.Contains(costId, StringComparer.OrdinalIgnoreCase)) Edits.RemovePoolCost.Add(costId);
        InvalidateCanvas();
        var msg = $"成本工坊（删除）：{costId}（成本行 + 它的资源关联都删；资源池本身留着，别的成本可能还在用）　待导出";
        Log?.Invoke(msg);
        return msg;
    }

    /// <summary>
    /// 成本工坊：建一个"战帮升级成本"——三张表一起写：
    ///   ① `resource_costs_tables`：id = costId、treasury_cost = gold（**带符号**：负=消耗），
    ///      后面几列是标签（照抄包里现有成本行）；
    ///   ② `pooled_resource_factor_junctions_tables`：池 id = `&lt;资源名&gt;_warband_upgrade`（已有就复用），
    ///      factor=other、resource=资源、min/max=∓2147483647、specific_faction_set 空、sort_order=0；
    ///   ③ `resource_cost_pooled_resource_junctions_tables`：把成本条目和池关联起来，
    ///      amount = 资源量（带符号），context/ui 两列照抄包里已有行。
    /// </summary>
    public string CreateWarbandCost(string costId, double gold, string? resourceKey, long resourceAmount)
    {
        costId = (costId ?? "").Trim();
        if (costId.Length == 0) return "成本 id 不能为空";
        if (!costId.All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-')) return "成本 id 只能用字母/数字/下划线/短横线";
        if (_pack?.Archive is null) return "先打开一个包";
        if (Edits.AddCost.ContainsKey(costId) || ResourceCostExists(costId))
            return $"已经有成本 id「{costId}」了（换个名字，或直接在升级里选它）";
        Edits.AddCost[costId] = gold;
        var parts = new List<string> { $"成本 {costId}（金币 {gold:0}）" };
        if (!string.IsNullOrWhiteSpace(resourceKey))
        {
            var poolId = resourceKey + "_warband_upgrade";
            if (!PoolFactorExists(poolId) && !Edits.PoolFactors.Any(x => x.UniqueId.Equals(poolId, StringComparison.OrdinalIgnoreCase)))
            {
                Edits.PoolFactors.Add((poolId, "other", resourceKey!, -2147483647L, 2147483647L, "", 0));
                parts.Add($"新建资源池 {poolId}（factor=other）");
            }
            else parts.Add($"复用已有资源池 {poolId}");
            var (ctx, ui) = PoolJunctionProto();
            Edits.PoolCosts.Add((poolId, costId, resourceAmount, ctx, ui));
            parts.Add($"{resourceAmount} 点 {resourceKey}");
        }
        InvalidateCanvas();
        var msg = "成本工坊：" + string.Join(" + ", parts) + "　待导出";
        Log?.Invoke(msg);
        return msg;
    }

    /// <summary>包里（+本轮编辑）已经有这个成本 id 了吗。</summary>
    private bool ResourceCostExists(string id)
    {
        try
        {
            var t = TableFiles.ReadConcat(_pack?.Archive!, "resource_costs_tables", GetSchema(), null);
            if (t is null) return false;
            var c = Col(t, "id");
            if (c < 0) c = TableFiles.FindKeyColumn(t);
            if (c < 0) return false;
            foreach (var r in t.Rows)
                if (r[c].ToTsv().Equals(id, StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch { }
        return false;
    }

    /// <summary>包里已经有这个资源池 id 了吗（`pooled_resource_factor_junctions_tables.unique_id`）。</summary>
    private bool PoolFactorExists(string uniqueId)
    {
        try
        {
            var t = TableFiles.ReadConcat(_pack?.Archive!, "pooled_resource_factor_junctions_tables", GetSchema(), null);
            if (t is null) return false;
            var c = Col(t, "unique_id");
            if (c < 0) c = TableFiles.FindKeyColumn(t);
            if (c < 0) return false;
            foreach (var r in t.Rows)
                if (r[c].ToTsv().Equals(uniqueId, StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch { }
        return false;
    }

    /// <summary>成本↔资源池那张表的后两列（context / ui_resource_transaction_pooled_resource）：照抄包里第一行的写法。</summary>
    private (string Context, string UiPooled) PoolJunctionProto()
    {
        try
        {
            var t = TableFiles.ReadConcat(_pack?.Archive!, "resource_cost_pooled_resource_junctions_tables", GetSchema(), null);
            if (t is not null && t.Rows.Count > 0)
            {
                var c = Col(t, "context");
                var u = Col(t, "ui_resource_transaction_pooled_resource");
                return (c >= 0 ? t.Rows[0][c].ToTsv() : "absolute", u >= 0 ? t.Rows[0][u].ToTsv() : "default");
            }
        }
        catch { }
        return ("absolute", "default");
    }

}
