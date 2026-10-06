namespace WarbandStudio.Pack;

/// <summary>
/// 画布上做过的编辑（**直接改 pack** 模型：先在内存里攒着，导出/保存时一次落盘）。
///
/// 每一条都对得上游戏表：
///   · AddJunction/RemoveJunction → `unit_to_unit_group_junctions_tables`（兵 ↔ 组）
///   · AddGroup/RemoveGroup      → `unit_upgrade_groups_tables`（组本身）
///   · InfoEdits                 → `unit_upgrade_group_ui_infos_tables`（x/y/category）
///   · AddRoute/RemoveRoute      → `unit_upgrade_to_unit_groups_tables`（能不能升级）
///   · AddLink/RemoveLink        → `unit_upgrade_group_ui_links_tables`（界面箭头，C1 结论）
///   · AddCost                   → `resource_costs_tables`（金额是负数）
/// </summary>
public sealed class WarbandEdits
{
    /// <summary>一条升级关系（组 → 组）。</summary>
    public sealed record RouteEdit(string UpgradeKey, string Base, string Target, string Cost,
                                   int RequiredRank, int SubtractedRank);

    /// <summary>一条界面连线（子/父 + 出入口位 + 三个偏移）。</summary>
    public sealed record LinkEdit(string Child, string Parent, int ChildPos, int ParentPos,
                                  double ParentOffset, double ChildOffset, double MidOffset);

    public List<(string Unit, string Group)> AddJunction { get; } = [];
    public List<(string Unit, string Group)> RemoveJunction { get; } = [];
    public List<string> AddGroup { get; } = [];
    public List<string> RemoveGroup { get; } = [];
    /// <summary>组 → (x, y, category)；category 为 null = 不改页签。</summary>
    public Dictionary<string, (int X, int Y, string? Category)> InfoEdits { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<RouteEdit> AddRoute { get; } = [];
    public List<string> RemoveRoute { get; } = [];                       // upgrade_key
    public List<LinkEdit> AddLink { get; } = [];
    public List<(string Child, string Parent)> RemoveLink { get; } = [];
    /// <summary>
    /// 成本 Key → 金额。**带符号**（负 = 消耗、正 = 获得），落表时原样写进 `resource_costs_tables.treasury_cost`
    /// （原来这里存"正数、落表取负"，成本工坊要区分正负之后改成带符号）。
    /// </summary>
    public Dictionary<string, double> AddCost { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>删掉一个成本 id：`resource_costs_tables` 里那一行（**改原文件**，覆盖表删不掉别人的行）。</summary>
    public List<string> RemoveCost { get; } = [];

    /// <summary>
    /// 删掉某个成本 id 的"额外资源"关联：`resource_cost_pooled_resource_junctions_tables` 里 `resource_cost` = 它的行
    ///（改资源 / 清空资源 / 删成本都用它；本轮要写的那条会被排除，不会被误删）。
    /// </summary>
    public List<string> RemovePoolCost { get; } = [];

    /// <summary>
    /// 资源池行（`pooled_resource_factor_junctions_tables`）：成本工坊里"用其他资源"时自动建一个池。
    /// id = `&lt;资源名&gt;_warband_upgrade`、factor = other、min/max = ∓2147483647、sort_order = 0（用户口径）。
    /// </summary>
    public List<(string UniqueId, string Factor, string Resource, long Minimum, long Maximum, string SpecificFactionSet, int SortOrder)> PoolFactors { get; } = [];

    /// <summary>
    /// 成本 ↔ 资源池（`resource_cost_pooled_resource_junctions_tables`）：某个成本 Key 额外扣/加多少资源。
    /// PoolFactor 指向上面那行的 UniqueId；Context / UiPooled 照抄包里已有行（表里的后两列只作标签）。
    /// </summary>
    public List<(string PoolFactor, string ResourceCost, long Amount, string Context, string UiPooled)> PoolCosts { get; } = [];

    /// <summary>把兵从某个军事组里**移除**（删 units_to_groupings_military_permissions 的行）。</summary>
    public List<(string Unit, string MilitaryGroup)> RemoveUnitGroup { get; } = [];

    /// <summary>
    /// 需要"解锁战役经验"的兵（RoR/精英）：加进画布的兵自动进这里，
    /// 导出时在 `main_units_tables/studio_elite_unlock` 覆盖表里写 `restrict_xp_gain_in_campaign = false`
    /// （不然精英兵招募出来会锁 0 级）。
    /// </summary>
    public List<string> UnlockXp { get; } = [];

    /// <summary>页签重命名：(旧 key, 新 key)。改齐 categories 行 / infos.category / twui / 两张图。</summary>
    public List<(string Old, string New)> TabRenames { get; } = [];

    /// <summary>「打开页签」：把别的种族的页签也加进当前画布（这些页签里的兵**不自动写军事组授权**）。</summary>
    public List<string> OpenedTabs { get; } = [];

    /// <summary>
    /// 页签右键「应用到其他种族」：**手工**指定这个页签还要归给哪些种族（和「打开页签」同类，只影响工具里显示，不写包）。
    /// （自动归属是沿"页签→组→兵→派系→亚文化"推出来的；这里是用户加的补充。）
    /// </summary>
    public List<(string Category, string Race)> TabRaceScopes { get; } = [];

    /// <summary>新建页签时记下的归属：种族级（Faction 空）= 该种族所有军事组；派系级 = 只它那一个。</summary>
    public List<(string Category, string? Race, string? Faction)> TabScopes { get; } = [];

    /// <summary>兵 → 军事组 授权（units_to_groupings_military_permissions_tables）：
    /// 种族级编辑填该种族**所有**军事组，派系级只填它自己那一个。</summary>
    public List<(string Unit, string MilitaryGroup)> AddUnitGroup { get; } = [];

    /// <summary>新建页签（key, 克隆哪个母版页签）。页签本体由 <see cref="WarbandNewTab"/> 生成。</summary>
    /// <summary>新建页签（key, 克隆哪个母版, 背景图来源, 按钮图来源；来源为空 = 跟母版一样）。</summary>
    public List<(string Key, string Donor, string? BgSource, string? BtnSource)> NewTabs { get; } = [];

    /// <summary>**从包里删掉这些条目**（文件树右键「删除」；导出时写包会跳过它们）。</summary>
    public List<string> RemoveFiles { get; } = [];

    /// <summary>
    /// **文件替换**：把包里 <c>Target</c> 这个条目的字节换成 <c>Source</c> 那个条目的字节
    /// （素材库换背景/按钮图用：目标 = `ui/skins/default/warband_upgrades/background_images_&lt;页签&gt;.png`）。
    /// </summary>
    public List<(string Target, string Source)> FileReplacements { get; } = [];

    // ───────────────────────── 互斥写入 ─────────────────────────
    // 同一对的"先加后删 / 先删后加"要互相抵消（**后一次操作说了算**）。
    // 不这么做的话，读侧（Backend.UnitsOf / GroupsOfUnit）是"先减后加"：
    // 先按 RemoveJunction 减掉，再把 AddJunction 整串加回来 —— 本会话从兵种库拖进来的兵
    // 在合并/拆组后会**复活**在原组里（实测：拆一次多出一个复制品、原组不变）。
    private static bool Same(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);

    /// <summary>兵↔组：加（present=true）/ 删（false）。</summary>
    public void SetJunction(string unit, string group, bool present)
    {
        if (present)
        {
            RemoveJunction.RemoveAll(x => Same(x.Unit, unit) && Same(x.Group, group));
            if (!AddJunction.Any(x => Same(x.Unit, unit) && Same(x.Group, group))) AddJunction.Add((unit, group));
        }
        else
        {
            AddJunction.RemoveAll(x => Same(x.Unit, unit) && Same(x.Group, group));
            if (!RemoveJunction.Any(x => Same(x.Unit, unit) && Same(x.Group, group))) RemoveJunction.Add((unit, group));
        }
    }

    /// <summary>两个组是不是"同一对"（谁当 child 谁当 parent 都算）。</summary>
    private static bool SamePair(string c1, string p1, string c2, string p2) =>
        (Same(c1, c2) && Same(p1, p2)) || (Same(c1, p2) && Same(p1, c2));

    /// <summary>
    /// 界面连线**按对**加/改（两个方向算同一对）：动手之前先把这一对的旧编辑全清掉。
    /// 实测踩过：删掉一条、换个方向再连 → 旧行方向不同没被删掉 → 画布上出现**两条线**；
    /// 以及"调整画线"必须能把反方向那条覆盖掉，而不是又加一条。
    /// </summary>
    public void SetLinkPair(LinkEdit link, bool present)
    {
        AddLink.RemoveAll(x => SamePair(x.Child, x.Parent, link.Child, link.Parent));
        RemoveLink.RemoveAll(x => SamePair(x.Child, x.Parent, link.Child, link.Parent));
        if (present) AddLink.Add(link);
        else RemoveLink.Add((link.Child, link.Parent));
    }

    /// <summary>界面连线（child+parent 一对）：加 / 删。</summary>
    public void SetLink(LinkEdit link, bool present)
    {
        if (present)
        {
            RemoveLink.RemoveAll(x => Same(x.Child, link.Child) && Same(x.Parent, link.Parent));
            AddLink.RemoveAll(x => Same(x.Child, link.Child) && Same(x.Parent, link.Parent));
            AddLink.Add(link);
        }
        else
        {
            AddLink.RemoveAll(x => Same(x.Child, link.Child) && Same(x.Parent, link.Parent));
            if (!RemoveLink.Any(x => Same(x.Child, link.Child) && Same(x.Parent, link.Parent)))
                RemoveLink.Add((link.Child, link.Parent));
        }
    }

    /// <summary>升级路线（按 upgrade_key）：加/改 / 删。</summary>
    public void SetRoute(RouteEdit route, bool present)
    {
        if (present)
        {
            RemoveRoute.RemoveAll(k => Same(k, route.UpgradeKey));
            AddRoute.RemoveAll(x => Same(x.UpgradeKey, route.UpgradeKey));
            AddRoute.Add(route);
        }
        else
        {
            AddRoute.RemoveAll(x => Same(x.UpgradeKey, route.UpgradeKey));
            if (!RemoveRoute.Any(k => Same(k, route.UpgradeKey))) RemoveRoute.Add(route.UpgradeKey);
        }
    }

    /// <summary>组本身：建 / 删（删的同时把它的坐标行记录也撤掉）。</summary>
    public void SetGroup(string group, bool present)
    {
        if (present)
        {
            RemoveGroup.RemoveAll(g => Same(g, group));
            if (!AddGroup.Any(g => Same(g, group))) AddGroup.Add(group);
        }
        else
        {
            AddGroup.RemoveAll(g => Same(g, group));
            if (!RemoveGroup.Any(g => Same(g, group))) RemoveGroup.Add(group);
            InfoEdits.Remove(group);
        }
    }

    /// <summary>兵→军事组授权：加 / 移出。</summary>
    public void SetUnitGroup(string unit, string militaryGroup, bool present)
    {
        if (present)
        {
            RemoveUnitGroup.RemoveAll(x => Same(x.Unit, unit) && Same(x.MilitaryGroup, militaryGroup));
            if (!AddUnitGroup.Any(x => Same(x.Unit, unit) && Same(x.MilitaryGroup, militaryGroup)))
                AddUnitGroup.Add((unit, militaryGroup));
        }
        else
        {
            AddUnitGroup.RemoveAll(x => Same(x.Unit, unit) && Same(x.MilitaryGroup, militaryGroup));
            if (!RemoveUnitGroup.Any(x => Same(x.Unit, unit) && Same(x.MilitaryGroup, militaryGroup)))
                RemoveUnitGroup.Add((unit, militaryGroup));
        }
    }

    public int Count =>
        AddJunction.Count + RemoveJunction.Count + AddGroup.Count + RemoveGroup.Count + InfoEdits.Count +
        AddRoute.Count + RemoveRoute.Count + AddLink.Count + RemoveLink.Count + AddCost.Count + NewTabs.Count + AddUnitGroup.Count + FileReplacements.Count + RemoveUnitGroup.Count + TabScopes.Count + OpenedTabs.Count + TabRenames.Count + UnlockXp.Count + TabRaceScopes.Count + RemoveFiles.Count +
        PoolFactors.Count + PoolCosts.Count + RemoveCost.Count + RemovePoolCost.Count;

    public bool IsEmpty => Count == 0;

    /// <summary>人类可读的一行摘要（日志/状态栏用）。</summary>
    public string Summary()
    {
        var parts = new List<string>();
        void P(string name, int n) { if (n > 0) parts.Add($"{name} {n}"); }
        P("加兵", AddJunction.Count);
        P("删兵", RemoveJunction.Count);
        P("新组", AddGroup.Count);
        P("删组", RemoveGroup.Count);
        P("坐标/页签", InfoEdits.Count);
        P("新升级", AddRoute.Count);
        P("删升级", RemoveRoute.Count);
        P("新连线", AddLink.Count);
        P("删连线", RemoveLink.Count);
        P("新成本", AddCost.Count);
        P("军事组授权", AddUnitGroup.Count);
        P("移出军事组", RemoveUnitGroup.Count);
        P("新页签", NewTabs.Count);
        P("打开页签", OpenedTabs.Count);
        P("页签归属", TabScopes.Count);
        P("重命名页签", TabRenames.Count);
        P("解锁经验", UnlockXp.Count);
        P("资源池", PoolFactors.Count);
        P("成本扣资源", PoolCosts.Count);
        P("删成本", RemoveCost.Count);
        P("清成本资源", RemovePoolCost.Count);
        P("换图", FileReplacements.Count);
        P("页签→其他种族", TabRaceScopes.Count);
        P("删文件", RemoveFiles.Count);
        return parts.Count == 0 ? "没有编辑" : string.Join("，", parts);
    }

    /// <summary>把这一轮编辑做成"撤销点"的快照（后续做撤销栈用）。</summary>
    public WarbandEdits Clone()
    {
        var c = new WarbandEdits();
        c.AddJunction.AddRange(AddJunction);
        c.RemoveJunction.AddRange(RemoveJunction);
        c.AddGroup.AddRange(AddGroup);
        c.RemoveGroup.AddRange(RemoveGroup);
        foreach (var kv in InfoEdits) c.InfoEdits[kv.Key] = kv.Value;
        c.AddRoute.AddRange(AddRoute);
        c.RemoveRoute.AddRange(RemoveRoute);
        c.AddLink.AddRange(AddLink);
        c.RemoveLink.AddRange(RemoveLink);
        foreach (var kv in AddCost) c.AddCost[kv.Key] = kv.Value;
        c.PoolFactors.AddRange(PoolFactors);
        c.PoolCosts.AddRange(PoolCosts);
        c.RemoveCost.AddRange(RemoveCost);
        c.RemovePoolCost.AddRange(RemovePoolCost);
        return c;
    }
}
