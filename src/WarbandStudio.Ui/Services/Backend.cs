using System.IO;
using WarbandStudio.Pack;
using WarbandStudio.Packfile;
using WarbandStudio.Rpfm;

namespace WarbandStudio.Ui.Services;

/// <summary>
/// 引擎门面。两条路分工：
///   · **读**（打开包、列文件、解表）→ 原生格式层 <see cref="PackArchive"/> + <see cref="DbTable"/>：
///     毫秒级、纯内存、不起进程、不落临时文件（也不依赖 rpfm_cli 在不在）。
///   · **写 / 诊断 / 生成依赖缓存** → 随包的 rpfm_cli（RPFM 4.7.4）：按需拉起，用到才要。
/// schema（schema_wh3.ron）随包，首次读表时载入一次（约 170ms）。
/// </summary>
public sealed class Backend(AppSettings settings) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private RpfmCli? _cli;
    private Schema? _schema;
    private readonly List<PackSession> _packs = [];
    private PackSession? _pack;

    /// <summary>已打开的 pack（多个共存，各自一棵树）。</summary>
    public IReadOnlyList<PackSession> Packs => _packs;

    /// <summary>
    /// 画布上的全部编辑（"直接改 pack"：内存里攒着，导出/另存时由 WarbandAmender 一次落表）。
    /// </summary>
    /// <summary>
    /// **每个包一份编辑**：换包/切画布不会串台（原版里新建的页签不会跑到雪乃的画布上）。
    /// </summary>
    private readonly Dictionary<string, WarbandEdits> _editsByPack = new(StringComparer.OrdinalIgnoreCase);
    private WarbandEdits _fallbackEdits = new();
    public WarbandEdits Edits =>
        _pack?.PackPath is { Length: > 0 } k
            ? (_editsByPack.TryGetValue(k, out var e) ? e : _editsByPack[k] = new WarbandEdits())
            : _fallbackEdits;

    /// <summary>放弃全部未导出的编辑（关画布时用户选了"放弃"）。</summary>
    public void DiscardEdits()
    {
        var n = Edits.Count;
        if (_pack?.PackPath is { Length: > 0 } k) _editsByPack[k] = new WarbandEdits();
        else _fallbackEdits = new WarbandEdits();
        InvalidateCanvas();
        RestagePendingArt();          // 放弃编辑 → 换图的预览也撤掉（画布回到包里那张）
        Log?.Invoke($"已放弃 {n} 处未导出的编辑");
    }

    /// <summary>
    /// 放弃**指定包**的待导出编辑。保存写回后要用这个，不能用 <see cref="DiscardEdits"/> ——
    /// `SaveInPlaceAsync` 里"关掉这个包"会把当前包切到别的画布页签，那时 `DiscardEdits()` 清的是**别的包**的编辑
    /// （用户实测：保存 A 之后 B 的待导出编辑没了、看着像"保存后成本消失"）。
    /// </summary>
    public void DiscardEditsOf(string packPath)
    {
        var n = _editsByPack.TryGetValue(packPath, out var e) ? e.Count : 0;
        _editsByPack[packPath] = new WarbandEdits();
        InvalidateCanvas();
        RestagePendingArt();
        Log?.Invoke($"已放弃 {n} 处未导出的编辑（{Path.GetFileName(packPath)}）");
    }

    /// <summary>关掉一个包（画布页签的 × ）。</summary>
    public async Task CloseSessionAsync(PackSession session)
    {
        if (_packs.Remove(session)) await session.CloseAsync();
        if (ReferenceEquals(_pack, session)) _pack = _packs.LastOrDefault();
        _factionCache = null;
        _milGroups = null;
        _raceMilGroups = null;
        _canvasCache = null;
        _canvasCacheKey = null;
        Log?.Invoke($"已关闭包：{session.DisplayName}");
    }
    public int EditCount => Edits.Count;
    public string EditSummary => Edits.Summary();

    /// <summary>**某个包**有多少处未导出编辑（关别的包 / 关画布页签时判断"要不要先问保存"用）。</summary>
    public int EditCountOf(string? packPath) =>
        packPath is { Length: > 0 } && _editsByPack.TryGetValue(packPath, out var e) ? e.Count : 0;

    /// <summary>某个包未导出编辑的摘要。</summary>
    public string EditSummaryOf(string? packPath) =>
        packPath is { Length: > 0 } && _editsByPack.TryGetValue(packPath, out var e) ? e.Summary() : "";

    public void SetGroupPos(string group, int x, int y)
    {
        var cat = Edits.InfoEdits.TryGetValue(group, out var old) ? old.Category : null;
        Edits.InfoEdits[group] = (x, y, cat);
        Log?.Invoke($"画布拖动：{group} → ({x},{y})（待导出）");
    }

    /// <summary>
    /// 多选统一坐标：**只改一个轴**（X 或 Y），另一轴各自的现值保留
    /// —— 单选时"应用坐标"是 x/y 一起写，多选时那样会把每组不同的另一个轴也抹平。
    /// </summary>
    public int SetGroupsAxis(IEnumerable<string> groups, string axis, int value)
    {
        var n = 0;
        foreach (var g in groups.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var cur = Edits.InfoEdits.TryGetValue(g, out var p) ? (p.X, p.Y, p.Category)
                    : PackPosOf(g) is { } q ? (q.Item1, q.Item2, q.Item3) : ((int, int, string?)?)null;
            var nx = axis.Equals("x", StringComparison.OrdinalIgnoreCase) ? value : (cur?.Item1 ?? 0);
            var ny = axis.Equals("y", StringComparison.OrdinalIgnoreCase) ? value : (cur?.Item2 ?? 0);
            Edits.InfoEdits[g] = (nx, ny, cur?.Item3);
            n++;
        }
        if (n > 0) InvalidateCanvas();
        Log?.Invoke($"多选坐标：{n} 个组只改 {axis.ToUpperInvariant()} = {value}（另一轴不动）　待导出");
        return n;
    }

    /// <summary>把组挪到另一个页签（写 infos 的 category）。</summary>
    public void SetGroupCategory(string group, string category, int x, int y)
    {
        Edits.InfoEdits[group] = (x, y, category);
        Log?.Invoke($"改页签：{group} → {category}（待导出）");
    }

    /// <summary>
    /// 兵种库双击/拖入：加兵到组；新组连组一起建。
    /// <paramref name="overwrite"/> = 画布上已经问过"这个兵已经被使用，是否继续加入" → 继续就**覆盖之前的表单**：
    /// 把这个兵旧的 junction 行删掉，只留这一次加入的位置。
    /// </summary>
    public void AddUnitToGroup(string group, string unit, int x, int y, bool isNewGroup, string? category = null,
                               bool overwrite = false)
    {
        NoteElite(unit);                       // RoR/精英：自动解锁战役经验（否则招出来锁 0 级）
        var movedFrom = new List<string>();
        if (overwrite)
            foreach (var g in GroupsOfUnit(unit))
                if (!g.Equals(group, StringComparison.OrdinalIgnoreCase))
                {
                    Edits.SetJunction(unit, g, false);
                    movedFrom.Add(g);
                }
        Edits.SetJunction(unit, group, true);
        if (isNewGroup) Edits.SetGroup(group, true);
        // 加进**已有组**时别用消息里的坐标：那是页面渲染时的快照，刚拖动过就可能过期
        // （用了就会把"拖到一半还没重推"的坐标改回去）。以当前生效的位置为准。
        if (!isNewGroup)
        {
            var cur = Edits.InfoEdits.TryGetValue(group, out var p) ? (p.X, p.Y)
                    : PackPosOf(group) is { } q ? (q.Item1, q.Item2) : (x, y);
            x = cur.Item1; y = cur.Item2;
        }
        var cat = category ?? (Edits.InfoEdits.TryGetValue(group, out var old) ? old.Category : null);
        Edits.InfoEdits[group] = (x, y, cat);
        Log?.Invoke($"画布加兵：{unit} → 组 {group}（{x},{y}）{(isNewGroup ? "（新组）" : "")}" +
                    (movedFrom.Count > 0 ? $"；覆盖：已把旧的从 {string.Join("、", movedFrom)} 移出" : "") + "　待导出");
    }

    /// <summary>删一张兵卡（组里只剩它时用 <see cref="RemoveGroup"/>）。</summary>
    public void RemoveUnitFromGroup(string group, string unit)
    {
        Edits.SetJunction(unit, group, false);
        Log?.Invoke($"画布删兵：{unit} ← 组 {group}（待导出）");
    }

    /// <summary>删整组（连带 junction / 坐标 / 连线 / 升级路线）。</summary>
    public void RemoveGroup(string group)
    {
        Edits.SetGroup(group, false);
        Log?.Invoke($"画布删组：{group}（连带的兵、连线、升级路线一起删；待导出）");
    }

    /// <summary>
    /// 新建一条升级（组 → 组）：按 C1 结论**路线 + UI 连线都写**。
    /// 成本：<paramref name="costKey"/> 用已有 Key；传空且 <paramref name="autoCost"/> 有值就自动新建一个成本 Key。
    /// <paramref name="alsoLink"/> = false（两端不在同一页签的"跨页"关系）时**只写路线表**，不写界面连线 —— 
    /// 跨页连线在游戏里画不出来（面板只画当前页签的坐标层），留着只会变成"有线看不见"的垃圾行。
    /// </summary>
    public string AddRoute(string baseGroup, string targetGroup, string? costKey, int requiredRank, int subtractedRank,
                           bool mutual, int childPos, int parentPos, double autoCost = 0, bool alsoLink = true,
                           double midOffset = 0)
    {
        var cost = costKey ?? "";
        if (string.IsNullOrWhiteSpace(cost) && autoCost > 0)
        {
            cost = $"studio_cost_{Math.Abs(autoCost):0}";
            Edits.AddCost[cost] = -Math.Abs(autoCost);   // AddCost 带符号：自动成本 = 消耗（负）
        }

        var key = $"studio_{baseGroup}_to_{targetGroup}";
        if (key.Length > 200) key = key[..200];
        Edits.SetRoute(new WarbandEdits.RouteEdit(key, baseGroup, targetGroup, cost, requiredRank, subtractedRank), true);
        // 连线：parent = base（升级来源），child = target（升级结果）——原版数据 738/747 是这个方向
        if (alsoLink)
        {
            Edits.SetLinkPair(new WarbandEdits.LinkEdit(targetGroup, baseGroup, childPos, parentPos, 0, 0, midOffset), true);
            if (mutual)
            {
                var key2 = $"studio_{targetGroup}_to_{baseGroup}";
                if (key2.Length > 200) key2 = key2[..200];
                Edits.SetRoute(new WarbandEdits.RouteEdit(key2, targetGroup, baseGroup, cost, requiredRank, subtractedRank), true);
                Edits.SetLinkPair(new WarbandEdits.LinkEdit(baseGroup, targetGroup, parentPos, childPos, 0, 0, 0), true);
            }
        }
        Log?.Invoke($"新建升级：{baseGroup} → {targetGroup}（成本 {cost}，等级 {requiredRank}/-{subtractedRank}" +
                    (mutual && alsoLink ? "，双向" : "") +
                    (alsoLink ? "" : "；只写路线表、不写界面连线（勾了「只升级不连线」或两端跨页）") + "）　待导出");
        return key;
    }

    /// <summary>改一条升级的金额/等级（键不变 → 覆盖表替换原行）。</summary>
    public void UpdateRoute(string upgradeKey, string baseGroup, string targetGroup, string cost, int requiredRank, int subtractedRank)
    {
        Edits.SetRoute(new WarbandEdits.RouteEdit(upgradeKey, baseGroup, targetGroup, cost, requiredRank, subtractedRank), true);
        Log?.Invoke($"改升级：{upgradeKey}（成本 {cost}，等级 {requiredRank}/-{subtractedRank}）　待导出");
    }

    /// <summary>
    /// 交换升级方向（原来 A→B 变 B→A）。
    /// <paramref name="alsoLink"/> = false（跨页）时**只改路线表**，不动界面连线。
    /// </summary>
    public void SwapRoute(string upgradeKey, string baseGroup, string targetGroup, string cost, int requiredRank, int subtractedRank,
                          int childPos, int parentPos, bool alsoLink = true, double midOffset = 0)
    {
        Edits.SetRoute(new WarbandEdits.RouteEdit(upgradeKey, targetGroup, baseGroup, cost, requiredRank, subtractedRank), true);
        if (alsoLink)
        {
            Edits.SetLinkPair(new WarbandEdits.LinkEdit(targetGroup, baseGroup, childPos, parentPos, 0, 0, 0), false);  // 旧连线（按对清：两个方向都算）
            // 新连线：child=新 target（原 base）、parent=新 base（原 target）。
            // LinkEdit 的参数顺序是 (Child, Parent, ChildPos, ParentPos) —— 这里原先把两个位置传反了，
            // 交换方向后线会从"不对着的那条边"出去（绕远），改成按新方向的位置。
            Edits.SetLinkPair(new WarbandEdits.LinkEdit(baseGroup, targetGroup, childPos, parentPos, 0, 0, midOffset), true);
            Log?.Invoke($"交换方向：{baseGroup} ⇄ {targetGroup}（路线 + 界面连线一起翻）　待导出");
        }
        else
            Log?.Invoke($"交换方向：{baseGroup} ⇄ {targetGroup}（只改路线表，不动界面连线）　待导出");
    }

    /// <summary>
    /// 只补一条界面连线（ui_links），**不动升级路线**：给"升级关系在、线没了"的情况用
    /// （多选两个组时按钮会变成「添加连线」）。和 RemoveLinkOnly 是一对。
    /// </summary>
    public void AddLinkOnly(string child, string parent, int childPos, int parentPos, string why = "补连线",
                            double midOffset = 0)
    {
        if (string.IsNullOrWhiteSpace(child) || string.IsNullOrWhiteSpace(parent)) return;
        Edits.SetLinkPair(new WarbandEdits.LinkEdit(child, parent, childPos, parentPos, 0, 0, midOffset), true);
        InvalidateCanvas();
        Log?.Invoke($"{why}：{parent} → {child}（只写 ui_links，升级路线不动；同一对只留这一条）　待导出");
    }

    /// <summary>只删一条界面连线（不动路线行）：给"这条线在路线表里找不到对应行"的情况用。</summary>
    public void RemoveLinkOnly(string child, string parent)
    {
        if (string.IsNullOrWhiteSpace(child) || string.IsNullOrWhiteSpace(parent)) return;
        Edits.SetLinkPair(new WarbandEdits.LinkEdit(child, parent, 0, 0, 0, 0, 0), false);
        InvalidateCanvas();
        Log?.Invoke($"删连线（只删线）：{parent} → {child}（这一对的两个方向一起清）　待导出");
    }

    /// <summary>删一条升级（路线 + 对应连线一起删）。</summary>
    public void RemoveRoute(string upgradeKey, string baseGroup, string targetGroup, bool alsoLink = true)
    {
        Edits.SetRoute(new WarbandEdits.RouteEdit(upgradeKey, baseGroup, targetGroup, "", 0, 0), false);
        if (alsoLink) Edits.SetLinkPair(new WarbandEdits.LinkEdit(targetGroup, baseGroup, 0, 0, 0, 0, 0), false);
        Log?.Invoke($"删升级：{upgradeKey}（{baseGroup} → {targetGroup}）{(alsoLink ? "，连线一起删" : "")}　待导出");
    }

    /// <summary>
    /// 加兵时按当前选中的**种族/派系**自动填军事组授权
    /// （`units_to_groupings_military_permissions_tables`）：
    ///   传 faction → 只填这个派系的军事组；只传 race → 填该种族的**所有**军事组（通用组 + 各专属组）。
    /// </summary>
    public int GrantUnit(string unit, string? race, string? faction)
    {
        var (byFac, byRace) = MilGroups();
        var groups = new List<string>();
        if (!string.IsNullOrWhiteSpace(faction) && byFac.TryGetValue(faction!, out var info)) groups.Add(info.Group);
        else if (!string.IsNullOrWhiteSpace(race) && byRace.TryGetValue(race!, out var gs)) groups.AddRange(gs);
        foreach (var g in groups.Distinct(StringComparer.OrdinalIgnoreCase))
            Edits.SetUnitGroup(unit, g, true);
        if (groups.Count > 0) InvalidateCanvas();
        Log?.Invoke($"军事组授权：{unit} → {(groups.Count == 0 ? "（没选种族/派系，未填）" : string.Join(" + ", groups))}");
        return groups.Count;
    }

    /// <summary>
    /// 合并组：把 <paramref name="drops"/> 里的兵全部并进 <paramref name="keep"/>，再删掉被并的组
    /// （删组会连带清掉它们的坐标行、两端连线与升级路线）。传坐标就是"合并成一个新组"的用法。
    /// </summary>
    public void MergeGroups(string keep, IEnumerable<string> drops, int? x = null, int? y = null, string? category = null)
    {
        var list = drops.Where(d => d.Length > 0 && !d.Equals(keep, StringComparison.OrdinalIgnoreCase)).ToList();
        if (list.Count == 0) return;
        // 画布不再自己起名（"合并成一个组"原来用 studio_group_时间戳，重载后不可读）：
        // 空 keep = 让后端按命名规则起（<前缀>_<页签>_<合并后每个兵的词…>）。
        if (string.IsNullOrWhiteSpace(keep))
            keep = MakeGroupKey(category, list.SelectMany(UnitsOf), $"studio_group_{DateTime.Now:HHmmssff}");
        foreach (var d in list)
        {
            Edits.SetGroup(keep, true);
            foreach (var u in UnitsOf(d))
            {
                Edits.SetJunction(u, d, false);
                Edits.SetJunction(u, keep, true);
            }
            Edits.SetGroup(d, false);
        }
        // 合并后**必须**给 keep 留下坐标+页签：否则（尤其是 keep 是本次新建的组时）
        // studio_layout 里没有它的行 → 重新加载包后这一组（连同里面的精英兵）就"消失"了
        {
            var pos = Edits.InfoEdits.TryGetValue(keep, out var p0) ? p0 : ((int, int, string?)?)null;
            if (pos is null) pos = PackPosOf(keep);
            if (pos is null && list.Count > 0)
                pos = Edits.InfoEdits.TryGetValue(list[0], out var p1) ? p1 : PackPosOf(list[0]);
            var fx = x ?? pos?.Item1 ?? 0;
            var fy = y ?? pos?.Item2 ?? 0;
            var fc = category ?? pos?.Item3;
            Edits.InfoEdits[keep] = (fx, fy, fc);
            Log?.Invoke($"合并组：{keep} 坐标/页签 → ({fx},{fy},{fc ?? "—"})");
        }
        InvalidateCanvas();
        var detail = string.Join(" + ", list.Select(d => $"{d}（{UnitsOf(d).Count} 个兵）"));
        Log?.Invoke($"合并组：{detail} → {keep}（合并后 {UnitsOf(keep).Count} 个兵）　待导出");
    }

    /// <summary>
    /// 拆分组：第一个兵留在原组（保住原组的连线/路线），其余的兵各自拆成一个新组，
    /// 摆在原组右侧一格一列（46×105 的步长）。
    /// </summary>
    public int SplitGroup(string group, int x, int y, string? category, string? race, string? faction)
    {
        var units = UnitsOf(group);
        if (units.Count <= 1) return 0;
        var made = 0;
        for (var i = 1; i < units.Count; i++)
        {
            var key = MakeGroupKey(category, new[] { units[i] }, $"studio_split_{DateTime.Now:HHmmssff}_{i}");
            Edits.SetGroup(key, true);
            Edits.SetJunction(units[i], group, false);
            Edits.SetJunction(units[i], key, true);
            GrantUnit(units[i], race, faction);
            Edits.InfoEdits[key] = (x + 46 * i, y + 105, category);
            made++;
        }
        InvalidateCanvas();
        Log?.Invoke($"拆分组：{group} → 留下 1 个兵 + 拆出 {made} 个单兵组　待导出");
        return made;
    }

    /// <summary>某个兵当前在哪些组里（"加兵时问它是不是已经被用过"要用；包含本轮待导出的编辑）。</summary>
    private List<string> GroupsOfUnit(string unit)
    {
        var list = new List<string>();
        try
        {
            var pack = _pack?.Archive;
            if (pack is null) return list;
            var t = TableFiles.ReadConcat(pack, "unit_to_unit_group_junctions_tables", GetSchema(), null);
            if (t is null) return list;
            var ju = Col(t, "unit"); var jg = Col(t, "unit_group");
            if (ju < 0 || jg < 0) return list;
            foreach (var r in t.Rows)
                if (r[ju].ToTsv().Equals(unit, StringComparison.OrdinalIgnoreCase))
                {
                    var g = r[jg].ToTsv();
                    if (g.Length > 0 && !list.Contains(g, StringComparer.OrdinalIgnoreCase)) list.Add(g);
                }
        }
        catch { }
        // 叠加"待导出"的编辑（和 UnitsOf 同一个道理：本轮新建的组在包里还没有行）
        list.RemoveAll(g => Edits.RemoveJunction.Any(x => x.Unit.Equals(unit, StringComparison.OrdinalIgnoreCase)
                                                       && x.Group.Equals(g, StringComparison.OrdinalIgnoreCase)));
        foreach (var (u2, g2) in Edits.AddJunction)
            if (u2.Equals(unit, StringComparison.OrdinalIgnoreCase) && !list.Contains(g2, StringComparer.OrdinalIgnoreCase))
                list.Add(g2);
        return list;
    }

    /// <summary>从包里读某个组的坐标/页签（合并时 keep 没有编辑记录就用它兜底）。</summary>
    private (int, int, string?)? PackPosOf(string group)
    {
        try
        {
            var pack = _pack?.Archive;
            if (pack is null) return null;
            var t = TableFiles.ReadMerged(pack, "unit_upgrade_group_ui_infos_tables", GetSchema(), null);
            if (t is null) return null;
            var g = Col(t, "unit_upgrade_group"); var xi = Col(t, "x"); var yi = Col(t, "y"); var ci = Col(t, "category");
            if (g < 0 || xi < 0 || yi < 0) return null;
            foreach (var r in t.Rows)
                if (r[g].ToTsv().Equals(group, StringComparison.OrdinalIgnoreCase))
                    return ((int)r[xi].Int, (int)r[yi].Int, ci >= 0 ? r[ci].ToTsv() : null);
        }
        catch { }
        return null;
    }

    /// <summary>某个组当前的兵（合并/搬家时要知道原来有哪些）。</summary>
    private List<string> UnitsOf(string group)
    {
        var list = new List<string>();
        try
        {
            var pack = _pack?.Archive;
            if (pack is null) return list;
            var t = TableFiles.ReadConcat(pack, "unit_to_unit_group_junctions_tables", GetSchema(), null);
            if (t is null) return list;
            var ju = Col(t, "unit"); var jg = Col(t, "unit_group");
            if (ju < 0 || jg < 0) return list;
            foreach (var r in t.Rows)
                if (r[jg].ToTsv().Equals(group, StringComparison.OrdinalIgnoreCase))
                {
                    var u = r[ju].ToTsv();
                    if (u.Length > 0 && !list.Contains(u, StringComparer.OrdinalIgnoreCase)) list.Add(u);
                }
        }
        catch { }
        // 关键：叠加"待导出"的编辑 —— 本次会话里新建的组（加兵/拆组/合并出来的）在包里还没有行，
        // 只看包就会得到"0 个兵"，合并时就会把它们整组删掉（实测：4 个组一起消失就是这个原因）
        list.RemoveAll(u => Edits.RemoveJunction.Any(x => x.Group.Equals(group, StringComparison.OrdinalIgnoreCase)
                                                      && x.Unit.Equals(u, StringComparison.OrdinalIgnoreCase)));
        foreach (var (u2, g2) in Edits.AddJunction)
            if (g2.Equals(group, StringComparison.OrdinalIgnoreCase) && !list.Contains(u2, StringComparer.OrdinalIgnoreCase))
                list.Add(u2);
        return list;
    }

    /// <summary>
    /// 多选建组：把选中的兵**移到**一个新组（junction 里删旧行、加新行；新组建组 + 写坐标 + 军事组授权）。
    /// </summary>
    public void MoveUnitsToNewGroup(string group, List<(string Group, string Unit)> units,
                                    int x, int y, string? category, string? race, string? faction)
    {
        Edits.SetGroup(group, true);
        foreach (var (oldGroup, unit) in units)
        {
            Edits.SetJunction(unit, oldGroup, false);
            Edits.SetJunction(unit, group, true);
            GrantUnit(unit, race, faction);
        }
        Edits.InfoEdits[group] = (x, y, category);
        InvalidateCanvas();
        Log?.Invoke($"多选建组：{units.Count} 个兵 → 新组 {group}（{x},{y}，页签 {category ?? "—"}）　待导出");
    }

    /// <summary>新建分组（空组，先落在画布上，之后往里拖兵）。</summary>
    public void NewGroup(string group, int x, int y, string? category)
    {
        if (string.IsNullOrWhiteSpace(group)) return;
        if (GroupKeyTaken(group))
        {
            var old = group;
            group = GroupNaming.Unique(group, GroupKeyTaken);
            Log?.Invoke($"新建分组：key「{old}」已被占用 → 改用「{group}」");
        }
        Edits.SetGroup(group, true);
        Edits.InfoEdits[group] = (x, y, category);
        Log?.Invoke($"新建分组：{group}（{x},{y}，页签 {category ?? "—"}）　待导出");
    }

    /// <summary>当前工程的**项目 key**（新单位组的命名前缀；v1.5.0 起存工程 project.json，不再是全局设置）。
    /// VM 在打开/新建/切工程（和改 key）时同步到这里；空 = 没工程 → 新组命名退回旧时间戳规则。</summary>
    public string ProjectKey { get; set; } = "";

    /// <summary>新建单位组的 key（命名规则见 <see cref="GroupNaming"/>：`<项目 key>_<页签>_<兵种词…>`；
    /// 项目 key 为空就退回 <paramref name="fallback"/> 的旧时间戳名）。</summary>
    public string MakeGroupKey(string? category, IEnumerable<string> units, string fallback) =>
        GroupNaming.NewKey(ProjectKey, category, units, GroupKeyTaken) ?? fallback;

    /// <summary>这个组名现在是不是已经被占用（包里已有的组 + 本会话新建的组；本轮删掉的可以复用）。
    /// 组名是表主键，重了会把两个组的数据混在一起 —— 起名时靠它去重。</summary>
    private bool GroupKeyTaken(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        if (Edits.AddGroup.Any(g => g.Equals(key, StringComparison.OrdinalIgnoreCase))) return true;
        if (Edits.RemoveGroup.Any(g => g.Equals(key, StringComparison.OrdinalIgnoreCase))) return false;
        var pack = _pack?.Archive;
        if (pack is null) return false;
        try
        {
            foreach (var f in TableFiles.EntriesFor(pack, "unit_upgrade_groups_tables"))
            {
                var t = DbTable.Decode(pack.ReadDecoded(f), "unit_upgrade_groups_tables", GetSchema());
                var c = t.Columns.FindIndex(x => x.Name.Equals("unit_group", StringComparison.OrdinalIgnoreCase));
                if (c < 0) continue;
                foreach (var r in t.Rows)
                    if (r[c].ToTsv().Equals(key, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        catch { /* 读不动就当没占用，别拦着建组 */ }
        return false;
    }

    /// <summary>页签重命名（一次改齐 categories / infos / twui / 两张图）。</summary>
    public void RenameTab(string oldKey, string newKey)
    {
        newKey = (newKey ?? "").Trim().ToUpperInvariant();
        if (oldKey.Length == 0 || newKey.Length == 0 || oldKey.Equals(newKey, StringComparison.OrdinalIgnoreCase)) return;
        Edits.TabRenames.Add((oldKey, newKey));
        for (var i = 0; i < Edits.TabScopes.Count; i++)
            if (Edits.TabScopes[i].Category.Equals(oldKey, StringComparison.OrdinalIgnoreCase))
                Edits.TabScopes[i] = (newKey, Edits.TabScopes[i].Race, Edits.TabScopes[i].Faction);
        for (var i = 0; i < Edits.OpenedTabs.Count; i++)
            if (Edits.OpenedTabs[i].Equals(oldKey, StringComparison.OrdinalIgnoreCase)) Edits.OpenedTabs[i] = newKey;
        // **两处以前漏掉的**（用户问"重命名会不会把用到旧 id 的地方都改掉"时查出来）：
        //   · 「应用到其他种族」的归属（TabRaceScopes）——不改的话改名后这份手工归属就丢了 ✗；
        //   · **本会话新建页签的登记**（NewTabs）——不改的话导出会按旧 key 再建一个页签，等于把改名顶回去 ✗。
        for (var i = 0; i < Edits.TabRaceScopes.Count; i++)
            if (Edits.TabRaceScopes[i].Category.Equals(oldKey, StringComparison.OrdinalIgnoreCase))
                Edits.TabRaceScopes[i] = (newKey, Edits.TabRaceScopes[i].Race);
        for (var i = 0; i < Edits.NewTabs.Count; i++)
            if (Edits.NewTabs[i].Key.Equals(oldKey, StringComparison.OrdinalIgnoreCase))
                Edits.NewTabs[i] = (newKey, Edits.NewTabs[i].Donor, Edits.NewTabs[i].BgSource, Edits.NewTabs[i].BtnSource);
        InvalidateCanvas();
        Log?.Invoke($"页签重命名：{oldKey} → {newKey}（categories/infos/twui/两张图一起改）　待导出");
    }

    /// <summary>页签右键「应用到其他种族」：记下"这个页签还要归给哪些种族"（只影响工具里显示，不写包）。</summary>
    public void ApplyTabToRaces(string category, IEnumerable<string> races)
    {
        if (string.IsNullOrWhiteSpace(category)) return;
        Edits.TabRaceScopes.RemoveAll(x => x.Category.Equals(category, StringComparison.OrdinalIgnoreCase));
        foreach (var r in races)
            if (r.Length > 0) Edits.TabRaceScopes.Add((category, r));
        InvalidateCanvas();
        Log?.Invoke($"页签归属（手工）：{category} → {(races.Count() == 0 ? "（清空）" : string.Join("、", races))}　待导出前只影响画布显示");
    }

    /// <summary>关闭一个「打开」进来的页签（只从画布上收起来，不动包）。</summary>
    public void CloseOpenedTab(string category)
    {
        var n = Edits.OpenedTabs.RemoveAll(t => t.Equals(category, StringComparison.OrdinalIgnoreCase));
        InvalidateCanvas();
        Log?.Invoke($"收起页签：{category}（{n} 项）");
    }

    /// <summary>「打开页签」：把别的种族的页签也显示到当前画布（不写任何授权，只影响显示）。</summary>
    public void OpenTabs(IEnumerable<string> tabs)
    {
        foreach (var t in tabs)
            if (t.Length > 0 && !Edits.OpenedTabs.Contains(t, StringComparer.OrdinalIgnoreCase)) Edits.OpenedTabs.Add(t);
        InvalidateCanvas();
        Log?.Invoke($"打开页签：{string.Join("、", Edits.OpenedTabs)}（这些页签里的兵不写军事组授权，兵牌打黄标）");
    }

    /// <summary>记下新建页签的归属（种族级/派系级），往里加兵时按它兜底填军事组。</summary>
    public void RememberTabScope(string category, string? race, string? faction)
    {
        if (string.IsNullOrWhiteSpace(category)) return;
        Edits.TabScopes.Add((category, race, faction));
        Log?.Invoke($"页签 {category} 的归属：{(string.IsNullOrEmpty(faction) ? (race ?? "（未选种族）") + "（整个种族）" : faction + "（单个派系）")}");
    }

    /// <summary>页签的归属（新建时记下的）。</summary>
    public (string? Race, string? Faction) ScopeOf(string? category)
    {
        if (category is null) return (null, null);
        for (var i = Edits.TabScopes.Count - 1; i >= 0; i--)
            if (Edits.TabScopes[i].Category.Equals(category, StringComparison.OrdinalIgnoreCase))
                return (Edits.TabScopes[i].Race, Edits.TabScopes[i].Faction);
        return (null, null);
    }

    /// <summary>加进画布的兵记一笔"要解锁战役经验"（导出时写 main_units 覆盖表）。</summary>
    private void NoteElite(string unit)
    {
        if (string.IsNullOrWhiteSpace(unit)) return;
        if (!Edits.UnlockXp.Contains(unit, StringComparer.OrdinalIgnoreCase)) Edits.UnlockXp.Add(unit);
    }

    /// <summary>把兵从当前军事组里移除（组右键菜单）：删 units_to_groupings_military_permissions 的行。</summary>
    public void RemoveUnitFromMilGroup(IEnumerable<string> units, string? race, string? faction)
    {
        var (byFac, byRace) = MilGroups();
        var groups = new List<string>();
        if (!string.IsNullOrWhiteSpace(faction) && byFac.TryGetValue(faction!, out var info)) groups.Add(info.Group);
        else if (!string.IsNullOrWhiteSpace(race) && byRace.TryGetValue(race!, out var gs)) groups.AddRange(gs);
        var n = 0;
        foreach (var u in units)
            foreach (var g in groups.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                Edits.SetUnitGroup(u, g, false);
                n++;
            }
        InvalidateCanvas();
        Log?.Invoke($"移出军事组：{n} 条（{(groups.Count == 0 ? "没选种族/派系" : string.Join(" + ", groups))}）　待导出" +
                    "　（组不会从画布/页签消失：拿掉全部军事组授权后画布给它们打「不在军事组」角标）");
    }

    /// <summary>新建页签（整套克隆：hierarchy 节点 + holder_tab + button_toggle_tab + 背景面板/条目，见 WarbandNewTab）。</summary>
    public void NewTab(string key, string donor, string? bgSource = null, string? btnSource = null)
    {
        // 目标名按页签 key（twui 按这个名字找图）；素材库里的**文件名保持不动**（库是共享的，改名会互相覆盖）
        var bg = bgSource;
        var btn = btnSource;
        Edits.NewTabs.Add((key, donor, bg, btn));
        // 游戏里页签的显示条件是"这个分类下有组"（twui 的 ContextVisibilitySetter 回调）——
        // 建完页签先自动放一个空组，免得"新建的页签在游戏里看不到"
        var autoGroup = $"studio_tab_{key.ToLowerInvariant()}";
        Edits.SetGroup(autoGroup, true);
        Edits.InfoEdits[autoGroup] = (0, 0, key);
        Log?.Invoke($"新建页签 {key}：已自动放一个空组 {autoGroup}（游戏里页签靠分类下有组才显示）");
        // 画布预览：把"这一页签的图"抽成 <key> 的名字。
        // 不抽的话画布会去找 background_images_<key>.png 而包里还没有 → 背景空着（以前甚至会兜底到别的图）
        try
        {
            var pack = _pack?.Archive;
            if (pack is not null)
            {
                var skins = Skins(pack, _pack?.PackPath);
                var low = key.ToLowerInvariant();
                var useDonor = string.IsNullOrWhiteSpace(donor) ? WarbandNewTab.PickDonor(pack) : donor;
                foreach (var (kind, want) in new[] { ("background_images_", bg), ("button_upgrade_", btn) })
                {
                    var file = $"{kind}{low}.png";
                    skins.DropCache(file);                          // 重建同名页签时别让旧图顶上来
                    var src = !string.IsNullOrWhiteSpace(want)
                        ? want!
                        : (useDonor is null ? null : $"ui/skins/default/warband_upgrades/{kind}{useDonor.ToLowerInvariant()}.png");
                    if (src is null) continue;
                    var staged = File.Exists(src)
                        ? skins.StageLocalFile(src, file)          // 素材库里的本地文件
                        : skins.ExtractForPage(src, file);         // 包内路径（母版的图）
                    if (staged is not null) Log?.Invoke($"页签 {key} 的画布预览：{kind}{low}.png ← {src}");
                }
            }
        }
        catch (Exception ex) { Log?.Invoke("页签图预览失败：" + ex.Message); }
        Log?.Invoke($"新建页签：{key}（母版 {(string.IsNullOrWhiteSpace(donor) ? "自动" : donor)}）　待导出");
    }

    /// <summary>
    /// **待导出新增的文件**（当前包里还没有的那些 FileReplacements 目标）——
    /// 文件树要把它们显示成"（待导出）"，否则用户会以为"添加到包没生效"（其实导出时才落盘）。
    /// </summary>
    public List<string> PendingNewFiles()
    {
        var list = new List<string>();
        try
        {
            var pack = _pack?.Archive;
            if (pack is null) return list;
            foreach (var (target, _) in Edits.FileReplacements)
                if (target.Length > 0 && pack.Find(target) is null && !list.Contains(target, StringComparer.OrdinalIgnoreCase))
                    list.Add(target);
        }
        catch { }
        return list;
    }

    /// <summary>
    /// UI 素材库的一条（**本地素材**，不是包里那条）：
    /// <paramref name="Kind"/> = bg（背景图）/ btn（按钮图）/ other（框、面板底等）；
    /// <paramref name="File"/> = 本地文件路径（WPF 直接当图片用、导出时按它读字节）；
    /// <paramref name="PackPath"/> = 建议写进包的位置（`ui/skins/default/warband_upgrades/&lt;文件名&gt;`）。
    /// </summary>
    public sealed record UiAsset(string Kind, string Name, string File, string Url, string PackPath, long Size,
                                 string PreviewFile = "");

    /// <summary>本地素材库根目录：<c>%APPDATA%\WarbandStudio\uilibrary\{bg,btn,other}</c>。</summary>
    public static string UiLibraryDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WarbandStudio", "uilibrary");

    private static string UiKindOf(string fileName)
    {
        var low = fileName.ToLowerInvariant();
        if (low.StartsWith("background_images_")) return "bg";
        if (low.Contains("button")) return "btn";
        return "other";
    }

    /// <summary>素材的分类中文名（界面分组用）。</summary>
    public static string UiKindLabel(string kind) => kind switch
    {
        "bg" => "背景图", "btn" => "按钮图", _ => "其他 UI",
    };

    /// <summary>
    /// 本地 UI 素材库：
    ///   · 第一次用**随工具内置的素材**铺一遍（WUU + 雪乃的 UI，已按内容去重；只补缺、不覆盖用户改过的）；
    ///   · 之后一直用本地目录里的文件 —— 不再实时读当前包（包里看到的图要进素材库得"导入"或者右键加）；
    ///   · 预览图会抽一份到皮肤缓存给画布/列表用。
    /// </summary>
    public List<UiAsset> UiLibrary()
    {
        var list = new List<UiAsset>();
        try
        {
            SeedUiLibrary();
            var removed = RemovedNames();
            var skins = Skins(_pack?.Archive, _pack?.PackPath);
            foreach (var kind in new[] { "bg", "btn", "other" })
            {
                var dir = Path.Combine(UiLibraryDir, kind);
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.GetFiles(dir, "*.png"))
                {
                    var name = Path.GetFileName(f);
                    if (removed.Contains(name)) { TryDelete(f); continue; }     // 用户删过的：不再出现（也顺手再删一次）
                    var cacheName = "uilib_" + name;
                    var url = skins.StageLocalFile(f, cacheName);
                    // 预览绑**缓存里的副本**（WPF 的 Image 会把文件锁住 → 之前"移除失败/改名失败"就是被它锁的）
                    var preview = Path.Combine(skins.CacheDir, cacheName);
                    list.Add(new UiAsset(kind, name, f, url ?? "", UiPackPathOf(name), new FileInfo(f).Length, preview));
                }
            }
            list.Sort((a, b) =>
            {
                var ka = Array.IndexOf(["bg", "btn", "other"], a.Kind);
                var kb = Array.IndexOf(["bg", "btn", "other"], b.Kind);
                return ka != kb ? ka - kb : string.CompareOrdinal(a.Name, b.Name);
            });
            Log?.Invoke($"UI 素材库（本地）：{list.Count} 张（背景 {list.Count(x => x.Kind == "bg")} / 按钮 {list.Count(x => x.Kind == "btn")} / 其他 {list.Count(x => x.Kind == "other")}）");
        }
        catch (Exception ex) { Log?.Invoke("UI 素材库读取失败：" + ex.Message); }
        return list;
    }

    /// <summary>这张素材写进包时用的位置（背景/按钮都放 warband_upgrades 下，跟 WUU/雪乃的摆法一致）。</summary>
    public static string UiPackPathOf(string fileName) =>
        $"ui/skins/default/warband_upgrades/{fileName}";

    /// <summary>把内置素材（bundled/uilibrary）**补缺**到本地目录（不覆盖已有同名文件，用户的导入不会被冲掉）。</summary>
    private void SeedUiLibrary()
    {
        var src = Path.Combine(AppContext.BaseDirectory, "bundled", "uilibrary");
        if (!Directory.Exists(src)) return;
        var removed = RemovedNames();      // 用户删过的内置素材别又铺回来
        // **版本号**：内置素材升级（改名规则变了/补了新图）时，用 bundle 覆盖同名文件，
        // 而不是只在"缺文件"时补 —— 否则本地那份旧的就是不复原（用户实测过这个问题）。
        // 用户**导入的**（bundle 里没有的名字）一律保留。
        var verFile = Path.Combine(UiLibraryDir, "_version.txt");
        var bundleVer = File.Exists(Path.Combine(src, "_version.txt"))
            ? File.ReadAllText(Path.Combine(src, "_version.txt")).Trim() : "1";
        var localVer = File.Exists(verFile) ? File.ReadAllText(verFile).Trim() : "";
        var upgrade = !string.Equals(bundleVer, localVer, StringComparison.Ordinal);
        var added = 0;
        var updated = 0;
        foreach (var kind in new[] { "bg", "btn", "other" })
        {
            var s = Path.Combine(src, kind);
            if (!Directory.Exists(s)) continue;
            var d = Path.Combine(UiLibraryDir, kind);
            Directory.CreateDirectory(d);
            foreach (var f in Directory.GetFiles(s, "*.png"))
            {
                var name = Path.GetFileName(f);
                if (removed.Contains(name)) continue;              // 用户删过的 → 不铺回来
                var target = Path.Combine(d, name);
                if (File.Exists(target))
                {
                    if (!upgrade) continue;                        // 平时：本地已有同名 → 保留本地的
                    File.Copy(f, target, overwrite: true);         // 版本升级：内置为准，覆盖同名
                    updated++;
                    continue;
                }
                File.Copy(f, target);
                added++;
            }
        }
        if (upgrade || added > 0 || updated > 0)
        {
            try { Directory.CreateDirectory(UiLibraryDir); File.WriteAllText(verFile, bundleVer); } catch { }
            Log?.Invoke($"UI 素材库：内置素材 v{bundleVer}（新铺 {added} 张，覆盖同名 {updated} 张；" +
                        "同名同内容的只留一份、同名不同内容的雪乃版带 _1 后缀）");
        }
    }

    /// <summary>导入素材：把选中的 PNG 复制进本地素材库（按文件名分到 背景/按钮/其他）。返回导入数量。</summary>
    /// <summary>
    /// 导入素材（**第一次重命名**）：复制进本地素材库，并按类型改成中性名字
    /// `background_images_new_added&lt;N&gt;.png` / `button_upgrade_new_added&lt;N&gt;.png`（N 取第一个空号，不覆盖已有的）。
    /// 用到某个页签上时再做第二次重命名（<see cref="CanonicalizeArt"/>）。
    /// </summary>
    public int ImportUiAssets(IEnumerable<string> files, string kind)
    {
        if (kind != "bg" && kind != "btn") kind = "other";
        var prefix = kind == "bg" ? "background_images_new_added" : kind == "btn" ? "button_upgrade_new_added" : "ui_new_added";
        var n = 0;
        foreach (var f in files)
        {
            try
            {
                if (!File.Exists(f) || !f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) continue;
                var dir = Path.Combine(UiLibraryDir, kind);
                Directory.CreateDirectory(dir);
                var target = UniquePath(dir, prefix);                 // 同名不覆盖：第二个往后自动加号（以前同名会被覆盖 → "导入第二张没出现"）
                File.Copy(f, target);
                // 这个名字以前被"移除"过（记在 removed.txt）→ 划掉它，不然列表会跳过（用户实测：导入成功但看不到）
                DropRemovedName(Path.GetFileName(target));
                Log?.Invoke($"素材库导入：{Path.GetFileName(f)} → {Path.GetFileName(target)}（{UiKindLabel(kind)}；" +
                            "用到页签上时会自动改成 <前缀><页签key>.png）");
                n++;
            }
            catch (Exception ex) { Log?.Invoke($"素材库导入失败 {Path.GetFileName(f)}：{ex.Message}"); }
        }
        return n;
    }

    private static string UniquePath(string dir, string prefix)
    {
        for (var i = 1; ; i++)
        {
            var p = Path.Combine(dir, $"{prefix}{i}.png");
            if (!File.Exists(p)) return p;
        }
    }

    /// <summary>这个本地文件是不是素材库里的（只有库里的才做重命名/移动）。</summary>
    private static bool IsInUiLibrary(string localFile)
    {
        try
        {
            var full = Path.GetFullPath(localFile);
            return full.StartsWith(Path.GetFullPath(UiLibraryDir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>用户删过的素材名单（`uilibrary/removed.txt`）：铺内置素材时跳过、加载时再删一次，删不掉也不会"复活"。</summary>
    private static HashSet<string> RemovedNames()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var f = Path.Combine(UiLibraryDir, "removed.txt");
            if (File.Exists(f))
                foreach (var ln in File.ReadAllLines(f))
                    if (ln.Trim().Length > 0) set.Add(ln.Trim());
        }
        catch { }
        return set;
    }

    private static void AddRemovedName(string name)
    {
        try
        {
            Directory.CreateDirectory(UiLibraryDir);
            var f = Path.Combine(UiLibraryDir, "removed.txt");
            var all = RemovedNames();
            if (all.Add(name)) File.AppendAllLines(f, new[] { name });
        }
        catch { }
    }

    /// <summary>把这个名字从"已删除名单"里划掉（重新导入同名素材时要划掉，否则列表会跳过它 → 看着像导入没生效）。</summary>
    private static void DropRemovedName(string name)
    {
        try
        {
            var f = Path.Combine(UiLibraryDir, "removed.txt");
            if (!File.Exists(f)) return;
            var keep = File.ReadAllLines(f).Where(l => !l.Trim().Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
            File.WriteAllLines(f, keep);
        }
        catch { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 还锁着就下次再删 */ }
    }

    /// <summary>
    /// 这次编辑**会动到的包内文件**（文件树给它们打红标，像 RPFM 的"已修改"标记）：
    ///   · 覆盖表：`db/&lt;表&gt;/studio_edits`（有新增/改值）/ `studio_layout`（坐标、页签）/ `studio_elite_unlock`（解锁）；
    ///   · **删行会重写原文件** → 该表在包里现有的文件都算（这些表文件不多，宁可标全）；
    ///   · 换图/加素材的目标文件、新建/重命名页签涉及的 twui 与两张图。
    /// </summary>
    public HashSet<string> EditedFiles()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var pack = _pack?.Archive;
            if (pack is null) return set;
            const string EditFileName = WarbandAmender.EditFileName;      // studio_edits
            const string LayoutFileName = WarbandAmender.LayoutFileName;  // studio_layout
            void Studio(string table, string file) => set.Add($"db/{table}/{file}");
            // **只标真的会被改到的那个文件**：删行是"改原文件"，所以逐个文件解码、看它里面有没有被删的行
            // （以前整张表全标红，"我只改了一部分却全红"就是这么来的）
            void DropFiles(string table, Func<DbTable, bool> anyDropped)
            {
                foreach (var f in TableFiles.EntriesFor(pack, table))
                {
                    try
                    {
                        var t = DbTable.Decode(pack.ReadDecoded(f), table, GetSchema());
                        if (anyDropped(t)) set.Add(f.Path.Replace(Path.DirectorySeparatorChar, '/'));
                    }
                    catch { }
                }
            }
            string CellOf(DbTable t, string col, int row)
            {
                var c = t.Columns.FindIndex(x => x.Name.Equals(col, StringComparison.OrdinalIgnoreCase));
                return c < 0 ? "" : t.Rows[row][c].ToTsv();
            }
            const string Junc = "unit_to_unit_group_junctions_tables";
            const string Groups = "unit_upgrade_groups_tables";
            const string Infos = "unit_upgrade_group_ui_infos_tables";
            const string Routes = "unit_upgrade_to_unit_groups_tables";
            const string Links = "unit_upgrade_group_ui_links_tables";
            const string Cats = "unit_upgrade_group_ui_categories_tables";
            if (Edits.AddJunction.Count > 0 || Edits.RemoveJunction.Count > 0) Studio(Junc, EditFileName);
            var dropJ = KeySet(Edits.RemoveJunction.Select(x => x.Unit + "" + x.Group));
            if (dropJ.Count > 0)
                DropFiles(Junc, t => Enumerable.Range(0, t.Rows.Count)
                    .Any(i => dropJ.Contains(CellOf(t, "unit", i) + "" + CellOf(t, "unit_group", i))));
            if (Edits.AddGroup.Count > 0) Studio(Groups, EditFileName);
            var dropG = KeySet(Edits.RemoveGroup);
            if (dropG.Count > 0)
            {
                DropFiles(Groups, t => Enumerable.Range(0, t.Rows.Count).Any(i => dropG.Contains(CellOf(t, "unit_group", i))));
                DropFiles(Junc, t => Enumerable.Range(0, t.Rows.Count).Any(i => dropG.Contains(CellOf(t, "unit_group", i))));
                DropFiles(Links, t => Enumerable.Range(0, t.Rows.Count)
                    .Any(i => dropG.Contains(CellOf(t, "child_key", i)) || dropG.Contains(CellOf(t, "parent_key", i))));
                DropFiles(Routes, t => Enumerable.Range(0, t.Rows.Count)
                    .Any(i => dropG.Contains(CellOf(t, "base_unit_group", i)) || dropG.Contains(CellOf(t, "target_unit_group", i))));
                DropFiles(Infos, t => Enumerable.Range(0, t.Rows.Count).Any(i => dropG.Contains(CellOf(t, "unit_upgrade_group", i))));
            }
            if (Edits.InfoEdits.Count > 0)
            {
                Studio(Infos, LayoutFileName);
                // 坐标/页签是**就地改原文件**的（覆盖表抢不过 MOD 自己的文件，见 WarbandAmender 步骤 3）——
                // 所以"包里已经有这一行、值又不一样"的文件也要标红
                foreach (var f in TableFiles.EntriesFor(pack, Infos))
                {
                    try
                    {
                        var t = DbTable.Decode(pack.ReadDecoded(f), Infos, GetSchema());
                        var kc = t.Columns.FindIndex(c => c.Name.Equals("unit_upgrade_group", StringComparison.OrdinalIgnoreCase));
                        if (kc < 0) continue;
                        var hit = false;
                        for (var i = 0; i < t.Rows.Count && !hit; i++)
                        {
                            if (!Edits.InfoEdits.TryGetValue(t.Rows[i][kc].ToTsv(), out var w)) continue;
                            hit = CellOf(t, "x", i) != w.X.ToString() || CellOf(t, "y", i) != w.Y.ToString()
                                  || (w.Category is not null && !CellOf(t, "category", i).Equals(w.Category, StringComparison.OrdinalIgnoreCase));
                        }
                        if (hit) set.Add(f.Path.Replace(Path.DirectorySeparatorChar, '/'));
                    }
                    catch { }
                }
            }
            if (Edits.AddRoute.Count > 0) Studio(Routes, EditFileName);
            var dropR = KeySet(Edits.RemoveRoute);
            if (dropR.Count > 0) DropFiles(Routes, t => Enumerable.Range(0, t.Rows.Count).Any(i => dropR.Contains(CellOf(t, "upgrade_key", i))));
            if (Edits.AddLink.Count > 0) Studio(Links, EditFileName);
            var dropL = KeySet(Edits.RemoveLink.Select(x => x.Child + "" + x.Parent));
            if (dropL.Count > 0)
                DropFiles(Links, t => Enumerable.Range(0, t.Rows.Count)
                    .Any(i => dropL.Contains(CellOf(t, "child_key", i) + "" + CellOf(t, "parent_key", i))));
            if (Edits.AddCost.Count > 0) Studio("resource_costs_tables", EditFileName);
            if (Edits.AddUnitGroup.Count > 0) Studio("units_to_groupings_military_permissions_tables", EditFileName);
            var dropU = KeySet(Edits.RemoveUnitGroup.Select(x => x.Unit + "" + x.MilitaryGroup));
            if (dropU.Count > 0)
                DropFiles("units_to_groupings_military_permissions_tables", t => Enumerable.Range(0, t.Rows.Count)
                    .Any(i => dropU.Contains(CellOf(t, "unit", i) + "" + CellOf(t, "military_group", i))));
            if (Edits.UnlockXp.Count > 0) Studio("main_units_tables", WarbandExporter.UnlockTableFileName);
            if (Edits.NewTabs.Count > 0 || Edits.TabRenames.Count > 0)
            {
                Studio(Cats, EditFileName);
                var twui = pack.VisibleEntries.FirstOrDefault(x => x.Path.EndsWith("warband_upgrades.twui.xml", StringComparison.OrdinalIgnoreCase));
                if (twui is not null) set.Add(twui.Path.Replace(Path.DirectorySeparatorChar, '/'));
            }
            foreach (var (key, _, _, _) in Edits.NewTabs)
                foreach (var kind in new[] { "background_images_", "button_upgrade_" })
                    set.Add($"ui/skins/default/warband_upgrades/{kind}{key.ToLowerInvariant()}.png");
            foreach (var (oldk, newk) in Edits.TabRenames)
                foreach (var kind in new[] { "background_images_", "button_upgrade_" })
                    set.Add($"ui/skins/default/warband_upgrades/{kind}{newk.ToLowerInvariant()}.png");
            foreach (var (target, _) in Edits.FileReplacements) set.Add(target.Replace(Path.DirectorySeparatorChar, '/'));
        }
        catch { }
        return set;
    }

    /// <summary>重命名素材库里的文件（前缀按类型保留，后缀做安全过滤；同名拒绝）。返回给用户看的一行说明。</summary>
    public string RenameUiAsset(UiAsset a, string suffix)
    {
        try
        {
            var sfx = new string((suffix ?? "").Where(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-').ToArray()).ToLowerInvariant();
            if (sfx.Length == 0) return "重命名：名字不能为空（只能用字母/数字/_/-）";
            var prefix = a.Kind == "btn" ? "button_upgrade_" : a.Kind == "bg" ? "background_images_" : "ui_";
            var dir = Path.GetDirectoryName(a.File)!;
            var target = Path.Combine(dir, prefix + sfx + ".png");
            if (string.Equals(target, a.File, StringComparison.OrdinalIgnoreCase)) return "重命名：名字没变";
            if (File.Exists(target)) return $"重命名：素材库里已经有 {prefix}{sfx}.png 了，换个名字";
            File.Move(a.File, target);
            Log?.Invoke($"素材库重命名：{a.Name} → {Path.GetFileName(target)}");
            return $"已重命名：{Path.GetFileName(target)}";
        }
        catch (Exception ex)
        {
            Log?.Invoke($"素材库重命名失败 {a.Name}：{ex.Message}");
            return $"重命名失败：{ex.Message}（文件可能还被预览占用，稍后再试）";
        }
    }

    /// <summary>从素材库移除（只删本地那份，不动任何包）。</summary>
    public bool RemoveUiAsset(UiAsset a)
    {
        // **直接从素材库删除**；只有删不掉（文件还被占用）才退一步记名字，下次加载再删
        TryDelete(a.File);
        var ok = !File.Exists(a.File);
        if (ok) DropRemovedName(a.Name);        // 删掉了就顺便把以前的名单记录划掉
        else AddRemovedName(a.Name);
        Log?.Invoke(ok ? $"素材库移除：{a.Name}（已删除）"
                       : $"素材库移除：{a.Name}（文件还被占用，已记入 removed.txt，下次启动再删）");
        return true;
    }

    /// <summary>把素材库里的这张图**加进当前编辑的包**（按 <see cref="UiAsset.PackPath"/>；导出时无则新增、有则覆盖）。不动画布。</summary>
    public string AddUiAssetToPack(UiAsset a)
    {
        if (a is null || string.IsNullOrWhiteSpace(a.File)) return "";
        // **不再让用户手输名字**：直接在素材自己的 key 后面加 _1 / _2 / …，取第一个没被占用的
        // （包里的 + 待导出里的都算占用）。为什么必须加后缀：包里常常已经有同名的 mod 图
        // （`background_images_skv.png` 这类），同名写进去会把 mod / 别的页签**正在用**的那张顶掉。
        // 真正"给页签用"的名字由**换图**在应用时决定（写成 `background_images_<页签key>.png`）——
        // 那一步才按页签 key 命名；这里的 _1 是"先安全地放进包"，想改名用文件树右键「重命名」。
        var prefix = a.Kind == "btn" ? "button_upgrade_" : a.Kind == "bg" ? "background_images_" : "ui_";
        var key = Path.GetFileNameWithoutExtension(a.File);
        if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) key = key[prefix.Length..];
        // **自动命名 = 名字带内容指纹**（用户口径：同名不同图的文件靠 `@指纹` 一眼区分，永远不互相串）；
        // **同内容重复添加直接跳过**（不再往包里塞第二份一样的图）。
        var fp = "";
        try
        {
            using var md5 = System.Security.Cryptography.MD5.Create();
            using var fs = File.OpenRead(a.File);
            fp = Convert.ToHexString(md5.ComputeHash(fs))[..6].ToLowerInvariant();
        }
        catch { }
        if (fp.Length == 0) fp = DateTime.Now.ToString("HHmmss");     // 读不到文件就退回时间戳，保证不撞名
        var target = $"ui/skins/default/warband_upgrades/{prefix}{key}@{fp}.png";
        if (ArtTargetExists(target))
        {
            Log?.Invoke($"UI 素材：{a.Name} 与包里已有的 {target} **内容相同**（指纹 {fp}）→ 跳过，不重复添加。");
            return target;
        }
        Edits.FileReplacements.Add((target, a.File));
        InvalidateCanvas();
        Log?.Invoke($"UI 素材：{a.Name} → {target}（自动命名带内容指纹 @{fp}；要用到页签上走「换图」，那一步才按页签 key 命名）　待导出");
        return target;
    }

    /// <summary>
    /// 这张包内 ui 图是不是某个页签**正在用**的（按文件名认：`background_images_<key>.png` / `button_upgrade_<key>.png`）。
    /// 返回那个页签 key（包里的大小写），没人用就返回空。
    /// 换图对话框拿它给格子打「在用」标记：选这种图给**别的**页签换图时会**复制一张**，绝不改原来那张。
    /// 注意 `_1`/`_2` 后缀的（素材库加包自动命名出来的）不算"在用" —— 它们就是给换图当素材的。
    /// </summary>
    /// <summary>
    /// "图 → 现在是谁在用"（换图面板的「在用」标记）。
    /// **按当前状态算，不按文件名死认**：每个页签"在用的文件" = 它**实际引用的那个文件**
    /// （ArtInnerOf：twui 里的名字 / 待导出新建的图；改名后自动跟着新名）。
    /// 换图只把来源的**内容**写进这个文件、**文件名不变** → 标记留在引用文件上；改名/重建后引用变了，
    /// 旧文件的标记自动撤销，不会留下"已经不用的图还挂着 🔒"。同一张图被多个页签用 → 写成 "SKV、SKG"。
    /// （v1.4.2 修正：曾经标"最后一次换图的**来源**"，结果和对话框"保持当前"（= 引用文件）的口径打架
    /// —— 用户实测："查看当前用了两个图"，一个换图前的、一个所选的，都像在用。）
    /// </summary>
    private Dictionary<string, List<string>> ArtSuppliers(string prefix, List<string> knownTabs)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        void Take(string image, string tab)
        {
            if (string.IsNullOrWhiteSpace(image)) return;
            if (!map.TryGetValue(image, out var l)) map[image] = l = [];
            if (!l.Contains(tab, StringComparer.OrdinalIgnoreCase)) l.Add(tab);
        }
        try
        {
            var pack = _pack?.Archive;
            foreach (var t in knownTabs)
            {
                // **twui 里这个页签实际引用的那个文件**（不是约定名 —— 改名会带 _N，老遗留可能叫别的 key）
                var target = ArtInnerOf(prefix, t);
                // 「在用」= **页签引用的那个文件**（§7.96 的"twui 名字"口径）。换图只把来源的**内容**写进
                // 这个文件、**文件名不变** → 标记不该跳到来源上：否则换图后"保持当前"（= 引用文件）和
                // 素材列表里的「本页在用」（= 来源）会显示成两张不同的图（用户实测："查看当前用了两个图"）。
                // 本轮新写的（换图目标 / 新建页签的图）包里还没有 → 靠 FileReplacements 认它算存在。
                if (pack?.Find(target) is not null
                    || Edits.FileReplacements.Any(x => x.Target.Equals(target, StringComparison.OrdinalIgnoreCase)))
                    Take(target, t);
            }
        }
        catch { }
        return map;
    }

    /// <summary>twui 文本（当前包的）。解析"页签实际用的图"用它 —— 一次解析缓存一份，别每次重建画布都读 780KB。</summary>
    private string? TwuiText()
    {
        var pack = _pack?.Archive;
        if (pack is null) return null;
        var e = pack.VisibleEntries.FirstOrDefault(x =>
            x.Path.EndsWith("warband_upgrades.twui.xml", StringComparison.OrdinalIgnoreCase));
        if (e is null) return null;
        try { return System.Text.Encoding.UTF8.GetString(pack.ReadDecoded(e)); }
        catch { return null; }
    }

    private Dictionary<string, TwuiTabs.TabArt>? _tabArtMap;
    private PackArchive? _tabArtPack;
    /// <summary>页签 → 图（twui 解析，按 Archive 实例缓存；存包会换实例 → 自动重解析）。</summary>
    private Dictionary<string, TwuiTabs.TabArt> TabArtMap()
    {
        var pack = _pack?.Archive;
        if (_tabArtMap is not null && ReferenceEquals(_tabArtPack, pack)) return _tabArtMap;
        _tabArtPack = pack;
        return _tabArtMap = TwuiTabs.Parse(TwuiText());
    }

    /// <summary>有**待导出改名**时，包里还是旧 key（改名导出时才落盘）→ 解析要按旧 key 找。</summary>
    private string PackSideKey(string category)
    {
        var k = category;
        for (var i = 0; i < 8; i++)
        {
            var prev = Edits.TabRenames.Where(x => x.New.Equals(k, StringComparison.OrdinalIgnoreCase))
                                       .Select(x => x.Old).LastOrDefault();
            if (prev is null || prev.Equals(k, StringComparison.OrdinalIgnoreCase)) break;
            k = prev;
        }
        return k;
    }

    /// <summary>
    /// 这个页签的图**在包里的真实路径**：`ui/skins/default/warband_upgrades/&lt;twui 里实际引用的文件名&gt;`。
    /// 游戏看的是 twui 里那个名字（页签面板背景 = `&lt;states&gt;` 下按 key 命名的状态 → 那张图；
    /// 按钮图写在 `button_toggle_tab_*` 组件里）—— 写约定名 `&lt;前缀&gt;&lt;key&gt;.png` 就会出现
    /// "换了图页签还是旧的 / 在用标记指错文件"（用户实测）。解析不到才退回约定名。
    /// </summary>
    private string ArtInnerOf(string prefix, string category)
    {
        var key = PackSideKey(category);
        var bg = prefix.StartsWith("background_images_", StringComparison.OrdinalIgnoreCase);
        if (TabArtMap().TryGetValue(key, out var art))
        {
            var f = bg ? art.BgFile : art.BtnFile;
            if (!string.IsNullOrWhiteSpace(f)) return TwuiTabs.SkinDir + f;
        }
        // 这个页签在 twui 里**没有结构**（"建了一半"的页签：有 categories 行/组/图，就是没落 twui）——
        // 若本会话给它改过名，落表会**挪图 + 补全结构**（和落表共用 TwuiTabs.PlanRename）→
        // 这里按**计划名**解析，换图/在用标记才指得到那个页签真正会用的文件。
        if (Edits.TabRenames.Any(x => x.New.Equals(category, StringComparison.OrdinalIgnoreCase)))
        {
            var pack = _pack?.Archive;
            bool Taken(string n) =>
                pack?.Find(TwuiTabs.SkinDir + n) is not null
                || Edits.FileReplacements.Any(f => f.Target.Equals(TwuiTabs.SkinDir + n, StringComparison.OrdinalIgnoreCase));
            // "正名优先"要看占用者能不能让位：判据和 Amender 一致 —— 名字在 twui 文本里出现 = 有引用、不让位。
            var xml = TwuiText();
            bool Evictable(string n) => xml is null || xml.IndexOf(n, StringComparison.OrdinalIgnoreCase) < 0;
            var (name, _, _) = TwuiTabs.PlanRename(prefix, category, Taken, Evictable);
            return TwuiTabs.SkinDir + name;
        }
        var scan = TwuiTabs.ScanName(TwuiText(), prefix, key);
        return TwuiTabs.SkinDir + (scan ?? TwuiTabs.ConventionName(prefix, key));
    }

    /// <summary>某个页签现在显示的图是谁（没换图就是它自己的画布文件）。给 SetTabArt 写日志/查占用用。</summary>
    private List<string> TabsUsing(string image)
    {
        if (string.IsNullOrWhiteSpace(image)) return [];
        var tabs = TabKeys();
        foreach (var (nk, _, _, _) in Edits.NewTabs)
            if (!tabs.Contains(nk, StringComparer.OrdinalIgnoreCase)) tabs.Add(nk);
        foreach (var prefix in new[] { "background_images_", "button_upgrade_" })
            if (map2(prefix).TryGetValue(image, out var l)) return l;
        return [];

        Dictionary<string, List<string>> map2(string prefix) => ArtSuppliers(prefix, tabs);
    }

    /// <summary>
    /// 来源图和目标位置现在的内容是不是同一份字节（是就别再记一条"换图"了，省得包里白白多一次写入）。
    /// 来源可以是包内路径，也可以是本地素材文件。
    /// </summary>
    private bool SameArt(string source, string target)
    {
        try
        {
            var pack = _pack?.Archive;
            if (pack is null) return false;
            var t = pack.Find(target);
            if (t is null) return false;
            var src = pack.Find(source) ?? pack.VisibleEntries.FirstOrDefault(x =>
                string.Equals(x.Path.Replace('/', Path.DirectorySeparatorChar),
                              source.Replace('/', Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase));
            byte[]? a = src is not null ? pack.ReadDecoded(src) : (File.Exists(source) ? File.ReadAllBytes(source) : null);
            if (a is null) return false;
            var b = pack.ReadDecoded(t);
            return a.Length == b.Length && a.AsSpan().SequenceEqual(b);
        }
        catch { return false; }
    }

    /// <summary>文件树右键「删除」：待导出新增的 → 从替换列表里去掉；包内已有的 → 记进删除名单（导出时跳过）。</summary>
    public string RemovePackFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "路径为空";
        var n = Edits.FileReplacements.RemoveAll(x => x.Target.Equals(path, StringComparison.OrdinalIgnoreCase));
        var inPack = _pack?.Archive?.Find(path) is not null;
        if (inPack && !Edits.RemoveFiles.Contains(path, StringComparer.OrdinalIgnoreCase))
            Edits.RemoveFiles.Add(path);
        InvalidateCanvas();
        Log?.Invoke($"文件删除：{path}（{(inPack ? "包内条目：导出时跳过" : "待导出新增：已撤掉")}）　待导出");
        return $"已删除：{Path.GetFileName(path)}（{(inPack ? "导出时不再写入" : "撤掉了待导出")}）";
    }

    /// <summary>文件树右键「重命名」：待导出新增的 → 改替换目标；包内已有的 → 新名写一份 + 旧名删掉。</summary>
    public string RenamePackFile(string path, string newName)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(newName)) return "名字为空";
        var name = newName.Trim().Replace(Path.DirectorySeparatorChar, '/');
        var dir = path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
        var target = dir.Length > 0 ? dir + "/" + name : name;
        foreach (var i in Enumerable.Range(0, Edits.FileReplacements.Count)
                     .Where(i => Edits.FileReplacements[i].Target.Equals(path, StringComparison.OrdinalIgnoreCase))
                     .Reverse().ToList())
        {
            var (_, src) = Edits.FileReplacements[i];
            Edits.FileReplacements[i] = (target, src);                  // 待导出新增：直接改目标名
        }
        if (_pack?.Archive?.Find(path) is not null)
        {
            Edits.FileReplacements.Add((target, path));                    // 包内条目：新名 ← 旧条目的字节
            if (!Edits.RemoveFiles.Contains(path, StringComparer.OrdinalIgnoreCase)) Edits.RemoveFiles.Add(path);
        }
        InvalidateCanvas();
        Log?.Invoke($"文件重命名：{path} → {target}　待导出");
        return $"已重命名：{Path.GetFileName(path)} → {name}（导出时生效）";
    }

    private static HashSet<string> KeySet(IEnumerable<string> keys) => new(keys, StringComparer.OrdinalIgnoreCase);

    /// <summary>一对组的"无序"身份键（谁当 child 谁当 parent 都算同一对）——连线、画线调整都按它去重。</summary>
    private static string PairOf(string a, string b) =>
        string.Compare(a, b, StringComparison.OrdinalIgnoreCase) <= 0 ? a + PairSep + b : b + PairSep + a;

    /// <summary>这个目标路径现在包里/待导出里已经有了吗（「添加到当前包」填完名字后用来提醒重名）。</summary>
    public bool ArtTargetExists(string target)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(target)) return false;
            if (_pack?.Archive?.Find(target) is not null) return true;
            return Edits.FileReplacements.Any(x => x.Target.Equals(target, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    /// <summary>按类型前缀 + 用户后缀拼出目标路径（给重名提醒用，和 AddUiAssetToPack 同一套规则）。</summary>
    public static string UiTargetOf(string kind, string? suffix)
    {
        var sfx = new string((suffix ?? "").Where(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-').ToArray()).ToLowerInvariant();
        var prefix = kind == "btn" ? "button_upgrade_" : kind == "bg" ? "background_images_" : "ui_";
        return $"ui/skins/default/warband_upgrades/{prefix}{sfx}.png";
    }

    /// <summary>给自检/诊断用：这个页签**实际用的图**（包内路径，twui 解析出来的）。</summary>
    public string TabArtPathOf(string category, bool background) =>
        ArtInnerOf(background ? "background_images_" : "button_upgrade_", category);

    /// <summary>给自检/诊断用：**假设**改名成 <paramref name="newKey"/>，图会落在哪个文件（和落表共用同一套规则）。</summary>
    public string PlannedArtPathOf(string newKey, bool background)
    {
        var prefix = background ? "background_images_" : "button_upgrade_";
        var pack = _pack?.Archive;
        bool Taken(string n) =>
            pack?.Find(TwuiTabs.SkinDir + n) is not null
            || Edits.FileReplacements.Any(f => f.Target.Equals(TwuiTabs.SkinDir + n, StringComparison.OrdinalIgnoreCase));
        var xml = TwuiText();
        bool Evictable(string n) => xml is null || xml.IndexOf(n, StringComparison.OrdinalIgnoreCase) < 0;
        var (name, _, _) = TwuiTabs.PlanRename(prefix, newKey, Taken, Evictable);
        return TwuiTabs.SkinDir + name;
    }

    /// <summary>兼容旧调用（同路径替换）。</summary>
    public void AddUiAssetToPack(string inner)
    {
        if (string.IsNullOrWhiteSpace(inner)) return;
        Edits.FileReplacements.Add((inner, inner));
        InvalidateCanvas();
        Log?.Invoke($"UI 素材：{inner} 加进当前包（导出时落盘）　待导出");
    }

    /// <summary>给某个页签换背景图/按钮图（来源 = 素材库里的本地文件；写进包里对应条目）。</summary>
    public void SetTabArt(string category, string? bgSource, string? btnSource)
    {
        if (string.IsNullOrWhiteSpace(category)) return;
        var bg = bgSource;
        var btn = btnSource;
        // 目标 = **twui 里这个页签实际引用的那个文件**（页签面板背景 = twui states 下按 key 命名的状态那张图；
        // 老版本按约定名 `<前缀><key>.png` 写 → 改名后的页签根本不看这个文件，用户实测"换图没生效"）。
        var bgInner = ArtInnerOf("background_images_", category);
        var btnInner = ArtInnerOf("button_upgrade_", category);
        var bgName = Path.GetFileName(bgInner);
        var btnName = Path.GetFileName(btnInner);
        // 被别的页签用着的源图 → 这里只**复制一份**（写成目标页签自己的名字），绝不重命名/覆盖原来那张
        foreach (var (kind2, src) in new[] { ("背景", bg), ("按钮", btn) })
        {
            if (string.IsNullOrWhiteSpace(src) || File.Exists(src!)) continue;
            var owners = TabsUsing(src!).Where(t => !t.Equals(category, StringComparison.OrdinalIgnoreCase)).ToList();
            if (owners.Count > 0)
                Log?.Invoke($"换图：源图 {Path.GetFileName(src!)} 正被 {string.Join("、", owners)} 页签用着 → 只复制一张给 {category}，不动原来那张。");
        }
        // **换图顺带"正名"**（用户 2026-10-07 要的）：这个页签的图名和标准命名（<前缀><key 小写>.png）
        // 不一致（历史遗留：SKV2 页签用着 skvg.png）时，保存时顺手把图**挪成标准名**——走"同名改名"编辑
        // （TabRenames 记 (key, key)，Amender 6d 只挪图 + 对齐 twui，不动 categories/infos），
        // 撞名按"正名优先"让位规则处理。图被**别的页签**共用就不动它（免得影响别人）。
        var renamed = new List<string>();                          // 日志用
        var renamedKinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void MaybeRename(string label, string prefix, string inner)
        {
            var conv = TwuiTabs.ConventionName(prefix, category);
            if (Path.GetFileName(inner).Equals(conv, StringComparison.OrdinalIgnoreCase)) return;
            if (TabsUsing(inner).Any(t => !t.Equals(category, StringComparison.OrdinalIgnoreCase)))
            {
                Log?.Invoke($"换图：页签 {category} 的{label}图 {Path.GetFileName(inner)} 还被别的页签用着 → 图名保持不动" +
                            "（换图照旧写这个文件；名字不齐整不影响使用）。");
                return;
            }
            if (!Edits.TabRenames.Any(x => x.Old.Equals(category, StringComparison.OrdinalIgnoreCase)
                                        && x.New.Equals(category, StringComparison.OrdinalIgnoreCase)))
                Edits.TabRenames.Add((category, category));        // 同名"改名" = 只做图名对齐
            renamed.Add($"{label} {Path.GetFileName(inner)} → {conv}");
            renamedKinds.Add(label);
        }
        // 两张都检查 —— 落表 6d 一旦触发会把**两张**图都对齐正名（背景/按钮一起归齐），日志要如实；
        // 但"什么都没选就点应用"不触发，免得没换图也把文件名改了。
        if (!string.IsNullOrWhiteSpace(bg) || !string.IsNullOrWhiteSpace(btn))
        {
            MaybeRename("背景", "background_images_", bgInner);
            MaybeRename("按钮", "button_upgrade_", btnInner);
        }

        var did = new List<string>();
        foreach (var (label, src, inner, name) in new[] { ("背景", bg, bgInner, bgName), ("按钮", btn, btnInner, btnName) })
        {
            if (string.IsNullOrWhiteSpace(src)) continue;
            if (SameArt(src!, inner))
            {
                // 内容一样但**图名会顺带归正**时别说"没变化"——那条正名编辑还挂着（用户实测：
                // 选了一张内容相同的图 → 看着"没生效"，其实真正要的是把引用名换成正名）。
                Log?.Invoke(renamedKinds.Contains(label)
                    ? $"换图：页签 {category} 的{label} 选的那张和现在的内容一样 —— 不写内容，只把**图名**归正（保存时落盘）。"
                    : $"换图：页签 {category} 的{label} 选的那张和页签现在用的内容一样 → 没变化，不记改动。");
                continue;
            }
            Edits.FileReplacements.Add((inner, src!));
            did.Add($"{label} ← {Path.GetFileName(src!)}（写到 {name}）");
        }
        if (renamed.Count > 0)
            Log?.Invoke($"换图：页签 {category} 的图名和页签 key 不一致（历史遗留）→ 保存时会一并改成标准命名：" +
                        string.Join("；", renamed) + "（撞名按「正名优先」让位，不覆盖别人的图）。");
        // 画布预览：先清掉这个页签的旧缓存（否则会显示上一次/别处留下的图），再把来源图抽成**实际用的名字**
        try
        {
            var skins = Skins(_pack?.Archive, _pack?.PackPath);
            foreach (var n in new[] { bgName, btnName }) skins.DropCache(n);
            // 来源有两种：包内路径（老用法）或**本地素材文件**（素材库那份）——预览都要出得来。
            // 包内来源必须走 StageFromPack：把来源键记成 `local:pack:<目标>`，不然重推画布时
            // Prepare 会拿包里还没导出的旧图把这份预览顶掉（用户实测："换图后画布背景没变化"）。
            string? Stage(string srcPath, string cacheName, string targetInner) =>
                File.Exists(srcPath) ? skins.StageLocalFile(srcPath, cacheName)
                                     : skins.StageFromPack(srcPath, targetInner, cacheName);
            if (!string.IsNullOrWhiteSpace(bg)) Stage(bg!, bgName, bgInner);
            if (!string.IsNullOrWhiteSpace(btn)) Stage(btn!, btnName, btnInner);
        }
        catch (Exception ex) { Log?.Invoke("换图预览失败：" + ex.Message); }
        InvalidateCanvas();
        // **可见提示**（用户报过"换了图上方一点动静都没有"）：改了哪几张、写到哪个文件、要保存才生效
        if (did.Count > 0)
        {
            Log?.Invoke($"换图：页签 {category} 已改 {did.Count} 处 —— {string.Join("；", did)}　" +
                        "待导出（保存/导出后游戏里才生效）；页签原来那张会自动保留成 _1（不覆盖、不丢）");
            var bgConv = TwuiTabs.ConventionName("background_images_", category);
            var btnConv = TwuiTabs.ConventionName("button_upgrade_", category);
            if (renamed.Count == 0 &&
                ((bg is not null && !bgName.Equals(bgConv, StringComparison.OrdinalIgnoreCase)) ||
                 (btn is not null && !btnName.Equals(btnConv, StringComparison.OrdinalIgnoreCase))))
                Log?.Invoke($"提示：页签 {category} 在 twui 里用的图名是 {bgName} / {btnName}（和页签 key 不一致，多半是以前改名的遗留）" +
                            "—— 换图已按**实际文件名**写，游戏里会生效；名字不齐整不影响使用。");
        }
        else
        {
            Log?.Invoke(renamed.Count > 0
                ? $"换图：页签 {category} 内容没变 —— 只把图名改成标准命名（见上一行）　待导出（保存后生效）。"
                : $"换图：页签 {category} 没有任何改动（没选图，或选的那张和现在这张一样）。");
        }
        // 换图只换"这张图"；页签本身要存在才算数 —— 包里没有 categories 行 / twui 块时，
        // 换了图游戏里也不会出现这个页签（用户实测：SKVG 建过一回但结构没落进包 → 进游戏页签直接没有）。
        // （本次要顺带"正名"的页签会走 6d：没结构时顺手按母版补全一整套 → 就不用报这条了。）
        if (!TabHasStructure(category))
            Log?.Invoke(renamed.Count > 0
                ? $"页签 {category} 在 twui 里还没有结构 —— 保存时会顺手补全（按母版克隆一整套），游戏里就能显示了。"
                : $"⚠ 页签 {category} 在包里没有结构（categories 行 / twui 块都没有）——游戏里这个页签不会显示。" +
                  $"要用它请先「新建页签」，key 填 {category}（已经放到这一页的组会自动归到它底下）。");
    }

    /// <summary>自检（--ui-selftest）⑭：换图**预览缓存**回归 —— 来源选**包内**的图时，抽成目标名之后
    /// **重推一遍画布数据（会跑 Prepare）**，缓存内容必须还是**来源**那张。
    /// 曾经的 bug：预览把来源键记成"包内来源路径"→ Prepare 请求目标文件时不匹配 →
    /// 又拿包里还没导出的旧图抽回来（用户实测："换图后画布背景没变化 / 保持当前还是旧内容"）。</summary>
    public string SelfTestSwapPreview()
    {
        try
        {
            var pack = _pack?.Archive;
            if (pack is null) return "(包没开)";
            // 目标页签必须**在画布 pages 里**（有组在用）：Prepare 只抽 pages 里的页签，
            // **空页签根本不会被抽**、也就照不出"被顶掉"（第一版自检挑到空页签 ART，白测）。
            var pages = new List<string>();
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(BuildWarbandJson());
                if (doc.RootElement.TryGetProperty("pages", out var pg))
                    foreach (var p in pg.EnumerateArray())
                        if (p.GetString() is { Length: > 0 } s) pages.Add(s);
            }
            catch { }
            if (pages.Count == 0) return "(画布没有页签，测不了)";
            var bgFiles = pack.VisibleEntries.Select(x => x.Path.Replace((char)92, '/'))
                .Where(p => p.StartsWith(TwuiTabs.SkinDir + "background_images_", StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFileName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            string? cat = null, targetName = null;
            foreach (var t in pages)
            {
                var n = Path.GetFileName(ArtInnerOf("background_images_", t));
                if (bgFiles.Contains(n, StringComparer.OrdinalIgnoreCase)) { cat = t; targetName = n; break; }
            }
            if (cat is null || targetName is null) return "(没有'目标图在包里'的页签，测不了)";
            // 来源 = 包里**另一张、内容不同**的背景图（内容相同会被 SameArt 跳过，测不到东西）
            var targetBytes = pack.Find(TwuiTabs.SkinDir + targetName) is { } te ? pack.ReadDecoded(te) : null;
            string? src = null; byte[]? srcBytes = null;
            foreach (var n in bgFiles.Where(n => !n.Equals(targetName, StringComparison.OrdinalIgnoreCase)))
            {
                if (pack.Find(TwuiTabs.SkinDir + n) is not { } e2) continue;
                var b2 = pack.ReadDecoded(e2);
                if (targetBytes is null || !b2.AsSpan().SequenceEqual(targetBytes)) { src = n; srcBytes = b2; break; }
            }
            if (src is null) return "(包里没有'和它内容不同'的第二张背景图，测不了)";
            SetTabArt(cat, TwuiTabs.SkinDir + src, null);            // 换图：预览暂存 + 编辑集
            _ = BuildWarbandJson();                                  // **重推画布**（内部跑 Prepare —— 就是曾经顶掉预览的那一步）
            var skins = Skins(pack, _pack?.PackPath);
            var cacheFile = Path.Combine(skins.CacheDir, targetName);
            var cached = File.Exists(cacheFile) ? File.ReadAllBytes(cacheFile) : null;
            var ok = srcBytes is not null && cached is not null && cached.AsSpan().SequenceEqual(srcBytes);
            return $"页签 {cat}：来源 {src} → 抽成 {targetName}，重推画布后缓存 " +
                   (ok ? "= 来源内容 ✓（没被包里旧图顶掉）" : "✗ 被顶掉了（回归！）");
        }
        catch (Exception ex) { return "(换图预览自检失败：" + ex.Message + ")"; }
    }

    /// <summary>自检（--ui-selftest）⑮：新组命名规则（`<前缀>_<页签>_<兵种词…>`；多兵依次接；撞名 _2 去重；
    /// 没配前缀退回旧时间戳名）。临时借用真实 settings 的前缀字段算例子，**finally 里还原**。</summary>
    public string SelfTestGroupNaming()
    {
        var save = ProjectKey;
        try
        {
            ProjectKey = "Yukino";
            var one = MakeGroupKey("SKV", new[] { "wh2_main_skv_inf_clanrat_1" }, "(旧式)");
            var many = MakeGroupKey("SKV", new[] { "wh2_main_skv_inf_clanrat_1", "Yukino_Skv_Inf_Night_Runners" }, "(旧式)");
            var empty = MakeGroupKey("SKV", Array.Empty<string>(), "(旧式)");
            ProjectKey = "";
            var none = MakeGroupKey("SKV", new[] { "wh2_main_skv_inf_clanrat_1" }, "(旧式)");
            return $"单兵 {one}；多兵 {many}；空组 {empty}；没配前缀 {none}";
        }
        finally { ProjectKey = save; }
    }

    /// <summary>这个页签 key 在"包 + 待导出"里有没有结构（本会话新建的页签 / categories 行 / twui holder_tab）。</summary>
    private bool TabHasStructure(string category)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(category)) return true;
            if (Edits.NewTabs.Any(t => t.Key.Equals(category, StringComparison.OrdinalIgnoreCase))) return true;
            var pack = _pack?.Archive;
            if (pack is null) return true;                     // 没包就不啰嗦
            var cats = TableFiles.ReadConcat(pack, "unit_upgrade_group_ui_categories_tables", GetSchema(), null);
            if (cats is not null)
            {
                var c = Col(cats, "category");
                if (c < 0) c = 0;
                if (c < cats.Columns.Count)
                    foreach (var r in cats.Rows)
                        if (r[c].ToTsv().Equals(category, StringComparison.OrdinalIgnoreCase)) return true;
            }
            var twui = pack.VisibleEntries.FirstOrDefault(x => x.Path.EndsWith("warband_upgrades.twui.xml", StringComparison.OrdinalIgnoreCase));
            if (twui is not null)
                return System.Text.Encoding.UTF8.GetString(pack.ReadDecoded(twui))
                       .IndexOf("holder_tab_" + category, StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch { }
        return false;
    }

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

    /// <summary>twui 里已有的页签 key（新建页签对话框的下拉用）。</summary>
    public List<string> TabKeys()
    {
        var keys = new List<string>();
        try
        {
            var pack = _pack?.Archive;
            if (pack is null) return keys;
            var e = pack.VisibleEntries.FirstOrDefault(x => x.Path.EndsWith("warband_upgrades.twui.xml", StringComparison.OrdinalIgnoreCase));
            if (e is null) return keys;
            keys = WarbandNewTab.ExistingTabs(System.Text.Encoding.UTF8.GetString(pack.ReadDecoded(e)));
            var cats = TableFiles.ReadMerged(pack, "unit_upgrade_group_ui_categories_tables", GetSchema(), null);
            if (cats is not null)
                foreach (var r in cats.Rows)
                {
                    var v = r[0].ToTsv();
                    if (v.Length > 0 && !keys.Contains(v, StringComparer.OrdinalIgnoreCase)) keys.Add(v);
                }
        }
        catch (Exception ex) { Log?.Invoke("页签列表读取失败：" + ex.Message); }
        return keys;
    }

    /// <summary>图标 url（http://icons.local/…）→ WPF 能读的本地文件路径（兵种库列表用）。</summary>
    public string? LocalIconPath(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        const string prefix = "http://icons.local/";
        if (!url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var name = url[prefix.Length..];
        var q = name.IndexOf('?');
        if (q >= 0) name = name[..q];                  // 图标 url 现在带 ?v=时间戳（刷缓存用）
        var f = Path.Combine(Icons.CacheDir, name);
        return File.Exists(f) ? f : null;
    }

    public PackSession? Pack => _pack;

    /// <summary>文件树里这棵树根的显示名（默认文件名；原版那棵叫「原版战帮升级」）。</summary>
    public string PackDisplayName { get; private set; } = "";

    /// <summary>
    /// 新建「原版战帮升级」包：把**原版**的战帮相关内容（白名单表 + twui + 页签图）抽出来写成一个独立 pack，
    /// 以后编辑都落在这个包上。已存在就直接用。
    /// </summary>
    /// <summary>
    /// 把**内置的 WUU 素材**组装成一个可编辑的参考包，**导出到指定目录**（「导入 WUU 模板」用）。
    /// v1.5.0 起启动不再自动生成/打开它（启动 = 空状态；用户自行导入模板）。
    /// 目标文件已存在就直接沿用（想要全新的先删掉旧的）。返回包路径；素材缺失时返回 null。
    /// </summary>
    public string? BuildWuuTemplatePack(string destDir, string? fileName = null)
    {
        try { return BuildWuuTemplatePackCore(destDir, fileName); }
        catch (Exception ex)
        {
            Log?.Invoke("导入 WUU 模板失败：" + ex.Message);
            return null;
        }
    }

    private string? BuildWuuTemplatePackCore(string destDir, string? fileName = null)
    {
        Directory.CreateDirectory(destDir);
        var dest = Path.Combine(destDir, fileName ?? "WUU战帮升级.pack");
        if (File.Exists(dest))
        {
            Log?.Invoke($"WUU 模板：目标已存在，直接沿用 {dest}");
            return dest;
        }

        var packs = new List<string>();
        var game = settings.GameDir ?? "";
        string? steam = null;
        try { steam = string.IsNullOrWhiteSpace(game) ? null : Directory.GetParent(Directory.GetParent(game)!.FullName)?.FullName; }
        catch { steam = null; }
        var ws = steam is null ? null : Path.Combine(steam, "workshop", "content", "1142710", "2853239091");
        // ① 内置的 WUU 内容优先（随工具发布，最稳）
        var bundled = Path.Combine(AppContext.BaseDirectory, "bundled", "wuu");
        if (Directory.Exists(bundled)) packs.AddRange(Directory.GetFiles(bundled, "*.pack"));
        // ② 其次 workshop 里的 WUU
        if (ws is not null && Directory.Exists(ws)) packs.AddRange(Directory.GetFiles(ws, "*.pack"));
        if (packs.Count == 0) { Log?.Invoke("WUU 模板：找不到素材（bundled/wuu 里没有 .pack）"); return null; }

        // **UI 来源（twui + skins）单独排队：WUF（warband ui framework，包名 !!!!!!TLA_warband_twui.pack）优先。**
        // 游戏里 WUF 与 WUU 的同名 UI 文件是"WUF 覆盖 WUU"（原作者设定），所以工具新建的页签也必须建在
        // **WUF 的那份 twui** 上 —— 否则改动落在游戏里根本不会生效的那份文件上。
        var uiSources = new List<string>();
        if (Directory.Exists(bundled))
            uiSources.AddRange(Directory.GetFiles(bundled, "*warband_twui*.pack"));
        var wsRoot = steam is null ? null : Path.Combine(steam, "workshop", "content", "1142710");
        if (wsRoot is not null && Directory.Exists(wsRoot))
            foreach (var d in Directory.GetDirectories(wsRoot))
            {
                try { uiSources.AddRange(Directory.GetFiles(d, "*warband_twui*.pack")); } catch { }
            }
        uiSources = uiSources.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        uiSources.AddRange(packs);                       // WUF 里没有的素材（背景/按钮 PNG）再从内容包拿
        var uiSourceTag = uiSources.Count > 0 ? Path.GetFileName(uiSources[0]) : "(无)";

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var items = new List<(string Path, byte[] Data)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var wuuFiles = 0;
        var wuuTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ③ 战帮表（原版 db.pack **只当运行时数据源**，不照搬进包）
        foreach (var pk in packs)
        {
            try
            {
                using var g = PackArchive.Open(pk);
                foreach (var e in g.VisibleEntries)
                {
                    var p = e.Path.Replace('\\', '/');
                    var table = p.StartsWith("db/", StringComparison.OrdinalIgnoreCase) && p.Split('/').Length > 1 ? p.Split('/')[1] : "";
                    if (table.Length == 0 || !PackSession.WarbandTables.Contains(table, StringComparer.OrdinalIgnoreCase)) continue;
                    if (!seen.Add(e.Path)) continue;
                    items.Add((e.Path, g.ReadDecoded(e)));
                    wuuTables.Add(table);
                    wuuFiles++;
                }
            }
            catch (Exception ex) { Log?.Invoke($"跳过 {Path.GetFileName(pk)}：{ex.Message}"); }
        }

        // ④ 战帮 UI：twui + **整个 ui/skins/default 目录**（upgrade_subtitle*.png 那个"框"就在这里）。
        //    顺序 = WUF 优先，其余从内容包补；同名文件第一个赢（seen 去重）。
        foreach (var pk in uiSources)
        {
            try
            {
                using var g = PackArchive.Open(pk);
                foreach (var e in g.VisibleEntries)
                {
                    var p = e.Path.Replace('\\', '/');
                    var want = p.EndsWith("warband_upgrades.twui.xml", StringComparison.OrdinalIgnoreCase)
                            || p.StartsWith("ui/skins/default/", StringComparison.OrdinalIgnoreCase);
                    if (!want || !seen.Add(e.Path)) continue;
                    items.Add((e.Path, g.ReadDecoded(e)));
                }
            }
            catch { }
        }

        PackWriter.WriteNew(dest, items);
        sw.Stop();
        Log?.Invoke($"导入 WUU 模板：{dest}（{items.Count} 个文件，其中 WUU 表/内容 {wuuFiles} 个，UI 来源 {uiSourceTag}，{sw.ElapsedMilliseconds} ms）");
        return dest;
    }

    /// <summary>原版战帮升级 = 游戏 data/db.pack（战帮相关的原版表都在里面；只读，当数据源用）。</summary>
    public string? VanillaDbPackPath =>
        string.IsNullOrWhiteSpace(settings.GameDir) ? null : Path.Combine(settings.GameDir, "data", "db.pack");

    /// <summary>
    /// 新建「原版战帮升级」包：把**原版**的战帮表 + 战帮 UI 抽成一个独立包（WUU 找不到时的兜底）。
    /// 已存在就直接用。
    /// </summary>
    public string? EnsureVanillaWarbandPack()
    {
        try
        {
            var src = VanillaDbPackPath;
            if (src is null || !File.Exists(src)) return null;
            var dir = Path.Combine(AppContext.BaseDirectory, "packs");
            Directory.CreateDirectory(dir);
            var dest = Path.Combine(dir, "原版战帮升级.pack");
            if (File.Exists(dest)) return dest;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var items = new List<(string Path, byte[] Data)>();
            using (var db = PackArchive.Open(src))
                foreach (var e in db.VisibleEntries)
                {
                    var p = e.Path.Replace('\\', '/');
                    var table = p.StartsWith("db/", StringComparison.OrdinalIgnoreCase) && p.Split('/').Length > 1 ? p.Split('/')[1] : "";
                    if (table.Length > 0 && PackSession.WarbandTables.Contains(table, StringComparer.OrdinalIgnoreCase))
                        items.Add((e.Path, db.ReadDecoded(e)));
                }
            var gd = Path.Combine(settings.GameDir ?? "", "data");
            if (Directory.Exists(gd))
                foreach (var pk in Directory.GetFiles(gd, "*.pack"))
                {
                    try
                    {
                        using var g = PackArchive.Open(pk);
                        foreach (var e in g.VisibleEntries)
                        {
                            var p = e.Path.Replace('\\', '/');
                            var want = p.EndsWith("warband_upgrades.twui.xml", StringComparison.OrdinalIgnoreCase)
                                    || p.StartsWith("ui/skins/default/", StringComparison.OrdinalIgnoreCase);
                            if (!want) continue;
                            if (items.Any(x => x.Path.Equals(e.Path, StringComparison.OrdinalIgnoreCase))) continue;
                            items.Add((e.Path, g.ReadDecoded(e)));
                        }
                    }
                    catch { }
                }
            PackWriter.WriteNew(dest, items);
            sw.Stop();
            Log?.Invoke($"新建原版战帮升级包：{dest}（{items.Count} 个文件，{sw.ElapsedMilliseconds} ms）");
            return dest;
        }
        catch (Exception ex)
        {
            Log?.Invoke("建原版战帮包失败：" + ex.Message);
            return null;
        }
    }

    public event Action<string>? Log;

    public string EngineText => _pack?.IsOpen == true
        ? $"读：原生解析（内存）· 写/诊断：rpfm_cli 4.7.4（随包{( _cli is null ? "，按需拉起" : "")}）"
        : $"引擎：原生格式层（读）· rpfm_cli 4.7.4（写/诊断，随包）";

    public string SchemaText => _schema is null
        ? "schema：随包提供（首次读表时载入）"
        : $"schema：已载入（{_schema.Definitions.Count} 张表）";

    /// <summary>schema 懒加载（11MB，约 170ms，只做一次）。</summary>
    public Schema GetSchema()
    {
        if (_schema is not null) return _schema;
        var path = RpfmCli.LocateSchema()
                   ?? throw new FileNotFoundException("找不到 schema_wh3.ron —— 发布包里应在 schemas\\ 目录下。");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _schema = Schema.Load(path);
        sw.Stop();
        Log?.Invoke($"schema 载入：{_schema.Definitions.Count} 张表（{sw.ElapsedMilliseconds} ms）");
        return _schema;
    }

    /// <summary>起 rpfm_cli 引擎（只在写回/诊断/依赖缓存时需要）。</summary>
    public async Task<RpfmCli> EnsureEngineAsync(CancellationToken ct = default)
    {
        if (_cli is not null) return _cli;
        await _gate.WaitAsync(ct);
        try
        {
            if (_cli is not null) return _cli;
            var exe = RpfmCli.LocateCli()
                      ?? throw new FileNotFoundException(
                          "找不到 rpfm_cli.exe —— 发布包里应在 rpfm\\ 目录下（或用环境变量 RPFM_CLI_PATH 指定）。");
            var schema = RpfmCli.LocateSchema()
                         ?? throw new FileNotFoundException("找不到 schema_wh3.ron。");
            _cli = new RpfmCli(exe, schema);
            _cli.Log += line => Log?.Invoke(line);
            if (!string.IsNullOrWhiteSpace(settings.GameDir)) _cli.GamePath = settings.GameDir;
            Log?.Invoke("rpfm_cli 就绪：" + exe);
            return _cli;
        }
        finally { _gate.Release(); }
    }

    /// <summary>记录游戏目录（诊断 / 依赖缓存要用）。</summary>
    public async Task<string> ApplyGameAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(settings.GameDir))
            return "没设游戏目录：打开包、看表、编辑都不受影响；只有「诊断」「依赖缓存」需要它。";
        if (_cli is not null) _cli.GamePath = settings.GameDir;
        await Task.CompletedTask;
        return $"游戏目录已记录：{settings.GameDir}";
    }

    /// <summary>打开一个包（原生，不开进程）。</summary>
    public async Task<PackSession> OpenPackAsync(string packPath, CancellationToken ct = default,
                                                 string? displayName = null, bool asUnitPack = false)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var full = Path.GetFullPath(packPath);
        var session = _packs.FirstOrDefault(x =>
            string.Equals(x.PackPath, full, StringComparison.OrdinalIgnoreCase));
        if (session is null)
        {
            session = new PackSession(null);
            await session.OpenAsync(full, ct);
            _packs.Add(session);
        }
        session.DisplayName = string.IsNullOrWhiteSpace(displayName) ? Path.GetFileName(full) : displayName!;
        if (asUnitPack) session.IsUnitPack = true;
        Loc.MergePack(session);                          // MOD 自带的 text/db/*.loc 优先进来（兵种包的中文名也靠它）
        if (!asUnitPack)
        {
            _pack = session;                             // 新打开的成为"当前"（画布看它）
            PackDisplayName = session.DisplayName;
        }
        sw.Stop();
        Log?.Invoke(session.IsUnitPack
            ? $"已加载兵种包 {Path.GetFileName(packPath)}：{session.Files.Count} 个文件（参考用：图标/中文名/兵种库；不占画布）"
            : $"已打开 {Path.GetFileName(packPath)}：{session.Files.Count} 个文件（原生 {sw.ElapsedMilliseconds} ms）");
        return session;
    }

    public async Task ClosePackAsync(CancellationToken ct = default)
    {
        if (_pack is not null) await _pack.CloseAsync(ct);
    }

    /// <summary>原生解一张表（内存里）。</summary>
    public DbTable ReadTable(string innerPath)
    {
        var pack = _pack ?? throw new InvalidOperationException("还没打开 pack。");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var table = pack.ReadTable(innerPath, GetSchema());
        sw.Stop();
        Log?.Invoke($"解表 {table.TableName}：{table.Rows.Count} 行 × {table.Columns.Count} 列（{sw.ElapsedMilliseconds} ms）");
        return table;
    }

    /// <summary>卡图解析（候选链 / 游戏索引 / 缓存都在里面；画布与兵种库共用）。</summary>
    private IconResolver? _icons;
    private IconResolver Icons => _icons ??= new IconResolver(settings, GetSchema, line => Log?.Invoke(line));

    /// <summary>
    /// 准备卡图解析：**别的已加载包（兵种 mod）也并进候选链** ——
    /// 这样"战帮包引用了兵种包的兵"时图标/中文名也出得来（画布上不会只剩 key）。
    /// </summary>
    private void PrepareIcons(PackArchive pack)
    {
        Icons.SetExtraPacks(_packs.Select(s => s.Archive).Where(a => a is not null && !ReferenceEquals(a, pack))!);
        Icons.Prepare(pack);                 // 注意别写成 PrepareIcons(pack)（自我递归 = 栈溢出闪退，v0.133 踩过）
    }

    /// <summary>本地化文本（.loc → 中文名）。</summary>
    private Localization? _loc;
    private Localization Loc => _loc ??= new Localization(settings, line => Log?.Invoke(line));

    /// <summary>游戏皮肤素材（页签背景图 + UI 区那几个图；旧工坊的背景 UI 用）——按包各一份。</summary>
    private readonly Dictionary<string, SkinAssets> _skins = new(StringComparer.OrdinalIgnoreCase);
    private SkinAssets Skins(PackArchive? pack, string? packPath)
    {
        var tag = SkinTagOf(packPath);
        // 注意：**保存（写回原包）会重开包 → 换了 Archive 实例**。缓存里的 SkinAssets 要是还攥着
        // 旧实例，换图抽图就会 "Cannot access a closed file"（表现为"当前是空的 / 预览点不出来"）。
        if (!_skins.TryGetValue(tag, out var sa) || !ReferenceEquals(sa.Pack, pack))
            _skins[tag] = sa = new SkinAssets(pack, settings.GameDir, line => Log?.Invoke(line), tag);
        return sa;
    }

    /// <summary>皮肤缓存的分目录键（v1.5.0 起 = `包名@目录哈希前 6 位`）。
    /// 以前只取包文件名 → **不同工程里的同名包会共用同一份缓存**（切工程后画面串图）；加上目录哈希就互不干扰。</summary>
    private static string SkinTagOf(string? packPath)
    {
        if (string.IsNullOrWhiteSpace(packPath)) return "common";
        var name = Path.GetFileNameWithoutExtension(packPath);
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(packPath)) ?? "";
            var h = System.Security.Cryptography.SHA1.HashData(
                System.Text.Encoding.UTF8.GetBytes(dir.ToLowerInvariant()));
            return name + "@" + Convert.ToHexString(h)[..6].ToLowerInvariant();
        }
        catch { return name; }
    }

    /// <summary>切换"当前包"（多画布：每个画布看自己的包）。</summary>
    public void SwitchTo(PackSession? session)
    {
        if (session is null || !session.IsOpen) return;
        _pack = session;
        PackDisplayName = session.DisplayName;
        Log?.Invoke($"切到画布：{session.DisplayName}（{session.Files.Count} 个文件）");
    }

    /// <summary>画布数据缓存（"打开/导入时算一次，改动或换包才重算"）。</summary>
    private string? _canvasCache;
    private string? _canvasCacheKey;
    private int _rev;

    // —— 撤销 / 重做：每步编辑前压一份"编辑集快照"（WarbandEdits.Clone） ——
    private readonly Dictionary<string, List<WarbandEdits>> _undoByPack = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<WarbandEdits>> _redoByPack = new(StringComparer.OrdinalIgnoreCase);
    private string Key => _pack?.PackPath ?? "";
    public int UndoDepth => _undoByPack.TryGetValue(Key, out var s) ? s.Count : 0;

    /// <summary>在改编辑集之前调一次：把当前状态压进撤销栈（同时清空重做栈）。</summary>
    public void PushUndo()
    {
        var k = Key;
        if (!_undoByPack.TryGetValue(k, out var s)) _undoByPack[k] = s = [];
        s.Add(Edits.Clone());
        if (s.Count > 100) s.RemoveAt(0);
        _redoByPack.Remove(k);
    }

    /// <summary>撤销一步。</summary>
    public bool Undo()
    {
        var k = Key;
        if (!_undoByPack.TryGetValue(k, out var s) || s.Count == 0) return false;
        if (!_redoByPack.TryGetValue(k, out var r)) _redoByPack[k] = r = [];
        r.Add(Edits.Clone());
        var snap = s[^1]; s.RemoveAt(s.Count - 1);
        _editsByPack[k] = snap;
        InvalidateCanvas();
        RestagePendingArt();          // 撤掉的换图预览别留在画布上（撤销/重做都要跟当前待导出对齐）
        Log?.Invoke($"撤销一步（剩 {s.Count} 步可撤）：{Edits.Summary()}");
        return true;
    }

    /// <summary>重做一步。</summary>
    public bool Redo()
    {
        var k = Key;
        if (!_redoByPack.TryGetValue(k, out var r) || r.Count == 0) return false;
        if (!_undoByPack.TryGetValue(k, out var s)) _undoByPack[k] = s = [];
        s.Add(Edits.Clone());
        var snap = r[^1]; r.RemoveAt(r.Count - 1);
        _editsByPack[k] = snap;
        InvalidateCanvas();
        RestagePendingArt();
        Log?.Invoke($"重做一步：{Edits.Summary()}");
        return true;
    }

    /// <summary>
    /// 把"待导出"里 warband_upgrades 的图替换重新暂存到皮肤缓存 —— 撤销/重做后画布要跟着变。
    /// 先 DropStaged 清掉已撤掉的那些（否则画布还挂着旧图），再按当前的 FileReplacements 重新抽一遍。
    /// </summary>
    private void RestagePendingArt()
    {
        try
        {
            var skins = Skins(_pack?.Archive, _pack?.PackPath);
            skins.DropStaged();
            foreach (var (target, source) in Edits.FileReplacements)
            {
                if (!target.StartsWith("ui/skins/default/warband_upgrades/", StringComparison.OrdinalIgnoreCase)) continue;
                var name = Path.GetFileName(target);
                skins.DropCache(name);
                if (File.Exists(source)) skins.StageLocalFile(source, name);
                else skins.ExtractForPage(source, name);
            }
        }
        catch { }
    }    /// <summary>有编辑/换包时让画布缓存失效。</summary>
    public void InvalidateCanvas()
    {
        _rev++;
        _canvasCache = null;
        _canvasCacheKey = null;
    }

    /// <summary>
    /// **保存**：把当前编辑写回**打开的那个包**（原地写）。做法是先导出到临时文件、再替换原文件，
    /// 并把原文件留一份 `.bak`（保存是不可逆动作，留个后路）。
    /// </summary>
    public async Task<string> SaveInPlaceAsync()
    {
        var session = _pack ?? throw new InvalidOperationException("先打开一个包。");
        var pack = session.PackPath;
        var tmp = pack + ".saving";
        var rep = Export(tmp);                       // 复用导出（含全部落表 + 文件替换）
        // **先把包关掉**：读包时我们自己一直占着这个文件的句柄，不关就替换不了（"used by another process"）
        await CloseSessionAsync(session);
        var bak = "";
        try
        {
            // 备份：**工程的 old/ 优先**（v1.5.0 起；那份是完整 .pack，能直接在「历史版本」里还原，保留最近 N 份）；
            // 包不在任何工程里（如 --open-pack）才退回旧备份目录（.bak）。
            var proj = ProjectStore.ProjectDirOfPack(pack);
            if (proj is not null)
            {
                bak = ProjectStore.BackupPack(proj, pack, settings.HistoryKeep) ?? "";
            }
            else
            {
                var bakDir = BackupDirPath();
                Directory.CreateDirectory(bakDir);
                bak = Path.Combine(bakDir, Path.GetFileName(pack) + "." + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".bak");
                File.Copy(pack, bak);
            }
        }
        catch (Exception ex) { Log?.Invoke("备份失败（继续保存）：" + ex.Message); bak = ""; }
        File.Delete(pack);
        File.Move(tmp, pack);
        DiscardEditsOf(pack);                        // 清的是**刚保存的那个包**的编辑（关包会把当前包切走，别用 DiscardEdits）
        Log?.Invoke($"保存：已写回 {pack}（备份 {(bak.Length > 0 ? bak : "失败")}）");
        return $"已保存回原包：{Path.GetFileName(pack)}（备份 {(bak.Length > 0 ? Path.GetFileName(bak) : "失败，见日志")}）；" +
               $"树里 {rep.UnitsInTree} 个兵，解锁 {rep.Unlocked}";
    }

    /// <summary>备份文件夹（设置里没写就用默认 %APPDATA%\WarbandStudioackups）。</summary>
    /// <summary>
    /// 从工程的 `old/` 历史版本**还原**一个包：先把当前包也备份一份（还原不是丢东西的借口），
    /// 再把所选备份拷回原位、清掉内存编辑。**重开包由调用方走正常打开链路**（画布/树/清单要一起刷）。
    /// 调用方负责先问"有未导出编辑怎么办"。
    /// </summary>
    public async Task<string> RestoreFromBackupAsync(string packPath, string backupFile)
    {
        var proj = ProjectStore.ProjectDirOfPack(packPath);
        var session = _packs.FirstOrDefault(s => s.PackPath.Equals(packPath, StringComparison.OrdinalIgnoreCase));
        if (session is not null) await CloseSessionAsync(session);      // 先关，释放文件句柄
        var keepBak = proj is not null ? ProjectStore.BackupPack(proj, packPath, settings.HistoryKeep) : null;
        File.Copy(backupFile, packPath, overwrite: true);
        DiscardEditsOf(packPath);
        Log?.Invoke($"还原：{Path.GetFileName(packPath)} ← {Path.GetFileName(backupFile)}" +
                    $"（还原前的包也备份了一份：{(keepBak is null ? "—" : Path.GetFileName(keepBak))}）");
        return $"已还原 {Path.GetFileName(packPath)} ← {Path.GetFileName(backupFile)}（还原前的那份也进了历史版本）";
    }

    public string BackupDirPath()
    {
        var d = settings.BackupDir;
        return string.IsNullOrWhiteSpace(d)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WarbandStudio", "backups")
            : d.Trim();
    }

    /// <summary>原生导出（精英解锁）：不动原文件，新增 db/main_units_tables/studio_elite_unlock 覆盖表。</summary>
    public WarbandStudio.Pack.ExportReport Export(string destPath)
    {
        var pack = _pack?.PackPath ?? throw new InvalidOperationException("先打开一个 pack。");
        var gameDir = settings.GameDir;
        if (string.IsNullOrWhiteSpace(gameDir))
            throw new InvalidOperationException("导出需要游戏目录（原版 data/db.pack 里有兵的完整行）。请在左下「全局选项」里设置。");
        var vanilla = Path.Combine(gameDir, "data", "db.pack");
        if (!File.Exists(vanilla))
            throw new FileNotFoundException("找不到原版包：" + vanilla);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var rep = WarbandStudio.Pack.WarbandExporter.Export(pack, vanilla, destPath, GetSchema(), Edits);
        sw.Stop();
        Log?.Invoke($"导出完成：{destPath}（解锁 {rep.Unlocked} 个兵，{sw.ElapsedMilliseconds} ms）");
        return rep;
    }

    /// <summary>
    /// 构建战帮画布数据（原生读表 + 按键合并）：组坐标来自 unit_upgrade_group_ui_infos_tables，
    /// 组里的兵来自 unit_to_unit_group_junctions_tables，卡图按兵种 key 到 assets/icons 里找。
    /// </summary>
    public string BuildWarbandJson()
    {
        var session = _pack ?? throw new InvalidOperationException("先打开一个 pack。");
        var pack = session.Archive ?? throw new InvalidOperationException("这个 pack 还没打开。");
        var cacheKey = session.PackPath + "|" + _rev;
        if (_canvasCache is not null && _canvasCacheKey == cacheKey)
        {
            Log?.Invoke("画布数据：走缓存（没改动就不重算）");
            return _canvasCache;
        }
        var schema = GetSchema();
        var notes = new List<string>();

        var infos = TableFiles.ReadMerged(pack, "unit_upgrade_group_ui_infos_tables", schema, notes);
        var junc = TableFiles.ReadConcat(pack, "unit_to_unit_group_junctions_tables", schema, notes);
        if (infos is null) throw new InvalidDataException("包里没有 unit_upgrade_group_ui_infos_tables（没有组坐标）。");

        int Col(DbTable t, params string[] names)
        {
            foreach (var n in names)
            {
                var i = t.Columns.FindIndex(c => c.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
                if (i >= 0) return i;
            }
            return -1;
        }

        var gCol = Col(infos, "unit_upgrade_group", "group", "unit_group");
        var xCol = Col(infos, "x");
        var yCol = Col(infos, "y");
        var cCol = Col(infos, "category");
        if (gCol < 0 || xCol < 0 || yCol < 0) throw new InvalidDataException("组坐标表的列名不符合预期。");

        var unitsByGroup = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (junc is not null)
        {
            var ju = Col(junc, "unit", "unit_key", "land_unit");
            var jg = Col(junc, "unit_group", "group", "unit_upgrade_group");
            if (ju < 0) ju = TableFiles.FindKeyColumn(junc);
            if (jg < 0) jg = ju == 0 ? 1 : 0;
            foreach (var row in junc.Rows)
            {
                var u = row[ju].ToTsv();
                var g = row[jg].ToTsv();
                if (u.Length == 0 || g.Length == 0) continue;
                if (!unitsByGroup.TryGetValue(g, out var list)) unitsByGroup[g] = list = [];
                if (!list.Contains(u)) list.Add(u);
            }
        }

        PrepareIcons(pack);

        // —— 连线（游戏里真实显示的箭头）：unit_upgrade_group_ui_links_tables ——
        // 数据链结论（C1，用户拍板）：**画线读这张表**；能不能升级读 unit_upgrade_to_unit_groups_tables（不画线）。
        var linksT = TableFiles.ReadConcat(pack, "unit_upgrade_group_ui_links_tables", schema, notes);
        var linkArr = new System.Text.Json.Nodes.JsonArray();
        var linkPairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var posHisto = new SortedDictionary<string, int>(StringComparer.Ordinal);
        if (linksT is not null)
        {
            var lc = Col(linksT, "child_key", "child");
            var lp = Col(linksT, "parent_key", "parent");
            var lcp = Col(linksT, "child_link_position");
            var lpp = Col(linksT, "parent_link_position");
            var lpo = Col(linksT, "parent_link_position_offset");
            var lco = Col(linksT, "child_link_position_offset");
            var lmo = Col(linksT, "mid_link_offset");
            if (lc >= 0 && lp >= 0)
                foreach (var row in linksT.Rows)
                {
                    var child = row[lc].ToTsv();
                    var parent = row[lp].ToTsv();
                    if (child.Length == 0 || parent.Length == 0) continue;
                    var cp = lcp >= 0 ? (int)row[lcp].Int : 0;
                    var pp = lpp >= 0 ? (int)row[lpp].Int : 0;
                    linkArr.Add(new System.Text.Json.Nodes.JsonObject
                    {
                        ["c"] = child,
                        ["p"] = parent,
                        ["cp"] = cp,
                        ["pp"] = pp,
                        ["po"] = lpo >= 0 ? (float)row[lpo].Float : 0f,
                        ["co"] = lco >= 0 ? (float)row[lco].Float : 0f,
                        ["mo"] = lmo >= 0 ? (float)row[lmo].Float : 0f,
                    });
                    linkPairs.Add(parent + PairSep + child);
                    var pk = cp + "→" + pp;
                    posHisto[pk] = posHisto.TryGetValue(pk, out var n) ? n + 1 : 1;
                }
        }

        // —— 路线（决定"能不能升"）：unit_upgrade_to_unit_groups_tables ——
        var routesT = TableFiles.ReadConcat(pack, "unit_upgrade_to_unit_groups_tables", schema, notes);
        var routePairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var routeInfo = new Dictionary<string, (string Cost, int Rank, int Sub)>(StringComparer.OrdinalIgnoreCase);
        if (routesT is not null)
        {
            var rb = Col(routesT, "base_unit_group");
            var rt = Col(routesT, "target_unit_group");
            var rk = Col(routesT, "upgrade_key");
            var rc = Col(routesT, "resource_cost");
            var rr = Col(routesT, "required_rank");
            var rs = Col(routesT, "subtracted_rank");
            if (rb >= 0 && rt >= 0)
                foreach (var row in routesT.Rows)
                {
                    var b = row[rb].ToTsv();
                    var t2 = row[rt].ToTsv();
                    if (b.Length == 0 || t2.Length == 0) continue;
                    routePairs.Add(b + PairSep + t2);
                    if (rk >= 0)
                        routeInfo[row[rk].ToTsv()] = (rc >= 0 ? row[rc].ToTsv() : "",
                                                      rr >= 0 ? (int)row[rr].Int : 0,
                                                      rs >= 0 ? (int)row[rs].Int : 0);
                }
        }

        // —— 成本 Key 列表（"新建升级"对话框里下拉用）：包里已有的 + 编辑里新建的；带金额一起给 ——
        var costKeys = new List<string>();
        var costAmount = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var costs = TableFiles.ReadConcat(pack, "resource_costs_tables", schema, notes);
            if (costs is not null)
            {
                var cid = Col(costs, "id");
                if (cid < 0) cid = TableFiles.FindKeyColumn(costs);
                var cval = Col(costs, "treasury_cost");
                if (cid >= 0)
                    foreach (var r in costs.Rows)
                    {
                        var v = r[cid].ToTsv();
                        if (v.Length == 0) continue;
                        if (!costKeys.Contains(v, StringComparer.OrdinalIgnoreCase)) costKeys.Add(v);
                        // 用 Num 而不是 .Float：treasury_cost 是 I32，.Float 载荷恒为 0（"金额全显示 0"的根因）
                        if (cval >= 0) costAmount[v] = r[cval].Num;
                    }
            }
        }
        catch (Exception ex) { notes.Add("成本 Key 列表跳过：" + ex.Message); }
        foreach (var kv in Edits.AddCost)
        {
            if (!costKeys.Contains(kv.Key, StringComparer.OrdinalIgnoreCase)) costKeys.Add(kv.Key);
            costAmount[kv.Key] = kv.Value;                // 已经带符号（负 = 消耗）
        }
        // 改名/删掉的旧 id：画布的成本下拉里也要去掉 —— 以前只并了 AddCost，旧名字还挂在对话框里
        // （用户实测："工坊里已经改名了，画布内选择升级的成本里还是旧名字"）。同一轮又加回来的（含只改大小写）以 AddCost 为准。
        foreach (var id in Edits.RemoveCost)
        {
            if (Edits.AddCost.ContainsKey(id)) continue;
            costKeys.RemoveAll(k => k.Equals(id, StringComparison.OrdinalIgnoreCase));
            costAmount.Remove(id);
        }
        costKeys.Sort(StringComparer.OrdinalIgnoreCase);

        // —— 成本的"额外资源"：resource_cost_pooled_resource_junctions_tables ——
        // 除了金币，成本 Key 还能扣别的资源（雪乃的 Yukino_Brt_Upgrade_Cost_T1 就扣 50 点骑士道：
        // pooled_resource_factor=chivalry_other / amount=-50 / resource_cost=那个成本 key）。
        // 这张表是**组合键**（factor + resource_cost），必须 ReadComposite，不然按第一个键合并会把行吃掉。
        // 显示用的是**资源**（不是池名：工具建的池叫 <资源>_warband_upgrade）和它的图标 url（画布下拉里带图标）。
        var pools = new Dictionary<string, List<(string Factor, double Amount, string Res, string Icon)>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var schema2 = GetSchema();
            PackArchive? vp = null;
            var vanilla = VanillaDbPackPath;
            if (vanilla is not null && File.Exists(vanilla)) vp = PackArchive.Open(vanilla);
            try
            {
                // 池 → 资源（unique_id → resource）
                var poolRes2 = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var pft = TableFiles.Merge(
                    vp is null ? null : TableFiles.ReadMerged(vp, "pooled_resource_factor_junctions_tables", schema2, notes),
                    TableFiles.ReadMerged(pack, "pooled_resource_factor_junctions_tables", schema2, notes));
                if (pft is not null)
                {
                    var ui2 = Col(pft, "unique_id");
                    if (ui2 < 0) ui2 = TableFiles.FindKeyColumn(pft);
                    var rs2 = Col(pft, "resource");
                    if (ui2 >= 0 && rs2 >= 0)
                        foreach (var r in pft.Rows)
                        {
                            var id = r[ui2].ToTsv();
                            if (id.Length > 0) poolRes2[id] = r[rs2].ToTsv();
                        }
                }
                // 资源 → optional_icon_path（图标到"用到的资源"才解析成 url，免得为几百种资源白抽图）
                var resIconPath2 = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var rt2 = TableFiles.Merge(
                    vp is null ? null : TableFiles.ReadMerged(vp, "pooled_resources_tables", schema2, notes),
                    TableFiles.ReadMerged(pack, "pooled_resources_tables", schema2, notes));
                if (rt2 is not null)
                {
                    var rk2 = Col(rt2, "key");
                    if (rk2 < 0) rk2 = TableFiles.FindKeyColumn(rt2);
                    var ri2 = Col(rt2, "optional_icon_path");
                    if (rk2 >= 0 && ri2 >= 0)
                        foreach (var r in rt2.Rows)
                        {
                            var key = r[rk2].ToTsv();
                            var png = r[ri2].ToTsv();
                            if (key.Length > 0 && png.Length > 0) resIconPath2[key] = png;
                        }
                }
                string ResOf(string factor)
                {
                    if (poolRes2.TryGetValue(factor, out var r0)) return r0;
                    var pf = Edits.PoolFactors.FirstOrDefault(f => f.UniqueId.Equals(factor, StringComparison.OrdinalIgnoreCase));
                    return pf.Resource ?? "";
                }
                var iconUrls2 = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                string IconOfRes(string res)
                {
                    if (res.Length == 0 || !resIconPath2.TryGetValue(res, out var png)) return "";
                    if (iconUrls2.TryGetValue(res, out var u)) return u;
                    var url = Icons.UrlByPngName(png, "res_" + IconResolver.Stem(png)) ?? "";
                    iconUrls2[res] = url;
                    return url;
                }
                void AddPool(string fac, string cost, double amt)
                {
                    if (fac.Length == 0 || cost.Length == 0) return;
                    var res = ResOf(fac);
                    if (!pools.TryGetValue(cost, out var l)) pools[cost] = l = [];
                    var at = l.FindIndex(x => x.Factor.Equals(fac, StringComparison.OrdinalIgnoreCase));
                    if (at >= 0) l[at] = (fac, amt, res, IconOfRes(res));
                    else l.Add((fac, amt, res, IconOfRes(res)));
                }
                var keyCols = new[] { "pooled_resource_factor", "resource_cost" };
                const string PoolTable = "resource_cost_pooled_resource_junctions_tables";
                var t = TableFiles.MergeComposite(
                    vp is null ? null : TableFiles.ReadComposite(vp, PoolTable, keyCols, schema2, notes),
                    TableFiles.ReadComposite(pack, PoolTable, keyCols, schema2, notes), keyCols);
                if (t is not null)
                {
                    var cf = Col(t, "pooled_resource_factor");
                    var cc = Col(t, "resource_cost");
                    var ca = Col(t, "amount");
                    if (cf >= 0 && cc >= 0 && ca >= 0)
                        foreach (var r in t.Rows) AddPool(r[cf].ToTsv(), r[cc].ToTsv(), r[ca].Num);
                    if (pools.Count > 0)
                        notes.Add($"成本额外资源：{pools.Count} 个成本 key 在读到的 junction 表里有别的资源消耗（如 chivalry_other）。");
                }
                // 待导出编辑（repl 优先）：改过资源的先清掉旧关联，再把本轮要写的关联并上
                // —— 刚创建/改过的成本，画线对话框里也要立刻看到"额外资源 + 图标"（用户实测）。
                foreach (var id in Edits.RemovePoolCost) pools.Remove(id);
                foreach (var id in Edits.RemoveCost) if (!Edits.AddCost.ContainsKey(id)) pools.Remove(id);   // 删掉的成本，额外资源也一起从下拉里去掉
                foreach (var (poolFactor, costId, amount, _, _) in Edits.PoolCosts) AddPool(poolFactor, costId, amount);
            }
            finally { vp?.Dispose(); }
        }
        catch (Exception ex) { notes.Add("成本额外资源跳过：" + ex.Message); }

        // —— 体检（只报告；按 C1 结论两边不互相顶替）——
        var routeNoLink = routePairs.Where(x => !linkPairs.Contains(x)).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var linkNoRoute = linkPairs.Where(x => !routePairs.Contains(x)).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var diag = new System.Text.Json.Nodes.JsonObject
        {
            ["routes"] = routePairs.Count,
            ["links"] = linkPairs.Count,
            ["routeNoLink"] = ToJsonArray(routeNoLink.Select(PrettyPair)),
            ["linkNoRoute"] = ToJsonArray(linkNoRoute.Select(PrettyPair)),
        };

        var jso = new System.Text.Json.Nodes.JsonObject();
        var groups = new System.Text.Json.Nodes.JsonArray();
        var icons = new System.Text.Json.Nodes.JsonObject();
        var pages = new System.Text.Json.Nodes.JsonArray();
        var seenPages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // —— 把画布上还没导出的编辑叠上来（"直接改 pack"：先在内存里看到效果）——
        foreach (var rm in Edits.RemoveGroup) unitsByGroup.Remove(rm);
        foreach (var kv in Edits.InfoEdits) unitsByGroup.TryAdd(kv.Key, []);
        foreach (var (unit, group) in Edits.RemoveJunction)
            if (unitsByGroup.TryGetValue(group, out var ul)) ul.RemoveAll(x => x.Equals(unit, StringComparison.OrdinalIgnoreCase));
        foreach (var (unit, group) in Edits.AddJunction)
        {
            if (!unitsByGroup.TryGetValue(group, out var ul)) unitsByGroup[group] = ul = [];
            if (!ul.Contains(unit, StringComparer.OrdinalIgnoreCase)) ul.Add(unit);
        }
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Edits.IsEmpty)
            Log?.Invoke($"叠加编辑：加兵 {Edits.AddJunction.Count} / 删兵 {Edits.RemoveJunction.Count} / " +
                        $"新建组 {Edits.AddGroup.Count} / 删组 {Edits.RemoveGroup.Count} / 改坐标 {Edits.InfoEdits.Count}；" +
                        $"叠加后组数 {unitsByGroup.Count}");

        // 兵种 → 派系 / 种族（"按种族/派系看"和"专属兵角标"都靠它）
        var unitFactions = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var unitRaces = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var raceFactionCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var unitExcl = new System.Text.Json.Nodes.JsonObject();
        var legendFacs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var (allFactions, _) = LoadFactions();
            var (milByFac2, _) = MilGroups();
            foreach (var f in allFactions)
            {
                if (f.Units.Count == 0) continue;
                // 分母只数**传奇派系**：叛军/小派系也挂着同一个军事组，但没人关心它们，
                // 否则"给所有传奇鼠人派系都授权了"的精英兵会被误标成专属。
                if (!f.Legendary) continue;
                raceFactionCount[f.Subculture] = raceFactionCount.TryGetValue(f.Subculture, out var rc) ? rc + 1 : 1;
                foreach (var u in f.Units)
                {
                    if (!unitFactions.TryGetValue(u, out var fl)) unitFactions[u] = fl = [];
                    if (!fl.Contains(f.Key, StringComparer.OrdinalIgnoreCase)) fl.Add(f.Key);
                    if (!unitRaces.TryGetValue(u, out var rl)) unitRaces[u] = rl = [];
                    if (!rl.Contains(f.Subculture, StringComparer.OrdinalIgnoreCase)) rl.Add(f.Subculture);
                    if (f.Legendary) legendFacs[u] = (legendFacs.TryGetValue(u, out var lc) ? lc : 0) + 1;   // 只数传奇
                }
            }
            foreach (var kv in unitFactions)
            {
                var races = unitRaces.TryGetValue(kv.Key, out var rl) ? rl : [];
                if (races.Count != 1) continue;
                var total = raceFactionCount.TryGetValue(races[0], out var n) ? n : 0;      // 该种族的传奇派系数
                var mine = legendFacs.TryGetValue(kv.Key, out var lc) ? lc : 0;             // 这个兵被几个传奇派系拿到
                if (total > 1 && mine < total) unitExcl[kv.Key] = ToJsonArray(kv.Value);    // 只有部分传奇能用 → 专属
            }
            // —— 叠加"待导出"的军事组授权：右键加入/移出之后，黄标要立刻跟着变 ——
            if (Edits.AddUnitGroup.Count > 0 || Edits.RemoveUnitGroup.Count > 0)
            {
                var facByGroup = new Dictionary<string, List<(string Fac, string Race)>>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in allFactions)
                    if (milByFac2.TryGetValue(f.Key, out var mi))
                    {
                        if (!facByGroup.TryGetValue(mi.Group, out var l)) facByGroup[mi.Group] = l = [];
                        l.Add((f.Key, f.Subculture));
                    }
                void Touch(string unit, string group, bool add)
                {
                    if (!facByGroup.TryGetValue(group, out var fs2)) return;
                    if (!unitFactions.TryGetValue(unit, out var fl)) unitFactions[unit] = fl = [];
                    if (!unitRaces.TryGetValue(unit, out var rl)) unitRaces[unit] = rl = [];
                    foreach (var (fac, race) in fs2)
                    {
                        if (add)
                        {
                            if (!fl.Contains(fac, StringComparer.OrdinalIgnoreCase)) fl.Add(fac);
                            if (!rl.Contains(race, StringComparer.OrdinalIgnoreCase)) rl.Add(race);
                        }
                        else
                        {
                            fl.RemoveAll(x => x.Equals(fac, StringComparison.OrdinalIgnoreCase));
                            rl.RemoveAll(x => x.Equals(race, StringComparison.OrdinalIgnoreCase));
                        }
                    }
                }
                foreach (var (u, g) in Edits.AddUnitGroup) Touch(u, g, true);
                foreach (var (u, g) in Edits.RemoveUnitGroup) Touch(u, g, false);
                unitExcl.Clear();
                foreach (var kv in unitFactions)
                {
                    var races = unitRaces.TryGetValue(kv.Key, out var rl) ? rl : [];
                    if (races.Count != 1) continue;
                    var total = raceFactionCount.TryGetValue(races[0], out var n) ? n : 0;
                    if (total > 1 && kv.Value.Count < total) unitExcl[kv.Key] = ToJsonArray(kv.Value);
                }
            }
            // 画布上"查不到归属"的兵：这类不打黄标（打标会误报），但把数量与样例记下来便于核对
            var unknownUnits = unitsByGroup.Values.SelectMany(x => x)
                .Where(u => !unitFactions.ContainsKey(u)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            Log?.Invoke($"派系归属：{unitFactions.Count} 个兵有派系，其中专属 {unitExcl.Count} 个；"
                        + $"画布上查不到归属的 {unknownUnits.Count} 个（不打黄标）");
            foreach (var u in unknownUnits.Take(8)) Log?.Invoke("   查不到归属：" + u);
        }
        catch (Exception ex) { notes.Add("派系归属跳过：" + ex.Message); }

        var gRaces = new System.Text.Json.Nodes.JsonObject();
        var gFactions = new System.Text.Json.Nodes.JsonObject();
        // 页签 → 种族/派系：**并集**。链路全在表单里：
        //   页签(infos.category) → 组 → 兵(junction) → 这个兵能被哪些派系用
        //   （units_to_groupings_military_permissions × factions.military_group、units_to_exclusive_faction_permissions、building_units_allowed）
        //   → 派系所属种族(factions.subculture)。混沌勇士和四神共用页签，所以一个页签会同时属于多个种族 —— 不能按"多数票"取一个。
        var pageRaceSet = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        var pageFacSet = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);

        void Emit(string g, int x, int y, string cat)
        {
            if (!emitted.Add(g)) return;
            var units = unitsByGroup.TryGetValue(g, out var list) ? list : [];
            var arr = new System.Text.Json.Nodes.JsonArray();
            var races = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var facs = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var u in units)
            {
                arr.Add(u);
                if (!icons.ContainsKey(u) && Icons.Url(u) is { } url) icons[u] = url;
                if (unitRaces.TryGetValue(u, out var rl)) foreach (var r in rl) races.Add(r);
                if (unitFactions.TryGetValue(u, out var fl)) foreach (var f in fl) facs.Add(f);
            }
            if (races.Count > 0)
            {
                gRaces[g] = ToJsonArray(races);
                if (cat.Length > 0)
                {
                    if (!pageRaceSet.TryGetValue(cat, out var rs)) pageRaceSet[cat] = rs = new(StringComparer.OrdinalIgnoreCase);
                    foreach (var r in races) rs.Add(r);
                }
            }
            if (facs.Count > 0)
            {
                gFactions[g] = ToJsonArray(facs);
                if (cat.Length > 0)
                {
                    if (!pageFacSet.TryGetValue(cat, out var fs2)) pageFacSet[cat] = fs2 = new(StringComparer.OrdinalIgnoreCase);
                    foreach (var f in facs) fs2.Add(f);
                }
            }
            groups.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["unit_group"] = g,
                ["x"] = x,
                ["y"] = y,
                ["category"] = cat,
                ["units"] = arr,
            });
            if (cat.Length > 0 && seenPages.Add(cat)) pages.Add(cat);
        }

        foreach (var row in infos.Rows)
        {
            var g = row[gCol].ToTsv();
            if (g.Length == 0) continue;
            if (Edits.RemoveGroup.Contains(g, StringComparer.OrdinalIgnoreCase)) continue;
            var cat = cCol >= 0 ? row[cCol].ToTsv() : "";
            var x = (int)row[xCol].Int;
            var y = (int)row[yCol].Int;
            if (Edits.InfoEdits.TryGetValue(g, out var ed))
            {
                x = ed.X; y = ed.Y;
                if (ed.Category is not null) cat = ed.Category;
            }
            Emit(g, x, y, cat);
        }
        // 编辑里新出现的组（还没有坐标行）：用编辑里的坐标补上
        foreach (var kv in Edits.InfoEdits)
        {
            if (groups.Count > 0 && emitted.Contains(kv.Key)) continue;
            Emit(kv.Key, kv.Value.X, kv.Value.Y, kv.Value.Category ?? "");
        }
        // 新建的页签先挂进页签列（哪怕还没有组，用户要能立刻选它往里放兵）
        foreach (var (tabKey, _, _, _) in Edits.NewTabs)
            if (seenPages.Add(tabKey)) pages.Add(tabKey);

        // 连线：去掉删掉的，补上新增的（C1：箭头读这张表）。
        // **一对组只画一条线**：删除按"对"认（两个方向都算），包里本来就有两条（正反各一）也只画一条；
        // 待导出的 AddLink 按"对"覆盖包里的那条 —— 老写法按方向精确匹配，删了反方向那条就漏，
        // 再换方向连一次就变成"两条线"（用户实测）。
        var removedLinks = new HashSet<string>(Edits.RemoveLink.Select(l => PairOf(l.Child, l.Parent)), StringComparer.OrdinalIgnoreCase);
        var keepLinks = new System.Text.Json.Nodes.JsonArray();
        var pairAt = new Dictionary<string, System.Text.Json.Nodes.JsonNode?>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in linkArr)
        {
            var c = node!["c"]!.GetValue<string>();
            var p = node["p"]!.GetValue<string>();
            if (removedLinks.Contains(PairOf(c, p)) || Edits.RemoveGroup.Contains(c, StringComparer.OrdinalIgnoreCase) || Edits.RemoveGroup.Contains(p, StringComparer.OrdinalIgnoreCase))
                continue;
            var pk = PairOf(c, p);
            if (pairAt.ContainsKey(pk)) continue;   // 同一对只留一条（包里正反都有时）
            // 存**克隆体本身**：JsonArray.Remove 是按引用比的，存原 node 去删克隆体等于没删
            //（实测：调整画线后"包里那行 + 待写那行"同时画出来 = 两条线）
            var clone = node!.DeepClone();
            pairAt[pk] = clone;
            keepLinks.Add(clone);
        }
        foreach (var l in Edits.AddLink)
        {
            if (Edits.RemoveGroup.Contains(l.Child, StringComparer.OrdinalIgnoreCase)) continue;
            var pk2 = PairOf(l.Child, l.Parent);
            if (pairAt.TryGetValue(pk2, out var oldNode) && oldNode is not null)
                keepLinks.Remove(oldNode);                 // 同对的旧线（可能是反方向）先摘掉，再挂新的
            keepLinks.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["c"] = l.Child, ["p"] = l.Parent,
                ["cp"] = l.ChildPos, ["pp"] = l.ParentPos,
                ["po"] = (float)l.ParentOffset, ["co"] = (float)l.ChildOffset, ["mo"] = (float)l.MidOffset,
            });
        }

        // 路线：新增/改动/删除
        var routeSet = new HashSet<string>(routePairs, StringComparer.OrdinalIgnoreCase);
        foreach (var r in Edits.AddRoute) routeSet.Add(r.Base + PairSep + r.Target);
        foreach (var k in Edits.RemoveRoute) routeSet.RemoveWhere(x => x.StartsWith(k + PairSep, StringComparison.OrdinalIgnoreCase));
        foreach (var g in Edits.RemoveGroup) routeSet.RemoveWhere(x => x.StartsWith(g + PairSep, StringComparison.OrdinalIgnoreCase)
                                                                  || x.EndsWith(PairSep + g, StringComparison.OrdinalIgnoreCase));

        // 每条连线带上它对应的升级键与金额/等级（编辑对话框要用）：
        // 原版实测 parent=base、child=target（738/747 是这个方向）
        var routeByPair = new Dictionary<string, (string Key, string Cost, int Rank, int Sub)>(StringComparer.OrdinalIgnoreCase);
        if (routesT is not null)
        {
            var rb2 = Col(routesT, "base_unit_group"); var rt2 = Col(routesT, "target_unit_group");
            var rk2 = Col(routesT, "upgrade_key"); var rc2 = Col(routesT, "resource_cost");
            var rr2 = Col(routesT, "required_rank"); var rs2 = Col(routesT, "subtracted_rank");
            if (rb2 >= 0 && rt2 >= 0)
                foreach (var row in routesT.Rows)
                {
                    var b2 = row[rb2].ToTsv(); var t2 = row[rt2].ToTsv();
                    if (b2.Length == 0 || t2.Length == 0) continue;
                    routeByPair[b2 + PairSep + t2] = (
                        rk2 >= 0 ? row[rk2].ToTsv() : "",
                        rc2 >= 0 ? row[rc2].ToTsv() : "",
                        rr2 >= 0 ? (int)row[rr2].Int : 0,
                        rs2 >= 0 ? (int)row[rs2].Int : 0);
                }
            foreach (var r in Edits.AddRoute) routeByPair[r.Base + PairSep + r.Target] = (r.UpgradeKey, r.Cost, r.RequiredRank, r.SubtractedRank);
            foreach (var k in Edits.RemoveRoute) routeByPair.Remove(k);
        }
        foreach (var node in keepLinks)
        {
            var o = (System.Text.Json.Nodes.JsonObject)node!;
            if (routeByPair.TryGetValue(o["p"]!.GetValue<string>() + PairSep + o["c"]!.GetValue<string>(), out var info))
            {
                o["k"] = info.Key; o["rc"] = info.Cost; o["rr"] = info.Rank; o["rs"] = info.Sub;
            }
        }

        // —— 路线明细（右键「查看已有升级」面板用）——
        // 与画布上的"路线集合"不同：这里保留每条路线的身份（upgrade_key），并把**待导出编辑**按落表语义叠上去
        // （同 key 的覆盖行替换原行；删掉的 key 整条不列）。link 列告诉页面这条路线有没有对应的界面连线
        // （没有 = 体检里的"有路线没连线"），页面据此决定交换/删除时要不要连 ui_links 一起改。
        var routeListArr = new System.Text.Json.Nodes.JsonArray();
        {
            var liveLinkPairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in keepLinks)
                if (node is System.Text.Json.Nodes.JsonObject o2)
                    liveLinkPairs.Add(o2["p"]!.GetValue<string>() + PairSep + o2["c"]!.GetValue<string>());
            var routeEditsByKey = new Dictionary<string, WarbandEdits.RouteEdit>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in Edits.AddRoute) routeEditsByKey[r.UpgradeKey] = r;   // 同 key 以最后一条为准（和落表一致）
            var listedRoute = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void EmitRoute(string k, string b, string t, string cost, int rr, int rs)
            {
                if (b.Length == 0 || t.Length == 0) return;
                if (k.Length > 0 && Edits.RemoveRoute.Contains(k, StringComparer.OrdinalIgnoreCase)) return;
                if (Edits.RemoveGroup.Contains(b, StringComparer.OrdinalIgnoreCase) ||
                    Edits.RemoveGroup.Contains(t, StringComparer.OrdinalIgnoreCase)) return;
                if (!listedRoute.Add(k + "\u0001" + b + "\u0001" + t)) return;
                routeListArr.Add(new System.Text.Json.Nodes.JsonObject
                {
                    ["k"] = k, ["b"] = b, ["t"] = t, ["c"] = cost,
                    ["ca"] = costAmount.TryGetValue(cost, out var amt3) ? amt3 : 0,
                    ["rr"] = rr, ["rs"] = rs,
                    ["link"] = liveLinkPairs.Contains(b + PairSep + t),
                });
            }
            if (routesT is not null)
            {
                var b3 = Col(routesT, "base_unit_group"); var t3 = Col(routesT, "target_unit_group");
                var k3 = Col(routesT, "upgrade_key"); var c3 = Col(routesT, "resource_cost");
                var r3 = Col(routesT, "required_rank"); var s3 = Col(routesT, "subtracted_rank");
                if (b3 >= 0 && t3 >= 0)
                    foreach (var row in routesT.Rows)
                    {
                        var k = k3 >= 0 ? row[k3].ToTsv() : "";
                        if (k.Length > 0 && routeEditsByKey.ContainsKey(k)) continue;   // 改过的 key 由下面统一发（改后的行）
                        EmitRoute(k, row[b3].ToTsv(), row[t3].ToTsv(), c3 >= 0 ? row[c3].ToTsv() : "",
                                  r3 >= 0 ? (int)row[r3].Int : 0, s3 >= 0 ? (int)row[s3].Int : 0);
                    }
            }
            foreach (var r in Edits.AddRoute)          // 本次新建 / 改金额 / 交换方向 / 改等级的
                EmitRoute(r.UpgradeKey, r.Base, r.Target, r.Cost, r.RequiredRank, r.SubtractedRank);
        }
        jso["routeList"] = routeListArr;

        jso["groups"] = groups;
        jso["icons"] = icons;
        jso["pages"] = pages;
        jso["links"] = keepLinks;
        jso["routes"] = ToJsonArray(routeSet.Select(PrettyPair));
        jso["diag"] = diag;
        var pageRaces = new System.Text.Json.Nodes.JsonObject();
        foreach (var kv in pageRaceSet) pageRaces[kv.Key] = ToJsonArray(kv.Value);
        var pageFactions = new System.Text.Json.Nodes.JsonObject();
        foreach (var kv in pageFacSet) pageFactions[kv.Key] = ToJsonArray(kv.Value);

        var skins = Skins(pack, session.PackPath);
        // 页签的图**按 twui 里实际引用的文件名**抽（改名会带 _N、老遗留可能不是 key）——
        // 按约定名抽的话，改名过的页签画布上显示的是另一个文件的内容（用户实测"工坊显示的不是我要的图"）
        var pageArt = pages.Select(p => p!.GetValue<string>())
                           .Where(c => !string.IsNullOrWhiteSpace(c))
                           .Select(c => (Cat: c,
                                         Bg: Path.GetFileName(ArtInnerOf("background_images_", c)),
                                         Btn: Path.GetFileName(ArtInnerOf("button_upgrade_", c))))
                           .ToList();
        var skin = skins.Prepare(pageArt);
        // 页面也要知道"这个页签实际用的文件名"（换图对话框的"保持当前"那格按它显示，别按 key 拼）
        var pageArtName = new System.Text.Json.Nodes.JsonObject();
        foreach (var (cat, bg, btn) in pageArt)
            pageArtName[cat] = new System.Text.Json.Nodes.JsonObject { ["bg"] = bg, ["btn"] = btn };
        jso["pageArtName"] = pageArtName;
        var pageBg = new System.Text.Json.Nodes.JsonObject();
        foreach (var p in pages)
        {
            var cat = p!.GetValue<string>();
            if (skins.Background(cat) is { } bg) pageBg[cat] = bg;
        }
        // 每个页签当前的**按钮图**也给页面：换图对话框要显示"现在这张"，而不是只写「跟母版一样」
        var pageBtn = new System.Text.Json.Nodes.JsonObject();
        foreach (var p in pages)
        {
            var cat = p!.GetValue<string>();
            if (skins.Button(cat) is { } btn) pageBtn[cat] = btn;
        }
        jso["pageBtn"] = pageBtn;
        var costArr = new System.Text.Json.Nodes.JsonArray();
        foreach (var k in costKeys)
            costArr.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["k"] = k,
                ["v"] = costAmount.TryGetValue(k, out var amt) ? amt : 0,
                // 额外资源（如 50 点骑士道）：画布在成本 key 后面一并显示
                ["pools"] = pools.TryGetValue(k, out var pl)
                    ? new System.Text.Json.Nodes.JsonArray(pl.Select(x =>
                        (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject
                        {
                            ["f"] = x.Factor, ["a"] = x.Amount, ["r"] = x.Res, ["i"] = x.Icon,
                        }).ToArray())
                    : new System.Text.Json.Nodes.JsonArray(),
            });
        // 换图/新建页签下拉里的素材 = **这个画布对应的包**里的 ui 图（背景/按钮），
        // 外加"待导出新增"的那几张（本地素材库只在左下那一页里，要进包先「添加到当前包」）。
        var art = new System.Text.Json.Nodes.JsonArray();
        {
            var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var tabKeysNow = TabKeys();          // 「在用」标记用：这一轮只算一次（tab key 要读 twui ~780KB）
            foreach (var (nk, _, _, _) in Edits.NewTabs)
                if (!tabKeysNow.Contains(nk, StringComparer.OrdinalIgnoreCase)) tabKeysNow.Add(nk);
            var supBg = ArtSuppliers("background_images_", tabKeysNow);
            var supBtn = ArtSuppliers("button_upgrade_", tabKeysNow);
            foreach (var e in pack.VisibleEntries)
            {
                var ap = e.Path.Replace(Path.DirectorySeparatorChar, '/');
                if (!ap.StartsWith("ui/skins/default/warband_upgrades/", StringComparison.OrdinalIgnoreCase)) continue;
                var an = Path.GetFileName(ap);
                var ak = an.StartsWith("background_images_", StringComparison.OrdinalIgnoreCase) ? "bg"
                       : an.Contains("button", StringComparison.OrdinalIgnoreCase) ? "btn" : "";
                if (ak.Length == 0 || !added.Add(ap)) continue;
                art.Add(new System.Text.Json.Nodes.JsonObject
                {
                    ["kind"] = ak, ["name"] = an, ["file"] = ap,
                    ["url"] = skins.ExtractForPage(ap, "art_" + an) ?? "", ["pending"] = false,
                    // 后台标记：现在是谁在用这张图（换图后旧标记会自动撤销；见 ArtSuppliers）
                    ["usedBy"] = (ak == "btn" ? supBtn : supBg).TryGetValue(ap, out var users) ? string.Join("、", users) : "",
                });
            }
            foreach (var (target, source) in Edits.FileReplacements)      // 待导出新增的也要能选到
            {
                if (!target.StartsWith("ui/skins/default/warband_upgrades/", StringComparison.OrdinalIgnoreCase)) continue;
                if (pack.Find(target) is not null || !added.Add(target)) continue;
                var an = Path.GetFileName(target);
                var ak = an.StartsWith("background_images_", StringComparison.OrdinalIgnoreCase) ? "bg"
                       : an.Contains("button", StringComparison.OrdinalIgnoreCase) ? "btn" : "";
                if (ak.Length == 0) continue;
                art.Add(new System.Text.Json.Nodes.JsonObject
                {
                    ["kind"] = ak, ["name"] = an, ["file"] = source,      // 来源（本地文件或包内路径）
                    ["url"] = (File.Exists(source) ? skins.StageLocalFile(source, "art_" + an)
                                                   : skins.ExtractForPage(source, "art_" + an)) ?? "",
                    ["pending"] = true,
                });
            }
        }
        var newTabs = new System.Text.Json.Nodes.JsonArray();
        foreach (var (tabKey, _, _, _) in Edits.NewTabs) newTabs.Add(tabKey);
        jso["newTabs"] = newTabs;
        jso["projectKey"] = ProjectKey ?? "";   // 画布「新建分组」默认名要用（<项目 key>_<页签>）
        foreach (var t in Edits.OpenedTabs) if (seenPages.Add(t)) pages.Add(t);   // 「打开页签」加进来的
        jso["openedTabs"] = ToJsonArray(Edits.OpenedTabs);
        // 页签右键「应用到其他种族」：手工归属（并进 pageRaces 一起给页面判断"这个页签归谁"）
        var tabScopes = new System.Text.Json.Nodes.JsonObject();
        foreach (var g in Edits.TabRaceScopes.GroupBy(x => x.Category, StringComparer.OrdinalIgnoreCase))
            tabScopes[g.Key] = ToJsonArray(g.Select(x => x.Race).Distinct(StringComparer.OrdinalIgnoreCase));
        jso["tabRaceScopes"] = tabScopes;
        // 供"应用到其他种族"的勾选列表：所有种族（key + 名字）
        try
        {
            var allRaces = new System.Text.Json.Nodes.JsonArray();
            foreach (var sub in LoadFactions().Item1.Select(f => f.Subculture)
                         .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var nm = Loc.Text("cultures_subcultures_name_" + sub) ?? sub;   // 种族名（loc 里没有就显示 key）
                allRaces.Add(new System.Text.Json.Nodes.JsonObject { ["r"] = sub, ["n"] = nm });
            }
            jso["allRaces"] = allRaces;
        }
        catch { }
        // 每个页签有多少组（「打开页签」对话框里显示，免得开出一堆空页签）
        var tabStats = new System.Text.Json.Nodes.JsonObject();
        foreach (var row in infos.Rows)
        {
            var c0 = cCol >= 0 ? row[cCol].ToTsv() : "";
            if (c0.Length == 0) continue;
            tabStats[c0] = (tabStats[c0]?.GetValue<int>() ?? 0) + 1;
        }
        jso["tabStats"] = tabStats;
        jso["artLibrary"] = art;
        jso["costKeys"] = costArr;
        // 画布的升级对话框也要能看出"这些成本属于哪个包"（多画布时最容易看错包 —— 用户实测"文件里有、这里没有"）
        jso["packName"] = session.Info?.FileName ?? Path.GetFileName(session.PackPath);
        jso["tabKeys"] = ToJsonArray(TabKeys());
        // 页签右键「从其他 mod 导入页签…」的来源：别的已加载包 + 它们的页签
        try
        {
            var modTabs = new System.Text.Json.Nodes.JsonArray();
            foreach (var (pk, nm, tabs) in ImportSources())
                modTabs.Add(new System.Text.Json.Nodes.JsonObject
                {
                    ["pack"] = pk, ["name"] = nm, ["tabs"] = ToJsonArray(tabs),
                });
            jso["modTabs"] = modTabs;
        }
        catch { }
        // 成本工坊的「选择资源」下拉：资源 key + 显示名 + 图标（原版打底 + 本包覆盖）
        try
        {
            var resArr = new System.Text.Json.Nodes.JsonArray();
            foreach (var r in PooledResources())
                resArr.Add(new System.Text.Json.Nodes.JsonObject { ["k"] = r.Key, ["n"] = r.Name, ["icon"] = r.IconUrl });
            jso["pooledResources"] = resArr;
        }
        catch { }
        jso["edits"] = Edits.Summary();
        // 画布上的兵也带中文名（兵牌下面显示的是中文，不再是 key 的尾段）
        var unitNames = new System.Text.Json.Nodes.JsonObject();
        foreach (var kv in unitsByGroup)
            foreach (var u in kv.Value)
                if (!unitNames.ContainsKey(u) && UnitName(u, LandOf(u)) is { } nm) unitNames[u] = nm;
        jso["names"] = unitNames;
        jso["unitExcl"] = unitExcl;
        jso["groupRaces"] = gRaces;
        jso["groupFactions"] = gFactions;
        jso["pageRaces"] = pageRaces;
        jso["pageFactions"] = pageFactions;
        jso["pageBg"] = pageBg;
        jso["skin"] = new System.Text.Json.Nodes.JsonObject(
            skin.Select(kv => new KeyValuePair<string, System.Text.Json.Nodes.JsonNode?>(kv.Key, kv.Value)));

        Log?.Invoke($"画布数据：{groups.Count} 组 / {unitsByGroup.Count} 组有兵 / 卡图命中 " +
                    $"{Icons.FromPack + Icons.FromGame + Icons.FromBundled}（包内 {Icons.FromPack}，游戏 {Icons.FromGame}，随附 {Icons.FromBundled}，缺 {Icons.Missing}）" +
                    (notes.Count > 0 ? "；" + string.Join("；", notes) : ""));
        if (posHisto.Count > 0)
            Log?.Invoke("连线出入口分布（子→父）：" + string.Join("，", posHisto.Select(kv => kv.Key + " " + kv.Value + " 条")));
        Log?.Invoke($"连线 {linkPairs.Count} 条 / 路线 {routePairs.Count} 条 → 有路线没连线 {routeNoLink.Count}，有连线没路线 {linkNoRoute.Count}（只报告，不自动改）");
        foreach (var x in routeNoLink.Take(10)) Log?.Invoke("   有路线没连线：" + PrettyPair(x));
        foreach (var x in linkNoRoute.Take(10)) Log?.Invoke("   有连线没路线：" + PrettyPair(x));
        if (Icons.NotInGame.Count > 0)
        {
            Log?.Invoke($"—— 以下 {Icons.NotInGame.Count} 张卡在游戏里没找到（用的随附素材），供核对：");
            foreach (var x in Icons.NotInGame) Log?.Invoke("   " + x);
        }
        _canvasCache = jso.ToJsonString();
        _canvasCacheKey = cacheKey;
        return _canvasCache;
    }

    /// <summary>
    /// 序章（prologue）的亚文化并回本体：`wh3_main_pro_sc_ksl_kislev` → `wh3_main_sc_ksl_kislev`。
    /// 原版把序章的恐虐/奸奇/基斯里夫单独列了三个 `_pro_sc_` 亚文化，里面的派系全是 `wh3_prologue_*`
    /// （基斯里夫远征军之类）——不并的话右侧会出现两个「恐虐」两个「基斯里夫」。
    /// </summary>
    private static string NormRace(string sub) =>
        sub.Replace("_pro_sc_", "_sc_", StringComparison.OrdinalIgnoreCase);

    /// <summary>按候选列名找列号（找不到 -1）。</summary>
    private static int Col(DbTable t, params string[] names)
    {
        foreach (var n in names)
        {
            var i = t.Columns.FindIndex(c => c.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) return i;
        }
        return -1;
    }

    /// <summary>成对 key 的分隔符（组对：base␁target / parent␁child）。</summary>
    private const char PairSep = '\u0001';

    /// <summary>
    /// 兵种中文名：`land_units_onscreen_name_&lt;land_unit&gt;`（老工坊 make_namer 的约定），
    /// 找不到试 `_driver`，再逐级剥掉尾段（MOD 的 `_ror_1` / `_3` 这类常常没有单独词条）。
    /// </summary>
    private string? UnitName(string mainKey, string landUnit)
    {
        string? Try(string u) => u.Length == 0
            ? null
            : Loc.Text("land_units_onscreen_name_" + u) ?? Loc.Text("land_units_onscreen_name_" + u + "_driver");

        var hit = Try(landUnit) ?? Try(mainKey);
        if (hit is not null) return hit;
        foreach (var src in new[] { landUnit, mainKey })
        {
            if (src.Length == 0) continue;
            var parts = src.Split('_');
            for (var cut = 1; cut <= 2 && parts.Length > cut + 2; cut++)
                if (Try(string.Join('_', parts[..^cut])) is { } v) return v;
        }
        return null;
    }

    private static string PrettyPair(string pair)
    {
        var i = pair.IndexOf(PairSep);
        return i < 0 ? pair : pair[..i] + " → " + pair[(i + 1)..];
    }

    private static System.Text.Json.Nodes.JsonArray ToJsonArray(IEnumerable<string> items)
    {
        var a = new System.Text.Json.Nodes.JsonArray();
        foreach (var x in items) a.Add(x);
        return a;
    }

    /// <summary>右上：种族（subculture）→ 派系（faction key）两级列表，实时读 DB（原版打底 + 本包覆盖）。</summary>
    public string BuildFactionJson()
    {
        var (factions, allUnits) = LoadFactions();
        var races = new System.Text.Json.Nodes.JsonArray();
        foreach (var g in factions.GroupBy(f => f.Subculture).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var (milByFac, milByRace) = MilGroups();
            var fs = new System.Text.Json.Nodes.JsonArray();
            foreach (var f in g.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                milByFac.TryGetValue(f.Key, out var mg);
                fs.Add(new System.Text.Json.Nodes.JsonObject
                {
                    ["key"] = f.Key,
                    ["name"] = f.Name,                                   // 中文名（查不到是 null）
                    ["legendary"] = f.Legendary,
                    ["units"] = f.Units.Count,
                    ["milGroup"] = mg?.Group,                            // factions_tables.military_group
                    ["milGeneric"] = mg?.Generic ?? false,               // 是否属于通用军事组
                });
            }
            races.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["race"] = g.Key,
                ["name"] = Loc.Text("cultures_subcultures_name_" + g.Key)
                           ?? Loc.Text("cultures_subcultures_confederation_screen_name_" + g.Key),
                ["units"] = g.SelectMany(f => f.Units).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                ["genericUnits"] = g.Where(f => !f.Legendary).SelectMany(f => f.Units)
                                    .Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                ["milGroups"] = milByRace.TryGetValue(g.Key, out var mgs) ? ToJsonArray(mgs) : new System.Text.Json.Nodes.JsonArray(),
                ["factions"] = fs,
            });
        }
        Log?.Invoke($"派系列表：{races.Count} 个种族 / {factions.Count} 个派系（传奇 {factions.Count(f => f.Legendary)}，有兵 {factions.Count(f => f.Units.Count > 0)}）");
        return new System.Text.Json.Nodes.JsonObject { ["races"] = races, ["total"] = allUnits.Count }.ToJsonString();
    }

    /// <summary>右下：兵种库（实时读 DB、按派系分类）。faction 传派系 key、或 "race:种族"；空 = 全部兵。</summary>
    public string BuildUnitLibraryJson(string? faction)
    {
        var pack = _pack?.Archive ?? throw new InvalidOperationException("先打开一个 pack。");
        PrepareIcons(pack);
        var (factions, allUnits) = LoadFactions();
        string label;
        List<string> units;
        if (string.IsNullOrWhiteSpace(faction)) { units = allUnits; label = "全部"; }
        else if (faction.StartsWith("race:", StringComparison.OrdinalIgnoreCase)
              || faction.StartsWith("generic:", StringComparison.OrdinalIgnoreCase))
        {
            // race: 种族全部；generic: 该种族里的**非传奇**派系（界面上的「通用」）
            var generic = faction.StartsWith("generic:", StringComparison.OrdinalIgnoreCase);
            var race = faction[(generic ? "generic:" : "race:").Length..];
            label = race + (generic ? "（通用）" : "");
            units = factions.Where(f => f.Subculture.Equals(race, StringComparison.OrdinalIgnoreCase) && (!generic || !f.Legendary))
                            .SelectMany(f => f.Units)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                            .ToList();
        }
        else
        {
            label = faction;
            units = factions.FirstOrDefault(f => f.Key.Equals(faction, StringComparison.OrdinalIgnoreCase))?.Units
                // 直接给种族 key（不带 race: 前缀）也认：找不到同名派系就按种族并集算
                ?? factions.Where(f => f.Subculture.Equals(faction, StringComparison.OrdinalIgnoreCase))
                           .SelectMany(f => f.Units)
                           .Distinct(StringComparer.OrdinalIgnoreCase)
                           .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                           .ToList();
        }
        return UnitsLibraryJson(label, units);
    }

    /// <summary>把一串兵做成"兵种库"JSON（图标 / 中文名 / 已在画布上）——种族页和 mod 兵种页共用。</summary>
    private string UnitsLibraryJson(string label, List<string> units)
    {
        var arr = new System.Text.Json.Nodes.JsonArray();
        var icons = new System.Text.Json.Nodes.JsonObject();
        var names = new System.Text.Json.Nodes.JsonObject();
        foreach (var u in units)
        {
            arr.Add(u);
            if (Icons.Url(u, countMiss: false) is { } url) icons[u] = url;
            if (UnitName(u, LandOf(u)) is { } nm) names[u] = nm;
        }
        var inTree = CanvasUnits();
        var inTreeArr = new System.Text.Json.Nodes.JsonArray();
        foreach (var u in units) if (inTree.Contains(u)) inTreeArr.Add(u);
        Log?.Invoke($"兵种库：{label} → {arr.Count} 个兵（有图 {icons.Count}，有中文名 {names.Count}，已在画布上 {inTreeArr.Count}）");
        return new System.Text.Json.Nodes.JsonObject
        {
            ["faction"] = label,
            ["units"] = arr,
            ["icons"] = icons,
            ["names"] = names,
            ["inTree"] = inTreeArr,
        }.ToJsonString();
    }

    /// <summary>
    /// 这个包的 `main_units_tables` 有哪些**文件**（"mod 兵种"页按文件分：
    /// 选中哪一个就显示哪一个的兵 —— 例如 zz 开头和 !!!snek 开头的两张各自成表）。
    /// </summary>
    public List<(string Path, string Name)> ModUnitTables(string packPath)
    {
        var list = new List<(string, string)>();
        try
        {
            var session = _packs.FirstOrDefault(s => (s.PackPath ?? "").Equals(packPath, StringComparison.OrdinalIgnoreCase)) ?? _pack;
            var pack = session?.Archive;
            if (pack is null) return list;
            foreach (var e in TableFiles.EntriesFor(pack, "main_units_tables"))
                list.Add((e.Path, Path.GetFileName(e.Path)));
        }
        catch (Exception ex) { Log?.Invoke("mod 表清单跳过：" + ex.Message); }
        return list;
    }

    /// <summary>打开的包（"mod 兵种"页上半部分）：包内路径 + 显示名。</summary>
    /// <summary>
    /// 「mod 兵种」页列哪些：**只列明确"加载为兵种 mod"的包**（用户口径：不要自动把带兵的战帮包塞进来）。
    /// </summary>
    public List<(string Key, string Name)> OpenMods() =>
        _packs.Where(s => s.IsUnitPack && !IsOwnBundledPack(s))
              .Select(s => (s.PackPath ?? s.DisplayName, s.DisplayName)).ToList();

    /// <summary>
    /// 是不是"工具自己生成/自带"的包（`&lt;exe&gt;/packs/WUU战帮升级.pack` 那个）：
    /// 它是启动时自动打开的战帮参考包，**不该出现在「mod 兵种」页**（用户口径）。
    /// </summary>
    private static bool IsOwnBundledPack(PackSession s)
    {
        try
        {
            var p = (s.PackPath ?? "").Trim();
            if (p.Length == 0) return false;
            var full = Path.GetFullPath(p);
            // 工具自带的包 = exe 旁 packs/（老版生成的源包）或 bundled/（随包素材）里那些。
            // v1.5.0 起 exe 旁不再自动生成 WUU 包；**工程目录里的同名包是用户的包**（要能出现在 mod 列表里）。
            foreach (var d in new[] { Path.Combine(AppContext.BaseDirectory, "packs"),
                                      Path.Combine(AppContext.BaseDirectory, "bundled") })
            {
                if (full.StartsWith(Path.GetFullPath(d) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
        catch { return false; }
    }

    /// <summary>某个包（含当前包）的页签 key 清单：twui 的 holder_tab_* + categories 行。</summary>
    public List<string> TabKeysOf(PackArchive pack)
    {
        var keys = new List<string>();
        try
        {
            var e = pack.VisibleEntries.FirstOrDefault(x =>
                x.Path.EndsWith("warband_upgrades.twui.xml", StringComparison.OrdinalIgnoreCase));
            if (e is not null)
                keys = WarbandNewTab.ExistingTabs(System.Text.Encoding.UTF8.GetString(pack.ReadDecoded(e)));
            var cats = TableFiles.ReadMerged(pack, "unit_upgrade_group_ui_categories_tables", GetSchema(), null);
            if (cats is not null)
                foreach (var r in cats.Rows)
                {
                    var v = r[0].ToTsv();
                    if (v.Length > 0 && !keys.Contains(v, StringComparer.OrdinalIgnoreCase)) keys.Add(v);
                }
        }
        catch { }
        return keys;
    }

    /// <summary>
    /// 可以"导入页签"的来源：**除当前包之外的已加载包**（战帮包 / 兵种包都行）：包路径 + 显示名 + 页签清单。
    /// 画布页签右键的「从其他 mod 导入页签…」用它。
    /// </summary>
    public List<(string Pack, string Name, List<string> Tabs)> ImportSources()
    {
        var list = new List<(string, string, List<string>)>();
        foreach (var s in _packs)
        {
            if (ReferenceEquals(s, _pack) || s.Archive is null) continue;
            var tabs = TabKeysOf(s.Archive);
            if (tabs.Count > 0) list.Add((s.PackPath ?? s.DisplayName, s.DisplayName, tabs));
        }
        return list;
    }

    /// <summary>
    /// 从别的已加载 mod 导入一个页签：**键 + 两张图（背景/按钮）取自来源包**；
    /// 页签结构（twui 块）用当前包里自动挑的母版克隆 —— 复用「新建页签」那条已经验收过的路，
    /// 不把来源包的 twui 块原样搬（两个包的 twui 结构可能不同，硬搬容易把文件写坏）。
    /// 已经有的同名页签会跳过。
    /// </summary>
    public string ImportTab(string fromPackPath, string key, string? race, string? faction)
    {
        key = (key ?? "").Trim().ToUpperInvariant();
        if (key.Length == 0) return "页签 key 为空";
        var cur = _pack?.Archive;
        if (cur is null) return "先打开一个战帮包";
        var src = _packs.FirstOrDefault(s => (s.PackPath ?? "").Equals(fromPackPath, StringComparison.OrdinalIgnoreCase));
        if (src?.Archive is null) return $"找不到来源包：{fromPackPath}";
        if (TabKeysOf(cur).Any(t => t.Equals(key, StringComparison.OrdinalIgnoreCase)))
            return $"当前包里已经有页签 {key} 了（跳过）";

        // 来源包的两张图 → 落成"本地素材文件"（新建页签那条路本来就能从本地文件取图）
        string? bg = null, btn = null;
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                   "WarbandStudio", "importcache");
            Directory.CreateDirectory(dir);
            foreach (var (kind, set) in new[]
            {
                ("background_images_", (Action<string>)(f => bg = f)),
                ("button_upgrade_", (Action<string>)(f => btn = f)),
            })
            {
                var low = key.ToLowerInvariant();
                var entry = src.Archive.VisibleEntries.FirstOrDefault(e =>
                    e.Path.EndsWith("/" + kind + low + ".png", StringComparison.OrdinalIgnoreCase));
                if (entry is null) continue;
                var f = Path.Combine(dir, kind + low + ".png");
                File.WriteAllBytes(f, src.Archive.ReadDecoded(entry));
                set(f);
            }
        }
        catch (Exception ex) { Log?.Invoke("导入页签取图失败：" + ex.Message); }

        NewTab(key, donor: "", bg, btn);              // ← 复用新建页签那条路（自动空组/预览/日志都在里面）
        var note = $"从 {src.DisplayName} 导入页签 {key}（母版用当前包的、背景 {(bg is null ? "来源包没有" : "已取")} / " +
                   $"按钮 {(btn is null ? "来源包没有" : "已取")}；归属 {(faction ?? race ?? "（没选种族/派系，默认全种族）")}）　待导出";
        Log?.Invoke(note);
        return note;
    }

    /// <summary>
    /// "mod 兵种"页的兵种库：列出**这个包自己加的兵**（在它的 main_units_tables 里、原版没有的 key）。
    /// 卡片、中文名、图标、拖拽都用和种族页兵种库同一套（同一个 UnitCard 数据 + 同一个 ListBox 事件）。
    /// </summary>
    public string BuildModUnitLibraryJson(string packPath, string? tablePath = null)
    {
        var session = _packs.FirstOrDefault(s => (s.PackPath ?? "").Equals(packPath, StringComparison.OrdinalIgnoreCase)) ?? _pack;
        var pack = session?.Archive ?? throw new InvalidOperationException("先打开一个 pack。");
        PrepareIcons(pack);
        var schema = GetSchema();
        var notes = new List<string>();
        PackArchive? vp = null;
        var vanilla = VanillaDbPackPath;
        if (vanilla is not null && File.Exists(vanilla)) vp = PackArchive.Open(vanilla);
        try
        {
            var vanillaKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (vp is not null)
            {
                var vt = TableFiles.ReadMerged(vp, "main_units_tables", schema, notes);
                if (vt is not null)
                {
                    var vk = Col(vt, "unit");
                    if (vk < 0) vk = TableFiles.FindKeyColumn(vt);
                    if (vk >= 0) foreach (var r in vt.Rows) { var v = r[vk].ToTsv(); if (v.Length > 0) vanillaKeys.Add(v); }
                }
            }
            var units = new List<string>();
            if (!string.IsNullOrWhiteSpace(tablePath))
            {
                // 只看某一个表文件（"选中哪一个就显示哪一个的兵"）：这张表的行**全列**（不按原版过滤），
                // 因为适配表常常是"给别的 mod 的兵改数据"，那些兵本来就不在原版里，用户要能一眼看全
                var entry = pack.Find(tablePath) ?? pack.VisibleEntries.FirstOrDefault(x =>
                    string.Equals(x.Path.Replace('/', Path.DirectorySeparatorChar),
                                  tablePath!.Replace('/', Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase));
                if (entry is not null)
                {
                    var ft = DbTable.Decode(pack.ReadDecoded(entry), "main_units_tables", schema);
                    var fk = ft.Columns.FindIndex(x => x.Name.Equals("unit", StringComparison.OrdinalIgnoreCase));
                    if (fk < 0) fk = TableFiles.FindKeyColumn(ft);
                    if (fk >= 0)
                        foreach (var r in ft.Rows)
                        {
                            var u = r[fk].ToTsv();
                            if (u.Length > 0) units.Add(u);
                        }
                    return UnitsLibraryJson(Path.GetFileName(tablePath!), units
                        .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList());
                }
            }
            var t = TableFiles.ReadMerged(pack, "main_units_tables", schema, notes);
            if (t is not null)
            {
                var k = Col(t, "unit");
                if (k < 0) k = TableFiles.FindKeyColumn(t);
                if (k >= 0)
                    foreach (var r in t.Rows)
                    {
                        var u = r[k].ToTsv();
                        if (u.Length == 0 || vanillaKeys.Contains(u)) continue;   // 只列这个 mod 新加的兵
                        units.Add(u);
                    }
            }
            units = units.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            return UnitsLibraryJson(session?.DisplayName ?? "mod", units);
        }
        finally { vp?.Dispose(); }
    }

    /// <summary>当前画布上已经有的兵（兵种库里要把它们变灰，避免同一个兵重复进树）。</summary>
    public HashSet<string> CanvasUnits()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var pack = _pack?.Archive;
            if (pack is null) return set;
            var t = TableFiles.ReadConcat(pack, "unit_to_unit_group_junctions_tables", GetSchema(), null);
            if (t is not null)
            {
                var ju = Col(t, "unit"); var jg = Col(t, "unit_group");
                if (ju >= 0 && jg >= 0)
                    foreach (var r in t.Rows)
                    {
                        var u = r[ju].ToTsv(); var g = r[jg].ToTsv();
                        if (u.Length == 0 || g.Length == 0) continue;
                        if (Edits.RemoveGroup.Contains(g, StringComparer.OrdinalIgnoreCase)) continue;
                        set.Add(u);
                    }
            }
            foreach (var (u, g) in Edits.RemoveJunction) set.Remove(u);          // 画布上删掉的
            foreach (var (u, g) in Edits.AddJunction) set.Add(u);               // 画布上加进去的
        }
        catch (Exception ex) { Log?.Invoke("画布兵种集合跳过：" + ex.Message); }
        return set;
    }

    /// <summary>main_units key → land_units key（兵种名/兵种类别都要走这一步）。</summary>
    private string LandOf(string mainKey)
    {
        if (_landUnitByUnit is not null) return _landUnitByUnit.TryGetValue(mainKey, out var lu) ? lu : "";
        _landUnitByUnit = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var pack = _pack?.Archive;
            if (pack is null) return "";
            var schema = GetSchema();
            var notes = new List<string>();
            PackArchive? vp = null;
            var vanilla = VanillaDbPackPath;
            if (vanilla is not null && File.Exists(vanilla)) vp = PackArchive.Open(vanilla);
            try
            {
                var main = TableFiles.Merge(
                    vp is null ? null : TableFiles.ReadMerged(vp, "main_units_tables", schema, notes),
                    TableFiles.ReadMerged(pack, "main_units_tables", schema, notes));
                if (main is not null)
                {
                    var mu = Col(main, "unit");
                    if (mu < 0) mu = TableFiles.FindKeyColumn(main);
                    var ml = Col(main, "land_unit");
                    if (mu >= 0 && ml >= 0)
                        foreach (var r in main.Rows)
                        {
                            var k = r[mu].ToTsv();
                            if (k.Length > 0) _landUnitByUnit[k] = r[ml].ToTsv();
                        }
                }
            }
            finally { vp?.Dispose(); }
        }
        catch (Exception ex) { Log?.Invoke("land_unit 映射失败：" + ex.Message); }
        return _landUnitByUnit.TryGetValue(mainKey, out var v) ? v : "";
    }

    private Dictionary<string, string>? _landUnitByUnit;

    private (List<FactionUnits> Factions, List<string> All)? _factionCache;
    private string? _factionCacheKey;

    /// <summary>派系 → 军事组 的分类结果（通用/专属 + 该种族的通用组），以及种族 → 该种族的全部军事组。</summary>
    public sealed record MilGroupInfo(string Faction, string Race, string Group, bool Generic);
    private Dictionary<string, MilGroupInfo>? _milGroups;
    private Dictionary<string, List<string>>? _raceMilGroups;

    /// <summary>
    /// 军事组归属分类。链路：factions_tables.military_group（派系 → 军事组），
    /// 传奇判定用 frontend_factions_tables（排除 prologue）。缓存到 %APPDATA%\WarbandStudio\military_groups.json，
    /// 打开新包时重算一遍（表里有新增会体现出来）。
    /// </summary>
    public (Dictionary<string, MilGroupInfo> ByFaction, Dictionary<string, List<string>> ByRace) MilGroups()
    {
        if (_milGroups is not null && _raceMilGroups is not null) return (_milGroups, _raceMilGroups);
        var byFac = new Dictionary<string, MilGroupInfo>(StringComparer.OrdinalIgnoreCase);
        var byRace = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var pack = _pack?.Archive ?? throw new InvalidOperationException("先打开一个 pack。");
            var schema = GetSchema();
            var notes = new List<string>();
            PackArchive? vp = null;
            var vanilla = VanillaDbPackPath;
            if (vanilla is not null && File.Exists(vanilla)) vp = PackArchive.Open(vanilla);
            try
            {
                DbTable? M(string t) => TableFiles.Merge(
                    vp is null ? null : TableFiles.ReadMerged(vp, t, schema, notes),
                    TableFiles.ReadMerged(pack, t, schema, notes));
                var fac = M("factions_tables") ?? throw new InvalidDataException("没有 factions_tables");
                var front = M("frontend_factions_tables");
                var legendary = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (front is not null)
                {
                    var ff = Col(front, "faction", "key");
                    if (ff >= 0)
                        foreach (var r in front.Rows)
                        {
                            var k = r[ff].ToTsv();
                            if (k.Length > 0 && !k.Contains("prologue", StringComparison.OrdinalIgnoreCase)) legendary.Add(k);
                        }
                }
                var fk = Col(fac, "key"); if (fk < 0) fk = TableFiles.FindKeyColumn(fac);
                var fs = Col(fac, "subculture"); var fm = Col(fac, "military_group");
                // 按种族收集：传奇的组计数 + 全体的组计数（1:1 时退回用）
                var legendCount = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
                var allCount = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
                var raceOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var groupOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var r in fac.Rows)
                {
                    var key = r[fk].ToTsv();
                    if (key.Length == 0) continue;
                    var race = (fs >= 0 ? r[fs].ToTsv() : "").Replace("_pro_sc_", "_sc_", StringComparison.OrdinalIgnoreCase);
                    var mg = fm >= 0 ? r[fm].ToTsv() : "";
                    if (race.Length == 0 || mg.Length == 0) continue;
                    if (!allCount.TryGetValue(race, out var ac)) allCount[race] = ac = new(StringComparer.OrdinalIgnoreCase);
                    ac[mg] = ac.TryGetValue(mg, out var n0) ? n0 + 1 : 1;
                    raceOf[key] = race; groupOf[key] = mg;
                    if (!legendary.Contains(key)) continue;
                    if (!legendCount.TryGetValue(race, out var lc)) legendCount[race] = lc = new(StringComparer.OrdinalIgnoreCase);
                    lc[mg] = lc.TryGetValue(mg, out var n1) ? n1 + 1 : 1;
                }
                foreach (var kv in allCount)
                {
                    var lc = legendCount.TryGetValue(kv.Key, out var v) ? v : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    var pick = lc.Count > 0 && lc.Values.Max() > 1 ? lc : kv.Value;     // 传奇里有多人共用的组才算通用；全是 1:1 就退回全体
                    var generic = pick.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase).First().Key;
                    byRace[kv.Key] = kv.Value.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                    foreach (var g in kv.Value.Keys)
                        foreach (var f in raceOf.Where(x => x.Value.Equals(kv.Key, StringComparison.OrdinalIgnoreCase)
                                                          && groupOf[x.Key].Equals(g, StringComparison.OrdinalIgnoreCase)).Select(x => x.Key))
                            byFac[f] = new MilGroupInfo(f, kv.Key, g, g.Equals(generic, StringComparison.OrdinalIgnoreCase));
                }
                try
                {
                    var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WarbandStudio");
                    Directory.CreateDirectory(dir);
                    File.WriteAllText(Path.Combine(dir, "military_groups.json"),
                        System.Text.Json.JsonSerializer.Serialize(
                            byFac.Values.Select(v => new { v.Faction, v.Race, v.Group, v.Generic }),
                            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                }
                catch { /* 缓存写不进去不影响使用 */ }
                Log?.Invoke($"军事组：{byFac.Count} 个派系 / {byRace.Count} 个种族（已缓存 military_groups.json）");
            }
            finally { vp?.Dispose(); }
        }
        catch (Exception ex) { Log?.Invoke("军事组分类跳过：" + ex.Message); }
        _milGroups = byFac;
        _raceMilGroups = byRace;
        return (byFac, byRace);
    }

    /// <summary>
    /// 派系 → 可用兵（实时读 DB；原版 db.pack 打底，本包覆盖）。三路合并：
    ///   ① factions.military_group ↔ units_to_groupings_military_permissions_tables（军事编组，主路）
    ///   ② units_to_exclusive_faction_permissions_tables（专属授权，allowed = true）
    ///   ③ building_units_allowed_tables（faction 列 + enabled = true，招募来源）
    /// 结果与 main_units_tables 求交，只留真正存在的兵。
    /// </summary>
    private (List<FactionUnits> Factions, List<string> All) LoadFactions()
    {
        var pack = _pack?.Archive ?? throw new InvalidOperationException("先打开一个 pack。");
        var key = _pack!.PackPath + "|" + (settings.GameDir ?? "");
        if (_factionCache is not null && _factionCacheKey == key) return _factionCache.Value;

        var schema = GetSchema();
        var notes = new List<string>();
        PackArchive? vp = null;
        try
        {
            var vanilla = VanillaDbPackPath;
            if (vanilla is not null && File.Exists(vanilla)) vp = PackArchive.Open(vanilla);
            DbTable? M(string table) => TableFiles.Merge(
                vp is null ? null : TableFiles.ReadMerged(vp, table, schema, notes),
                TableFiles.ReadMerged(pack, table, schema, notes));

            // ★ 军事组授权（units_to_groupings_military_permissions_tables）：**原版为基底 + 本包新增/覆盖重复**，
            //   而且必须按 (unit, military_group) **组合键**合并 —— 这张表 schema 的键只标了 unit，
            //   但一个兵可以同时挂多个军事组（原版 5340 行里大量是一个兵好几条）。用上面那个 M()（按键合并）
            //   会：① 本包的行把原版同一个兵的**其它组**顶掉；② 同一个兵的多组只剩一条。
            //   实测原版 5340 行被并成 1945 条 —— 表现就是"打开 mod 包以后兵全变成不属于当前军事组（黄标）"。
            DbTable? C(string table, string[] keyCols) => TableFiles.MergeComposite(
                vp is null ? null : TableFiles.ReadComposite(vp, table, keyCols, schema, notes),
                TableFiles.ReadComposite(pack, table, keyCols, schema, notes), keyCols);

            var fac = M("factions_tables")
                      ?? throw new InvalidDataException("读不到 factions_tables：需要在「全局选项」里设游戏目录（原版 data/db.pack）。");
            // 传奇派系 = 新战役界面里能选的（frontend_factions_tables）；其余归「通用」
            var frontend = M("frontend_factions_tables");
            var grouping = C("units_to_groupings_military_permissions_tables", ["unit", "military_group"]);
            // 自检行：这张表的条数直接决定"兵属于哪个军事组"判得准不准，出问题先看这行
            if (grouping is not null)
            {
                var uc = Col(grouping, "unit");
                var gc = Col(grouping, "military_group");
                if (uc >= 0 && gc >= 0)
                {
                    var uset = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var pset = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var r in grouping.Rows)
                    {
                        var u = r[uc].ToTsv(); var g = r[gc].ToTsv();
                        if (u.Length == 0 || g.Length == 0) continue;
                        uset.Add(u); pset.Add(u + "\u0001" + g);
                    }
                    Log?.Invoke($"军事组授权：{uset.Count} 个兵 / {pset.Count} 条（原版打底 + 本包新增/覆盖重复，按 unit+military_group 组合键）");
                }
            }
            // 专属授权同理：schema 的键是 key（= 兵），但真实身份是 (key, faction) —— 原版 1489 行按键合并只剩 378 条
            // （丢 75%），兵就会"不属于"它本该属于的派系。合并口径与军事组一致：原版打底 + 本包新增/覆盖重复。
            var excl = C("units_to_exclusive_faction_permissions_tables", ["key", "faction"]);
            var bua = M("building_units_allowed_tables");   // 这张实测首列就唯一（6867=6867），不用改
            var main = M("main_units_tables");
            var land = M("land_units_tables");

            // 英雄/领主 = land_units.class == "com"（原版 1398 个 _cha_ 全在这个类）；
            // 兵种库只列普通部队，所以按 land_unit → class 把 com 剔掉。
            var charUnit = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var nameByUnit = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (main is not null && land is not null)
            {
                var mu0 = Col(main, "unit");
                if (mu0 < 0) mu0 = TableFiles.FindKeyColumn(main);
                var ml0 = Col(main, "land_unit");
                var lk0 = Col(land, "land_unit");
                if (lk0 < 0) lk0 = TableFiles.FindKeyColumn(land);
                var lc0 = Col(land, "class");
                var classByLand = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (mu0 >= 0 && ml0 >= 0 && lk0 >= 0 && lc0 >= 0)
                    foreach (var r in land.Rows)
                    {
                        var k = r[lk0].ToTsv();
                        if (k.Length > 0) classByLand[k] = r[lc0].ToTsv();
                    }
                if (mu0 >= 0 && ml0 >= 0)
                    foreach (var r in main.Rows)
                    {
                        var u = r[mu0].ToTsv();
                        var lu = r[ml0].ToTsv();
                        if (u.Length == 0) continue;
                        if (classByLand.TryGetValue(lu, out var cls) && cls.Equals("com", StringComparison.OrdinalIgnoreCase))
                            charUnit.Add(u);
                        var nm = UnitName(u, lu);
                        if (nm is not null) nameByUnit[u] = nm;
                    }
                Log?.Invoke($"兵种库：英雄/领主 {charUnit.Count} 个（land_units.class=com）已剔除；中文名 {nameByUnit.Count} 条");
            }

            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (main is not null)
            {
                var mu = Col(main, "unit");
                if (mu < 0) mu = TableFiles.FindKeyColumn(main);
                if (mu >= 0) foreach (var r in main.Rows) { var u = r[mu].ToTsv(); if (u.Length > 0) known.Add(u); }
            }

            static Dictionary<string, HashSet<string>> Bucket(DbTable? t, int unitCol, int groupCol, int flagCol)
            {
                var d = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                if (t is null || unitCol < 0 || groupCol < 0) return d;
                foreach (var r in t.Rows)
                {
                    if (flagCol >= 0)
                    {
                        var v = r[flagCol].ToTsv();
                        if (!v.Equals("true", StringComparison.OrdinalIgnoreCase) && v != "1") continue;
                    }
                    var u = r[unitCol].ToTsv();
                    var g = r[groupCol].ToTsv();
                    if (u.Length == 0 || g.Length == 0) continue;
                    if (!d.TryGetValue(g, out var set)) d[g] = set = new(StringComparer.OrdinalIgnoreCase);
                    set.Add(u);
                }
                return d;
            }

            var byGroup = grouping is null ? new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
                                           : Bucket(grouping, Col(grouping, "unit"), Col(grouping, "military_group"), -1);
            var byExclusive = excl is null ? new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
                                           : Bucket(excl, Col(excl, "key"), Col(excl, "faction"), Col(excl, "allowed"));
            var byBuilding = bua is null ? new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
                                         : Bucket(bua, Col(bua, "unit"), Col(bua, "faction"), Col(bua, "enabled"));

            var fk = Col(fac, "key");
            if (fk < 0) fk = TableFiles.FindKeyColumn(fac);
            var fs = Col(fac, "subculture");
            var fm = Col(fac, "military_group");
            var legendary = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (frontend is not null)
            {
                var ff = Col(frontend, "faction", "key");
                if (ff >= 0)
                    foreach (var r in frontend.Rows)
                    {
                        var k = r[ff].ToTsv();
                        // 序章专属派系（wh3_prologue_*，如「基斯里夫远征军」）不算传奇：它们不是大战役里能选的势力，
                        // 一律并进该种族的「通用」里。
                        if (k.Length == 0 || k.Contains("prologue", StringComparison.OrdinalIgnoreCase)) continue;
                        legendary.Add(k);
                    }
            }

            var list = new List<FactionUnits>();
            var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in fac.Rows)
            {
                var fkey = r[fk].ToTsv();
                if (fkey.Length == 0) continue;
                var sub = fs >= 0 ? r[fs].ToTsv() : "";
                sub = NormRace(sub);                             // 序章亚文化并回本体（不然会出现两个「恐虐」「基斯里夫」）
                var mg = fm >= 0 ? r[fm].ToTsv() : "";
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (mg.Length > 0 && byGroup.TryGetValue(mg, out var g1)) set.UnionWith(g1);
                if (byExclusive.TryGetValue(fkey, out var g2)) set.UnionWith(g2);
                if (byBuilding.TryGetValue(fkey, out var g3)) set.UnionWith(g3);
                if (known.Count > 0) set.IntersectWith(known);
                set.ExceptWith(charUnit);                       // 英雄/领主不进兵种库
                list.Add(new FactionUnits(
                    fkey, sub,
                    Loc.Text("factions_screen_name_" + fkey),        // 中文名（查不到就 null → 界面显示 Key）
                    legendary.Contains(fkey),
                    set.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList()));
                all.UnionWith(set);
            }

            _factionCache = (list, all.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList());
            _factionCacheKey = key;
            Log?.Invoke($"派系库：{list.Count} 个派系（传奇 {list.Count(f => f.Legendary)}）/ {list.Count(f => f.Units.Count > 0)} 个有兵；兵种并集 {all.Count}（main_units {known.Count}）" +
                        (notes.Count > 0 ? "；" + string.Join("；", notes.Take(3)) : ""));
            return _factionCache.Value;
        }
        finally { vp?.Dispose(); }
    }

    /// <summary>诊断（走 rpfm_cli：坏引用/非法枚举/空 key…）。</summary>：坏引用/非法枚举/空 key…）。</summary>
    public async Task<string> DiagnosticsAsync(CancellationToken ct = default)
    {
        var cli = await EnsureEngineAsync(ct);
        var pack = _pack?.PackPath ?? throw new InvalidOperationException("先打开一个 pack 再跑诊断。");
        var r = await cli.DiagnoseAsync([pack], ct);
        if (!r.Ok) throw new RpfmException(r.All);
        return r.StdOut;
    }

    /// <summary>生成依赖缓存（diagnose 的前置；要读一遍游戏 data）。</summary>
    public async Task<string> GenerateDependenciesAsync(CancellationToken ct = default)
    {
        var cli = await EnsureEngineAsync(ct);
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WarbandStudio", "cache");
        Directory.CreateDirectory(dir);
        var cache = Path.Combine(dir, "dependencies_wh3.pak2");
        var r = await cli.GenerateDependenciesAsync(cache, ct);
        if (!r.Ok) throw new RpfmException(r.All);
        cli.DependenciesCachePath = cache;
        return cache;
    }

    public async ValueTask DisposeAsync()
    {
        if (_pack is not null) await _pack.CloseAsync();
        _pack = null;
        _cli = null;
    }

/// <summary>派系 → 可用兵（实时从 DB 算，来自 LoadFactions）。</summary>
public sealed record FactionUnits(
    string Key,
    string Subculture,
    string? Name,          // 中文显示名（factions_screen_name_<key>，查不到就是 null）
    bool Legendary,        // 是否传奇：出现在 frontend_factions_tables = 新战役可选
    List<string> Units);
}
