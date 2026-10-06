using System.Text.Json;
using WarbandStudio.Pack;
using WarbandStudio.Packfile;
using WarbandStudio.Rpfm;

namespace WarbandStudio.Probe;

/// <summary>
/// 全量编辑落表验收：在真实包上跑一遍"新建组+加兵、新建升级+连线、新建成本、改页签、删连线/删升级/删兵"，
/// 导出后把期望值写进 &lt;dest&gt;.plan.json，交给 verify_native.py 用 rpfm_cli 读回核对。
/// </summary>
public static class AmendTestRunner
{
    public static int Run(string[] a)
    {
        if (a.Length < 4) { Console.Error.WriteLine("用法: probe amend-test <pack> <原版db.pack> <导出.pack>"); return 1; }
        var schema = WarbandStudio.Packfile.Schema.Load(RpfmCli.LocateSchema() ?? throw new FileNotFoundException("找不到 schema。"));
        var packPath = Path.GetFullPath(a[1]);
        var vanillaPath = Path.GetFullPath(a[2]);
        var dest = Path.GetFullPath(a[3]);
        var notes = new List<string>();

        using var pack = PackArchive.Open(packPath);
        var infos = TableFiles.ReadMerged(pack, "unit_upgrade_group_ui_infos_tables", schema, notes)
                    ?? throw new InvalidDataException("包里没有 ui_infos");
        var junc = TableFiles.ReadConcat(pack, "unit_to_unit_group_junctions_tables", schema, notes)
                   ?? throw new InvalidDataException("包里没有 junction");
        var links = TableFiles.ReadConcat(pack, "unit_upgrade_group_ui_links_tables", schema, notes)
                    ?? throw new InvalidDataException("包里没有 ui_links");
        var routes = TableFiles.ReadConcat(pack, "unit_upgrade_to_unit_groups_tables", schema, notes)
                     ?? throw new InvalidDataException("包里没有 routes");
        var costs = TableFiles.ReadMerged(pack, "resource_costs_tables", schema, notes);
        var allGroups = TableFiles.ReadMerged(pack, "unit_upgrade_groups_tables", schema, notes);

        static int Col(DbTable t, params string[] names)
        {
            foreach (var n in names)
            {
                var i = t.Columns.FindIndex(c => c.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
                if (i >= 0) return i;
            }
            return -1;
        }

        string CellOf(DbTable t, int row, params string[] names) => t.Rows[row][Col(t, names)].ToTsv();

        var g1 = CellOf(infos, 0, "unit_upgrade_group");
        var g2 = CellOf(infos, 1, "unit_upgrade_group");
        var juCol = Col(junc, "unit");
        var jgCol = Col(junc, "unit_group");
        string SampleUnit()
        {
            foreach (var r in junc.Rows)
                if (r[jgCol].ToTsv().Equals(g1, StringComparison.OrdinalIgnoreCase)) return r[juCol].ToTsv();
            return junc.Rows[0][juCol].ToTsv();
        }
        var sampleUnit = SampleUnit();
        var unit2 = junc.Rows.Select(r => r[juCol].ToTsv())
                        .FirstOrDefault(x => !x.Equals(sampleUnit, StringComparison.OrdinalIgnoreCase)) ?? sampleUnit;
        string delLinkChild = links.Rows[0][Col(links, "child_key")].ToTsv();
        string delLinkParent = links.Rows[0][Col(links, "parent_key")].ToTsv();
        string delRoute = routes.Rows[0][Col(routes, "upgrade_key")].ToTsv();
        string delJunctionUnit = junc.Rows[0][juCol].ToTsv();
        string delJunctionGroup = junc.Rows[0][jgCol].ToTsv();
        // 已有连线集合（key = child \u0001 parent）：挑 route-only 删除的样本、避开已有线对
        var linkPairSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        {
            var lc = Col(links, "child_key"); var lp = Col(links, "parent_key");
            if (lc >= 0 && lp >= 0)
                foreach (var r in links.Rows) linkPairSet.Add(r[lc].ToTsv() + "\u0001" + r[lp].ToTsv());
        }
        // 和画布 JS 的 positionsFor 同一套规则（单兵组宽 40）：1上 2右 3下 4左 → (childPos, parentPos)
        static (int Child, int Parent) PositionsFor((int X, int Y) parent, (int X, int Y) child)
        {
            double pcx = parent.X + 20.0, pcy = parent.Y + 43.5;
            double ccx = child.X + 20.0, ccy = child.Y + 43.5;
            if (Math.Abs(pcy - ccy) >= Math.Abs(pcx - ccx))
                return pcy <= ccy ? (1, 3) : (3, 1);
            return pcx <= ccx ? (4, 2) : (2, 4);
        }

        const string newGroup = "studio_test_group";
        const string newCost = "studio_cost_999";
        var g1Cat = CellOf(infos, 0, "category");
        var e = new WarbandEdits();
        e.AddGroup.Add(newGroup);
        e.AddJunction.Add((sampleUnit, newGroup));
        e.InfoEdits[newGroup] = (500, 300, g1Cat);                // 与 g1 同页签：同页新建仍要写界面连线
        e.InfoEdits[g1] = (700, 150, null);                       // 只挪坐标，页签不动
        e.InfoEdits[g2] = (12, 34, "STUDIOCAT");                  // 坐标 + 改页签
        e.AddRoute.Add(new WarbandEdits.RouteEdit("studio_test_route", g1, newGroup, newCost, 3, 1));
        e.AddLink.Add(new WarbandEdits.LinkEdit(newGroup, g1, 1, 3, 0, 0, 0));
        e.AddCost[newCost] = -999;      // 成本现在是**带符号**的（负 = 消耗）
        // 加进画布的兵自动解锁战役经验：优先挑一个 RoR（原版里一般是锁着经验的）
        var unlockUnit = junc.Rows.Select(r => r[juCol].ToTsv())
                          .FirstOrDefault(x => x.Contains("_ror", StringComparison.OrdinalIgnoreCase)) ?? sampleUnit;
        e.UnlockXp.Add(unlockUnit);
        e.TabRenames.Add(("BRT", "BRTREN"));                    // 页签重命名
        // 新建页签的 key 挑一个包里**还没有**的（MODX / MODX2 / …）：
        // 固定用 MODX 的话，包里已有 MODX 时这次验收其实什么都没建，检查会对着旧块得出错误结论。
        var newTabKey = "MODX";
        var twuiEntry0 = pack.VisibleEntries.FirstOrDefault(x =>
            x.Path.EndsWith("warband_upgrades.twui.xml", StringComparison.OrdinalIgnoreCase));
        if (twuiEntry0 is not null)
        {
            var twuiText = System.Text.Encoding.UTF8.GetString(pack.ReadDecoded(twuiEntry0));
            for (var i = 2; i <= 9 && twuiText.Contains("holder_tab_" + newTabKey, StringComparison.OrdinalIgnoreCase); i++)
                newTabKey = "MODX" + i;
        }
        // 素材库是**本地文件**（v0.92 起）：写两张"素材 PNG"到临时目录，
        // 用它当新页签的按钮图 + 一次"换图"的来源 —— 验证本地文件也能落进包
        var artDir = Path.Combine(Path.GetTempPath(), "warbandstudio_amend_art");
        Directory.CreateDirectory(artDir);
        var png1x1 = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFAAH/q842iQAAAABJRU5ErkJggg==");
        var localBtn = Path.Combine(artDir, "button_upgrade_modx.png");
        var localBg = Path.Combine(artDir, "background_images_studio_local.png");
        File.WriteAllBytes(localBtn, png1x1);
        File.WriteAllBytes(localBg, png1x1);

        e.NewTabs.Add((newTabKey, "MOD1", "ui/skins/default/warband_upgrades/background_images_emp.png", localBtn));   // 背景从包内选、按钮从**本地素材**选
        // 新建页签时后台会**自动放一个空组**（游戏里"这个分类下有组"页签才显示，P4）——
        // Backend.NewTab 做的就是这两条编辑，这里照抄一份验落表
        var autoGroup = "studio_tab_" + newTabKey.ToLowerInvariant();
        e.AddGroup.Add(autoGroup);
        e.InfoEdits[autoGroup] = (0, 0, newTabKey);
        // 换图：把 BRT 页签的背景图换成 EMP 那张（导出包里目标条目应当与来源字节一致）
        e.FileReplacements.Add(("ui/skins/default/warband_upgrades/background_images_brt.png",
                                "ui/skins/default/warband_upgrades/background_images_emp.png"));
        // 本地素材当来源：目标条目在包里本来是没的 → 导出时新增，字节 = 本地文件
        e.FileReplacements.Add(("ui/skins/default/warband_upgrades/background_images_studio_local.png", localBg));
        e.AddUnitGroup.Add((sampleUnit, "wh_main_group_empire"));  // 军事组授权（种族级：填该种族所有组）
        e.RemoveRoute.Add(delRoute);
        e.RemoveLink.Add((delLinkChild, delLinkParent));
        e.RemoveJunction.Add((delJunctionUnit, delJunctionGroup));

        // ── 一对组只有一条界面连线（v0.124：删了再换方向连不该出两条）──────────────
        // 拿包里**本来就有**的一条连线，用反方向再写一条（模拟"删掉旧的、换个方向重连"的落表结果）。
        // 挑的时候两个组**必须真的在 groups 表里**：作者自己的文件里也有悬空行（会被自愈剔掉），
        // 挑到悬空对就什么都测不到（这里连着踩了两次）。
        var flipLinkChild = "";
        var flipLinkParent = "";
        var liveGroupsAtStart = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (allGroups is not null)
        {
            var gc0 = Col(allGroups, "unit_group");
            if (gc0 < 0) gc0 = TableFiles.FindKeyColumn(allGroups);
            if (gc0 >= 0)
                foreach (var r in allGroups.Rows) { var v = r[gc0].ToTsv(); if (v.Length > 0) liveGroupsAtStart.Add(v); }
        }
        for (var i = 0; i < links.Rows.Count; i++)
        {
            var c0 = CellOf(links, i, "child_key");
            var p0 = CellOf(links, i, "parent_key");
            if (c0.Length == 0 || p0.Length == 0 || c0.Equals(p0, StringComparison.OrdinalIgnoreCase)) continue;
            if (c0.Equals(g1, StringComparison.OrdinalIgnoreCase) || p0.Equals(g1, StringComparison.OrdinalIgnoreCase)) continue;
            if (c0.Equals(newGroup, StringComparison.OrdinalIgnoreCase) || p0.Equals(newGroup, StringComparison.OrdinalIgnoreCase)) continue;
            if (c0.Equals(g2, StringComparison.OrdinalIgnoreCase) || p0.Equals(g2, StringComparison.OrdinalIgnoreCase)) continue;
            if (c0.StartsWith("studio", StringComparison.OrdinalIgnoreCase) || p0.StartsWith("studio", StringComparison.OrdinalIgnoreCase)) continue;
            if (!liveGroupsAtStart.Contains(c0) || !liveGroupsAtStart.Contains(p0)) continue;   // 两个组都要在 groups 里
            flipLinkChild = p0; flipLinkParent = c0;         // 反方向
            break;
        }
        if (flipLinkChild.Length > 0)
        {
            var fpos = PositionsFor((0, 0), (0, 100));
            e.SetLinkPair(new WarbandEdits.LinkEdit(flipLinkChild, flipLinkParent, fpos.Child, fpos.Parent, 0, 0, 0), true);
        }
        // 再挑**另一条**作者的连线，只改出入口（同方向）→ 必须"就地覆盖旧条目"，包里不能出现第二份
        var adjLinkChild = "";
        var adjLinkParent = "";
        var adjLinkMoBefore = "0";
        var adjLinkPoBefore = "0";
        var adjLinkCoBefore = "0";
        for (var i = 0; i < links.Rows.Count; i++)
        {
            var c1 = CellOf(links, i, "child_key");
            var p1 = CellOf(links, i, "parent_key");
            if (c1.Length == 0 || p1.Length == 0 || c1.Equals(p1, StringComparison.OrdinalIgnoreCase)) continue;
            if (c1.Equals(flipLinkParent, StringComparison.OrdinalIgnoreCase) && p1.Equals(flipLinkChild, StringComparison.OrdinalIgnoreCase)) continue;
            if (c1.Equals(flipLinkChild, StringComparison.OrdinalIgnoreCase)) continue;
            if (c1.Equals(g1, StringComparison.OrdinalIgnoreCase) || p1.Equals(g1, StringComparison.OrdinalIgnoreCase)) continue;
            if (c1.Equals(g2, StringComparison.OrdinalIgnoreCase) || p1.Equals(g2, StringComparison.OrdinalIgnoreCase)) continue;
            if (c1.Equals(newGroup, StringComparison.OrdinalIgnoreCase) || p1.Equals(newGroup, StringComparison.OrdinalIgnoreCase)) continue;
            if (c1.StartsWith("studio", StringComparison.OrdinalIgnoreCase) || p1.StartsWith("studio", StringComparison.OrdinalIgnoreCase)) continue;
            if (!liveGroupsAtStart.Contains(c1) || !liveGroupsAtStart.Contains(p1)) continue;
            adjLinkChild = c1; adjLinkParent = p1;
            adjLinkMoBefore = CellOf(links, i, "mid_link_offset");
            adjLinkPoBefore = CellOf(links, i, "parent_link_position_offset");
            adjLinkCoBefore = CellOf(links, i, "child_link_position_offset");
            break;
        }
        if (adjLinkChild.Length > 0)
            // 出入口改成 2/4、mid_link_offset 填 37（模拟拖转点手柄）；po/co 保持 0（不该被动）
            e.SetLinkPair(new WarbandEdits.LinkEdit(adjLinkChild, adjLinkParent, 2, 4, 0, 0, 37), true);
        // 再挑一条**作者自己的升级**，改金额/等级（同 key）→ 必须就地覆盖，不留第二份
        var adjRouteKey = "";
        for (var i = 0; i < routes.Rows.Count; i++)
        {
            var k0 = CellOf(routes, i, "upgrade_key");
            var b0 = CellOf(routes, i, "base_unit_group");
            var t0 = CellOf(routes, i, "target_unit_group");
            if (k0.Length == 0 || b0.Length == 0 || t0.Length == 0) continue;
            if (k0.StartsWith("studio", StringComparison.OrdinalIgnoreCase)) continue;
            if (k0.Equals(delRoute, StringComparison.OrdinalIgnoreCase)) continue;
            if (!liveGroupsAtStart.Contains(b0) || !liveGroupsAtStart.Contains(t0)) continue;
            adjRouteKey = k0;
            break;
        }
        if (adjRouteKey.Length > 0)
            e.SetRoute(new WarbandEdits.RouteEdit(adjRouteKey, "", "", newCost, 5, 2), true);   // 只改成本/等级（base/target 保持）
        

        // ── 成本工坊（v0.127）：成本（带符号）+ 资源池 + 成本↔池，三张表一起写 ──
        const string wbCostId = "studio_test_cost_wb";
        const string wbResource = "brt_chivalry";
        const string wbPoolId = wbResource + "_warband_upgrade";
        e.AddCost[wbCostId] = 250;                     // **正号**：证明符号原样落表（旧代码会写成负）
        e.PoolFactors.Add((wbPoolId, "other", wbResource, -2147483647L, 2147483647L, "", 0));
        e.PoolCosts.Add((wbPoolId, wbCostId, -50, "absolute", "default"));

        // ── 成本工坊 v2（v0.131）：改已有成本（就地覆盖）+ 删成本（连它的资源关联一起）──
        var adjCostId = "";
        var delCostId = "";
        {
            var ck = costs is null ? -1 : Col(costs, "id");
            if (costs is not null && ck < 0) ck = TableFiles.FindKeyColumn(costs);
            var joinCosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                // 有资源关联的成本（删它能顺便验证"关联也一起清掉"）
                var jt = TableFiles.ReadComposite(pack, "resource_cost_pooled_resource_junctions_tables",
                    ["pooled_resource_factor", "resource_cost"], schema, notes);
                if (jt is not null)
                {
                    var jc = Col(jt, "resource_cost");
                    if (jc >= 0) foreach (var r in jt.Rows) { var v = r[jc].ToTsv(); if (v.Length > 0) joinCosts.Add(v); }
                }
            }
            catch { }
            if (costs is not null && ck >= 0)
                foreach (var r in costs.Rows)
                {
                    var id = r[ck].ToTsv();
                    if (id.Length == 0 || id.StartsWith("studio", StringComparison.OrdinalIgnoreCase)) continue;
                    if (joinCosts.Contains(id) && delCostId.Length == 0) { delCostId = id; continue; }
                    if (adjCostId.Length == 0) adjCostId = id;
                }
            if (adjCostId.Length > 0) e.AddCost[adjCostId] = -777;         // 改金额（就地覆盖）
            if (delCostId.Length > 0)
            {
                e.RemoveCost.Add(delCostId);                                // 删成本行
                e.RemovePoolCost.Add(delCostId);                            // 连它的资源关联一起清
            }
        }

        // ── 互斥写入（v0.115：同一对"先加后删 / 先删后加"后一次说了算）────────────────
        // 这三个是用户实测过的症状："先连一条升级线，再点删除删不掉""合并/拆组后那个兵还在原组里"——
        // 根因都是编辑集里同时留着 Add 和 Remove 两条，读侧"先减后加"把删掉的又加回来了。
        // ① 先加后删（路线）：最终不该有这条升级
        const string purgeRouteKey = "studio_test_purge";
        e.SetRoute(new WarbandEdits.RouteEdit(purgeRouteKey, g1, newGroup, newCost, 1, 0), true);
        e.SetRoute(new WarbandEdits.RouteEdit(purgeRouteKey, g1, newGroup, "", 0, 0), false);
        // ② 先加后删（连线）：最终不该有这条线（挑一对别处没用到的组，免得和"新建连线"检查撞）
        e.SetLink(new WarbandEdits.LinkEdit(g2, g1, 1, 3, 0, 0, 0), true);
        e.SetLink(new WarbandEdits.LinkEdit(g2, g1, 1, 3, 0, 0, 0), false);
        // ③ 先删后加（兵↔组）：拿包里本来就有的一对，删掉再加回来 → 最终这一对要**在**（拖出去又拖回来的场景）
        var keepPairUnit = "";
        var keepPairGroup = "";
        for (var i = 0; i < junc.Rows.Count; i++)
        {
            var u = CellOf(junc, i, "unit");
            var g = CellOf(junc, i, "unit_group");
            if (u.Length == 0 || g.Length == 0) continue;
            if (u.Equals(sampleUnit, StringComparison.OrdinalIgnoreCase) || u.Equals(unit2, StringComparison.OrdinalIgnoreCase)) continue;
            if (u.Equals(delJunctionUnit, StringComparison.OrdinalIgnoreCase)) continue;
            if (g.Equals(delJunctionGroup, StringComparison.OrdinalIgnoreCase) || g.Equals(g1, StringComparison.OrdinalIgnoreCase)) continue;
            if (g.Equals(g2, StringComparison.OrdinalIgnoreCase)) continue;
            if (g.StartsWith("studio_", StringComparison.OrdinalIgnoreCase)) continue;
            keepPairUnit = u; keepPairGroup = g; break;
        }
        if (keepPairUnit.Length > 0)
        {
            e.SetJunction(keepPairUnit, keepPairGroup, false);
            e.SetJunction(keepPairUnit, keepPairGroup, true);
        }

        // ── 「右键 → 查看已有升级」面板的落表语义 ────────────────────────────────
        // ① 同一会话里"建了再改"：同键以最后一条为准；交换方向后连线的出入口按新方向重算
        const string swapKey = "studio_test_swap";
        const string swapA = "studio_test_swap_a", swapB = "studio_test_swap_b";
        e.AddGroup.Add(swapA); e.AddGroup.Add(swapB);
        e.AddJunction.Add((sampleUnit, swapA));
        e.AddJunction.Add((unit2, swapB));
        e.InfoEdits[swapA] = (60, 60, g1Cat);                     // A 在 B 上方，同一页签
        e.InfoEdits[swapB] = (60, 220, g1Cat);
        var posAB = PositionsFor((60, 60), (60, 220));            // 建：parent=A（上）/ child=B（下）
        e.AddRoute.Add(new WarbandEdits.RouteEdit(swapKey, swapA, swapB, newCost, 2, 0));
        e.SetLinkPair(new WarbandEdits.LinkEdit(swapB, swapA, posAB.Child, posAB.Parent, 0, 0, 0), true);
        var posBA = PositionsFor((60, 220), (60, 60));            // 交换后：parent=B（下）/ child=A（上）
        e.AddRoute.Add(new WarbandEdits.RouteEdit(swapKey, swapB, swapA, newCost, 2, 0));   // 同键覆盖
        // 交换方向 = 同一对换成新方向：走 SetLinkPair（按"对"归一，旧方向不会剩下来）
        e.SetLinkPair(new WarbandEdits.LinkEdit(swapA, swapB, posBA.Child, posBA.Parent, 0, 0, 0), true);

        // ② 跨页新建升级（两端不在同一页签）：只写路线表，不写 ui_links
        string? xpageTo = null;
        for (var i = 0; i < infos.Rows.Count; i++)
        {
            var g = CellOf(infos, i, "unit_upgrade_group");
            if (g.Equals(g1, StringComparison.OrdinalIgnoreCase) || g.Equals(g2, StringComparison.OrdinalIgnoreCase)) continue;
            if (CellOf(infos, i, "category").Equals(g1Cat, StringComparison.OrdinalIgnoreCase)) continue;
            if (linkPairSet.Contains(g + "\u0001" + g1)) continue;   // 该方向本来就有线：换一个
            xpageTo = g;
            break;
        }
        const string xpageKey = "studio_test_xpage";
        if (xpageTo is not null)
            e.AddRoute.Add(new WarbandEdits.RouteEdit(xpageKey, g1, xpageTo, newCost, 1, 0));

        // ③ route-only 删除：挑一条既有"路线 + 连线"的关系，只删路线行，连线保留（跨页语义）
        // 注意**别挑"翻转/调整画线"那两步动过的对**：翻转那步会把这一对的方向改掉（一对组只有一条线、
        // 方向 = 最后写的），这条检查却按原方向断言"连线还在" → 同一对被两步同时选中就误报 ✗
        // （用户包实测踩到：Brt 的 Pegasus↔Knights_of_the_Realm 同时被 flip 和 keepLink 选中）。
        static bool SamePair(string a, string b, string c, string d) =>
            (a.Equals(c, StringComparison.OrdinalIgnoreCase) && b.Equals(d, StringComparison.OrdinalIgnoreCase)) ||
            (a.Equals(d, StringComparison.OrdinalIgnoreCase) && b.Equals(c, StringComparison.OrdinalIgnoreCase));
        string? keepKey = null, keepChild = null, keepParent = null;
        {
            var rb = Col(routes, "base_unit_group"); var rt = Col(routes, "target_unit_group"); var rk = Col(routes, "upgrade_key");
            if (rb >= 0 && rt >= 0)
                foreach (var r in routes.Rows)
                {
                    var k = rk >= 0 ? r[rk].ToTsv() : "";
                    var b = r[rb].ToTsv(); var t = r[rt].ToTsv();
                    if (k.Length == 0 || k.Equals(delRoute, StringComparison.OrdinalIgnoreCase)) continue;
                    if (b.Equals(g1, StringComparison.OrdinalIgnoreCase) || t.Equals(g1, StringComparison.OrdinalIgnoreCase) ||
                        b.Equals(g2, StringComparison.OrdinalIgnoreCase) || t.Equals(g2, StringComparison.OrdinalIgnoreCase)) continue;
                    if (SamePair(t, b, flipLinkChild, flipLinkParent)) continue;
                    if (SamePair(t, b, adjLinkChild, adjLinkParent)) continue;
                    if (!linkPairSet.Contains(t + "\u0001" + b)) continue;
                    keepKey = k; keepChild = t; keepParent = b;
                    break;
                }
        }
        if (keepKey is not null) e.RemoveRoute.Add(keepKey);      // 注意：**不**往 RemoveLink 里加

        // ── 合并组：把本次会话新建的组并进另一个组（v0.84 的老 bug：被并组的兵会整批消失）──
        // 复现 Backend.MergeGroups 的编辑集：AddJunction(兵,keep) + RemoveJunction(兵,drop) + RemoveGroup(drop)。
        // 老代码里 drop 的 junction 行只存在于本轮的 studio_edits，删行又读不到它 → 最后剩一条指向已删组的悬空行。
        const string mergeKeep = "studio_test_merge_keep";
        const string mergeDrop = "studio_test_merge_drop";
        e.AddGroup.Add(mergeKeep);
        e.AddGroup.Add(mergeDrop);
        e.AddJunction.Add((unit2, mergeKeep));                    // keep 原本有一个兵
        e.AddJunction.Add((sampleUnit, mergeDrop));               // drop 的兵是本次会话加进去的
        e.InfoEdits[mergeKeep] = (900, 400, g1Cat);
        e.InfoEdits[mergeDrop] = (966, 400, g1Cat);
        e.AddJunction.Add((sampleUnit, mergeKeep));               // ← 合并：兵挪进 keep
        e.RemoveJunction.Add((sampleUnit, mergeDrop));
        e.RemoveGroup.Add(mergeDrop);
        e.InfoEdits.Remove(mergeDrop);

        var rep = WarbandExporter.Export(packPath, vanillaPath, dest, schema, e);
        var plan = new Dictionary<string, object>
        {
            ["dest"] = dest,
            ["newGroup"] = newGroup,
            ["newUnit"] = sampleUnit,
            ["newCost"] = newCost,
            ["newCostAmount"] = -999,
            ["newRouteKey"] = "studio_test_route",
            ["newRouteBase"] = g1,
            ["newRouteTarget"] = newGroup,
            ["newRouteRank"] = 3,
            ["movedGroup"] = g1,
            ["movedTo"] = new[] { 700, 150 },
            ["categoryGroup"] = g2,
            ["category"] = "STUDIOCAT",
            ["deletedRoute"] = delRoute,
            ["deletedLinkChild"] = delLinkChild,
            ["deletedLinkParent"] = delLinkParent,
            ["deletedJunctionUnit"] = delJunctionUnit,
            ["deletedJunctionGroup"] = delJunctionGroup,
            ["grantUnit"] = sampleUnit,
            ["unlockUnit"] = unlockUnit,
            ["grantGroup"] = "wh_main_group_empire",
            ["renameOld"] = "BRT",
            ["renameNew"] = "BRTREN",
            ["artTarget"] = "ui/skins/default/warband_upgrades/background_images_brt.png",
            ["artSource"] = "ui/skins/default/warband_upgrades/background_images_emp.png",
            ["newTabKey"] = newTabKey,
            ["newTabDonor"] = "MOD1",
            ["newTabLocalBtn"] = "ui/skins/default/warband_upgrades/button_upgrade_" + newTabKey.ToLowerInvariant() + ".png",
            ["localArtTarget"] = "ui/skins/default/warband_upgrades/background_images_studio_local.png",
            ["localArtBytes"] = Convert.ToBase64String(png1x1),
            ["newTabAutoGroup"] = autoGroup,
            ["newTabAutoCat"] = newTabKey,
            ["newTabTwui"] = "ui/campaign ui/warband_upgrades.twui.xml",
            ["newTabImages"] = new[]
            {
                $"ui/skins/default/warband_upgrades/background_images_{newTabKey.ToLowerInvariant()}.png",
                $"ui/skins/default/warband_upgrades/button_upgrade_{newTabKey.ToLowerInvariant()}.png",
            },
            // 跨页只改路线表 / 同会话同键后写为准 / route-only 删除
            ["xpageRouteKey"] = xpageTo is null ? "" : xpageKey,
            ["xpageFrom"] = g1,
            ["xpageTo"] = xpageTo ?? "",
            ["swapRouteKey"] = swapKey,
            ["swapBase"] = swapB,
            ["swapTarget"] = swapA,
            ["swapChildPos"] = posBA.Child,
            ["swapParentPos"] = posBA.Parent,
            ["keepLinkRouteKey"] = keepKey ?? "",
            ["keepLinkChild"] = keepChild ?? "",
            ["keepLinkParent"] = keepParent ?? "",
            // 合并组（本次新建的组并进另一个组，兵不能丢）
            ["mergeKeep"] = mergeKeep,
            ["mergeDrop"] = mergeDrop,
            ["mergeUnit"] = sampleUnit,
            ["mergePos"] = new[] { 900, 400 },
            // 互斥写入（v0.115）：同一对"先加后删"不该留、"先删后加"该在
            ["purgeRouteKey"] = purgeRouteKey,
            ["purgeLinkChild"] = g2,
            ["purgeLinkParent"] = g1,
            ["keepPairUnit"] = keepPairUnit,
            ["keepPairGroup"] = keepPairGroup,
            // 一对组只有一条线：反方向再写一条后，导出包里这一对应当只剩 1 行
            ["flipLinkChild"] = flipLinkChild,
            ["flipLinkParent"] = flipLinkParent,
            ["adjLinkChild"] = adjLinkChild,
            ["adjLinkParent"] = adjLinkParent,
            ["adjLinkMoBefore"] = adjLinkMoBefore,
            ["adjLinkPoBefore"] = adjLinkPoBefore,
            ["adjLinkCoBefore"] = adjLinkCoBefore,
            ["wbCostId"] = wbCostId,
            ["adjCostId"] = adjCostId,
            ["delCostId"] = delCostId,
            ["wbGold"] = 250,
            ["wbPoolId"] = wbPoolId,
            ["wbResource"] = wbResource,
            ["adjRouteKey"] = adjRouteKey,
            ["adjRouteCost"] = newCost,
        };
        File.WriteAllText(dest + ".plan.json", JsonSerializer.Serialize(plan, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"[✓] 全量编辑导出：{dest}");
        Console.WriteLine($"[i] 编辑摘要：{e.Summary()}");
        foreach (var x in rep.AddedEntries) Console.WriteLine("      新增 " + x);
        // ⚠ 开头的（自愈剔除、页签体检这种"必须看一眼"的）全打出来，其余最多 20 条 ——
        // 不然真正该看到的警告会被 20 条上限截掉（实测：页签体检的提醒排在最后，压根没打出来）
        foreach (var n in rep.Notes.Where(n => n.StartsWith("⚠"))
                     .Concat(rep.Notes.Where(n => !n.StartsWith("⚠")).Take(20)))
            Console.WriteLine("      · " + n);
        return 0;
    }
}
