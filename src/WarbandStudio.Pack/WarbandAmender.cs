using System.Text.RegularExpressions;
using WarbandStudio.Packfile;

namespace WarbandStudio.Pack;

/// <summary>
/// 把画布上的编辑**落进包**（"直接改 pack"：不引入工具自己的项目层）。
///
/// 落表策略（TW 的表是"多文件 + 按 key 覆盖"，两条路子各有适用面）：
///   · **新增 / 改值** → 写一张覆盖表文件 `db/&lt;表&gt;/studio_edits`（键相同的行覆盖原行，键不同就是新行）；
///     组坐标/页签沿用原来的 `studio_layout`。
///   · **删行** → 只能改**原来那个文件**（覆盖表删不掉别人的行）：把它解出来、滤掉目标行、原样重编回去。
/// 编码器与解码器是逐字节往返验证过的（verify_native.py ①），所以重编不会破坏其余内容。
/// </summary>
public static class WarbandAmender
{
    public const string EditFileName = "zzzz_studio_edits";   // 名字排最后：游戏按文件名加载，后加载的覆盖前面的（以前叫 studio_edits，会被 Yukino_ 这类文件顶掉）
    public const string LayoutFileName = "zzzz_studio_layout"; // 同上：坐标/页签的覆盖表必须排最后

    private sealed record Meta(SchemaDefinition Def, string Guid, bool Mysterious);

    /// <summary>把编辑写进 <paramref name="repl"/>（"包内路径 → 新字节"），返回改动的文件数。</summary>
    public static int Apply(PackArchive pack, PackArchive? vanilla, WarbandEdits e, Schema schema,
                            Dictionary<string, byte[]> repl, List<string> notes)
    {
        var changed = 0;
        const string Junc = "unit_to_unit_group_junctions_tables";

        // ── -1) 旧名覆盖表搬家（见 MigrateLegacy 的说明）──
        MigrateLegacy(pack, repl, e, notes);

        // ── -1b) 页签重命名的连带：本会话新排进"旧页签名"的组要跟着换 key ──
        // 不然包里的已有行会在 6d 被改成新 key，本轮新加的行却还写着旧 key →
        // 那个组落在"不存在的页签"上（页签体检会报 ⚠；实测就是重命名 + 新建组一起用会踩到）。
        if (e.TabRenames.Count > 0)
            foreach (var k in e.InfoEdits.Keys.ToList())
            {
                var cur = e.InfoEdits[k].Category;
                if (cur is null) continue;
                foreach (var (o, n2) in e.TabRenames)
                    if (cur.Equals(o, StringComparison.OrdinalIgnoreCase))
                    {
                        e.InfoEdits[k] = (e.InfoEdits[k].X, e.InfoEdits[k].Y, n2);
                        break;
                    }
            }

        // ── 0) **自愈：剔除"引用了不存在的组"的行** ──
        // 游戏/RPFM 会把悬空引用判成 invalid database record 直接崩（实测：合并后消失的那几个组，
        // 路线表里还留着引用它们的行）。这里先算出"这次导出后真正存在的组"（包里已有的 ∪ 新增 − 删除），
        // 再把引用了名单外组的**新增**路线 / 连线 / junction 丢掉（并写日志说明丢了几条）。
        var liveGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var src in new[] { pack, vanilla })
            {
                if (src is null) continue;
                var groupsT = TableFiles.ReadConcat(src, "unit_upgrade_groups_tables", schema, notes);
                if (groupsT is null) continue;
                var gc = groupsT.Columns.FindIndex(c => c.Name.Equals("unit_group", StringComparison.OrdinalIgnoreCase));
                if (gc < 0) gc = TableFiles.FindKeyColumn(groupsT);
                foreach (var r in groupsT.Rows) { var v = r[gc].ToTsv(); if (v.Length > 0) liveGroups.Add(v); }
            }
        }
        catch { }
        foreach (var g in e.AddGroup) liveGroups.Add(g);
        foreach (var g in e.RemoveGroup) liveGroups.Remove(g);
        // 空 base/target = "只改金额/等级，两个端别动"（就地改时保留作者原值）→ 不能因为它是空就当成悬空丢掉
        var addRoutes = e.AddRoute.Where(r =>
            (r.Base.Length == 0 || liveGroups.Contains(r.Base)) &&
            (r.Target.Length == 0 || liveGroups.Contains(r.Target))).ToList();
        var addLinks = e.AddLink.Where(l => liveGroups.Contains(l.Child) && liveGroups.Contains(l.Parent)).ToList();
        var addJunctions = e.AddJunction.Where(j => liveGroups.Contains(j.Group)).ToList();
        // 包内**已有**的行也扫一遍：引用了不存在组的路线/连线，导出时直接剔掉（救历史遗留的坏包）
        var droppedExisting = 0;
        droppedExisting += RewriteDropping(pack, "unit_upgrade_to_unit_groups_tables", schema, repl, notes,
            (t, i) => !liveGroups.Contains(Cell(t, i, "base_unit_group")) || !liveGroups.Contains(Cell(t, i, "target_unit_group")));
        droppedExisting += RewriteDropping(pack, "unit_upgrade_group_ui_links_tables", schema, repl, notes,
            (t, i) => !liveGroups.Contains(Cell(t, i, "child_key")) || !liveGroups.Contains(Cell(t, i, "parent_key")));
        if (droppedExisting > 0)
            notes.Add($"⚠ 自愈：包里原有 {droppedExisting} 个文件里的悬空路线/连线行已剔除（引用的组不存在）。");

        var dropped = (e.AddRoute.Count - addRoutes.Count) + (e.AddLink.Count - addLinks.Count) + (e.AddJunction.Count - addJunctions.Count);
        if (dropped > 0)
            notes.Add($"⚠ 自愈：剔除 {dropped} 条「引用了不存在的组」的行（路线 {e.AddRoute.Count - addRoutes.Count} / " +
                      $"连线 {e.AddLink.Count - addLinks.Count} / 兵↔组 {e.AddJunction.Count - addJunctions.Count}）——组可能被合并/删除过。");

        const string Groups = "unit_upgrade_groups_tables";
        const string Infos = "unit_upgrade_group_ui_infos_tables";
        const string Routes = "unit_upgrade_to_unit_groups_tables";
        const string Links = "unit_upgrade_group_ui_links_tables";
        const string Costs = "resource_costs_tables";

        // ── 1) 兵 ↔ 组 ──
        if (e.AddJunction.Count > 0)
            changed += AddRows(pack, vanilla, Junc, schema, repl, notes,
                addJunctions.Select(j => new Dictionary<string, object>
                {
                    ["unit"] = j.Unit,
                    ["unit_group"] = j.Group,
                }));

        if (e.RemoveJunction.Count > 0)
        {
            var drop = KeySet(e.RemoveJunction.Select(j => j.Unit + "\u0001" + j.Group));
            changed += RewriteDropping(pack, Junc, schema, repl, notes,
                (t, i) => drop.Contains(Cell(t, i, "unit") + "\u0001" + Cell(t, i, "unit_group")));
        }

        // ── 2) 组本身（连带的 junction / infos / links / routes 一起清）──
        if (e.AddGroup.Count > 0)
            changed += AddRows(pack, vanilla, Groups, schema, repl, notes,
                e.AddGroup.Select(g => new Dictionary<string, object> { ["unit_group"] = g }),
                keyCols: ["unit_group"]);

        if (e.RemoveGroup.Count > 0)
        {
            var drop = KeySet(e.RemoveGroup);
            changed += RewriteDropping(pack, Groups, schema, repl, notes,
                (t, i) => drop.Contains(Cell(t, i, "unit_group")));
            changed += RewriteDropping(pack, Junc, schema, repl, notes,
                (t, i) => drop.Contains(Cell(t, i, "unit_group")));
            changed += RewriteDropping(pack, Links, schema, repl, notes,
                (t, i) => drop.Contains(Cell(t, i, "child_key")) || drop.Contains(Cell(t, i, "parent_key")));
            changed += RewriteDropping(pack, Routes, schema, repl, notes,
                (t, i) => drop.Contains(Cell(t, i, "base_unit_group")) || drop.Contains(Cell(t, i, "target_unit_group")));
            // 坐标行：键唯一，覆盖表里给个"空页签"没用 —— 只能删原行
            changed += RewriteDropping(pack, Infos, schema, repl, notes,
                (t, i) => drop.Contains(Cell(t, i, "unit_upgrade_group")));
        }

        // ── 3) 组坐标 / 页签（infos 键唯一，"改值"两种落法一起用）──
        //   ① **就地改**：包里已经有这一行的文件，直接改那一行的 x/y/category。
        //      为什么必须就地改：同一张表多文件是"按文件名加载顺序定胜负"，后加载的赢。
        //      MOD 自己的文件（Yukino_Upgrade_* 这类）跟我们的覆盖表谁后加载不由我们决定，
        //      实测就是"拖了坐标、移到别的页签，重新加载后全回来了"（用户报的"新建 studio_layout 被原文件顶掉"）。
        //      改了原文件本身，就没有第二份数据来抢。
        //   ② 包里哪都没有的组（本会话新建的）→ 补进 zzzz_studio_layout 覆盖表（名字排最后）。
        if (e.InfoEdits.Count > 0)
        {
            if (TryMeta(pack, vanilla, Infos, schema, out var meta, out _))
            {
                var want = new Dictionary<string, (int X, int Y, string? Cat)>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in e.InfoEdits) want[kv.Key] = kv.Value;
                var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // ① 就地改。TablePaths 里头也包含覆盖表自己 —— 上一轮写进覆盖表的行同样就地更新，
                //    不会被这一轮的写法整块盖掉（老代码每次都拿"包里的合并结果"重建覆盖表，会丢行）。
                foreach (var f in TablePaths(pack, Infos, repl))
                {
                    var bytes0 = CurrentBytes(pack, repl, f);
                    if (bytes0 is null) continue;
                    DbTable t;
                    try { t = DbTable.Decode(bytes0, Infos, schema); } catch { continue; }
                    var kc = t.Columns.FindIndex(c => c.Name.Equals("unit_upgrade_group", StringComparison.OrdinalIgnoreCase));
                    if (kc < 0) continue;
                    var fields = t.Definition.Fields;
                    var fx = fields.FindIndex(x => x.Name.Equals("x", StringComparison.OrdinalIgnoreCase));
                    var fy = fields.FindIndex(x => x.Name.Equals("y", StringComparison.OrdinalIgnoreCase));
                    var fc = fields.FindIndex(x => x.Name.Equals("category", StringComparison.OrdinalIgnoreCase));
                    if (fx < 0 || fy < 0) continue;
                    var raws = new List<DbValue[]>();
                    var hit = 0;
                    for (var i = 0; i < t.Rows.Count; i++)
                    {
                        var raw = t.RawRows[i];
                        var g = t.Rows[i][kc].ToTsv();
                        if (want.TryGetValue(g, out var w))
                        {
                            var curX = fx < raw.Length ? (int)raw[fx].Int : 0;
                            var curY = fy < raw.Length ? (int)raw[fy].Int : 0;
                            var cat = w.Cat;
                            if (cat is null)
                            {
                                // 只改坐标：页签保持原值；页签同时被重命名的话跟着换 key
                                var cur = Cell(t, i, "category");
                                foreach (var (o, n2) in e.TabRenames)
                                    if (cur.Equals(o, StringComparison.OrdinalIgnoreCase)) cat = n2;
                            }
                            done.Add(g);            // 找到了行 → 不用再补覆盖表
                            var same = curX == w.X && curY == w.Y
                                       && (cat is null || Cell(t, i, "category").Equals(cat, StringComparison.OrdinalIgnoreCase));
                            if (!same)
                            {
                                var copy = (DbValue[])raw.Clone();
                                if (fx < copy.Length) copy[fx] = DbValue.Of((long)w.X);
                                if (fy < copy.Length) copy[fy] = DbValue.Of((long)w.Y);
                                if (cat is not null && fc >= 0 && fc < copy.Length) copy[fc] = DbValue.OfStr(cat);
                                raw = copy;
                                hit++;
                            }
                        }
                        raws.Add(raw);
                    }
                    if (hit == 0) continue;
                    repl[f] = EncodeLike(t, raws);
                    changed++;
                    notes.Add($"{f}：就地改 {hit} 个组的坐标/页签");
                }

                // ② 包里没有行的组 → 覆盖表
                var need = want.Keys.Where(k => !done.Contains(k)).ToList();
                if (need.Count > 0)
                {
                    var layoutPath = $"db/{Infos}/{LayoutFileName}";
                    var baseBytes = CurrentBytes(pack, repl, layoutPath);
                    DbTable? baseT = null;
                    if (baseBytes is not null) { try { baseT = DbTable.Decode(baseBytes, Infos, schema); } catch { baseT = null; } }
                    var raws = baseT is null ? new List<DbValue[]>() : baseT.RawRows.Select(r => (DbValue[])r.Clone()).ToList();
                    var kc2 = baseT is null ? -1 : baseT.Columns.FindIndex(c => c.Name.Equals("unit_upgrade_group", StringComparison.OrdinalIgnoreCase));
                    var added2 = 0;
                    foreach (var k in need)
                    {
                        var w = want[k];
                        var raw = Build(meta, new Dictionary<string, object>
                        {
                            ["unit_upgrade_group"] = k,
                            ["x"] = (long)w.X,
                            ["y"] = (long)w.Y,
                            ["category"] = w.Cat ?? "",
                        });
                        var at = -1;
                        if (baseT is not null && kc2 >= 0)
                            for (var i = 0; i < baseT.Rows.Count; i++)
                                if (baseT.Rows[i][kc2].ToTsv().Equals(k, StringComparison.OrdinalIgnoreCase)) { at = i; break; }
                        if (at >= 0) raws[at] = raw; else { raws.Add(raw); added2++; }
                    }
                    repl[layoutPath] = baseT is null ? Encode(meta, raws) : EncodeLike(baseT, raws);
                    changed++;
                    notes.Add($"坐标/页签：{need.Count} 个组写进 {LayoutFileName} 覆盖表（包里原本没有它们的行，新增 {added2}）");
                }
            }
            else notes.Add("坐标：包里没有 unit_upgrade_group_ui_infos_tables，这台表的编辑没落。");
        }

        // ── 4) 升级关系（决定能不能升）──
        if (e.AddRoute.Count > 0)
        {
            // **已有行就地改**（口径同坐标/页签，见 §7.31）：改金额/等级、换方向都是在**作者那一行**上改，
            // 不留第二份（否则包里两份条目：作者文件一份 + 覆盖表一份，用户看到就是"旧的还在/冲突"）。
            // 只有包里哪都没有的 key（工具新建的升级）才走覆盖表。
            var want = new Dictionary<string, WarbandEdits.RouteEdit>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in addRoutes) want[r.UpgradeKey] = r;
            static bool IsOursFile2(string path)
            {
                var n = System.IO.Path.GetFileName(path);
                return n.StartsWith("studio", StringComparison.OrdinalIgnoreCase)
                    || n.StartsWith("zzzz_studio", StringComparison.OrdinalIgnoreCase);
            }
            var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var settledInAuthor = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in TablePaths(pack, Routes, repl).OrderBy(x => IsOursFile2(x) ? 1 : 0))
            {
                var bytes0 = CurrentBytes(pack, repl, f);
                if (bytes0 is null) continue;
                DbTable t;
                try { t = DbTable.Decode(bytes0, Routes, schema); } catch { continue; }
                var kc = t.Columns.FindIndex(x => x.Name.Equals("upgrade_key", StringComparison.OrdinalIgnoreCase));
                if (kc < 0) continue;
                var def = t.Definition.Fields;
                var cols = new (string Name, int Raw, bool Str)[]
                {
                    ("base_unit_group", def.FindIndex(x => x.Name.Equals("base_unit_group", StringComparison.OrdinalIgnoreCase)), true),
                    ("target_unit_group", def.FindIndex(x => x.Name.Equals("target_unit_group", StringComparison.OrdinalIgnoreCase)), true),
                    ("resource_cost", def.FindIndex(x => x.Name.Equals("resource_cost", StringComparison.OrdinalIgnoreCase)), true),
                    ("required_rank", def.FindIndex(x => x.Name.Equals("required_rank", StringComparison.OrdinalIgnoreCase)), false),
                    ("subtracted_rank", def.FindIndex(x => x.Name.Equals("subtracted_rank", StringComparison.OrdinalIgnoreCase)), false),
                };
                var raws = new List<DbValue[]>();
                var hit = 0;
                var dupDropped = 0;
                for (var i = 0; i < t.Rows.Count; i++)
                {
                    var k = t.Rows[i][kc].ToTsv();
                    if (k.Length > 0 && want.TryGetValue(k, out var w))
                    {
                        if (IsOursFile2(f) && settledInAuthor.Contains(k)) { dupDropped++; continue; }
                        var raw = (DbValue[])t.RawRows[i].Clone();
                        void Set(string v, int idx, bool str)
                        {
                            if (idx < 0 || idx >= raw.Length) return;
                            // 空字符串 = 这次不改这一列（保留作者原值）：调用方只想改金额/等级时不用把 base/target 也抄一遍
                            if (str && v.Length == 0) return;
                            raw[idx] = str ? DbValue.OfStr(v) : DbValue.Of((long)(long.TryParse(v, out var n) ? n : 0));
                        }
                        Set(w.Base, cols[0].Raw, true);
                        Set(w.Target, cols[1].Raw, true);
                        Set(w.Cost, cols[2].Raw, true);
                        Set(w.RequiredRank.ToString(), cols[3].Raw, false);
                        Set(w.SubtractedRank.ToString(), cols[4].Raw, false);
                        raws.Add(raw);
                        placed.Add(k);
                        if (!IsOursFile2(f)) settledInAuthor.Add(k);
                        hit++;
                        continue;
                    }
                    raws.Add(t.RawRows[i]);
                }
                if (hit == 0 && dupDropped == 0) continue;
                repl[f] = EncodeLike(t, raws);
                changed++;
                notes.Add($"{f}：升级就地改 {hit} 行" + (dupDropped > 0 ? $"，剔掉多余的同 key 行 {dupDropped} 条" : ""));
            }
            var restRoutes = addRoutes.Where(r => !placed.Contains(r.UpgradeKey)).ToList();
            if (restRoutes.Count > 0)
                changed += AddRows(pack, vanilla, Routes, schema, repl, notes,
                    restRoutes.Select(r => new Dictionary<string, object>
                    {
                        ["upgrade_key"] = r.UpgradeKey,
                        ["base_unit_group"] = r.Base,
                        ["target_unit_group"] = r.Target,
                        ["resource_cost"] = r.Cost,
                        ["required_rank"] = (long)r.RequiredRank,
                        ["subtracted_rank"] = (long)r.SubtractedRank,
                    }),
                    keyCols: ["upgrade_key"]);    // 同一条升级改金额/换方向 = 同 key 覆盖（后写的为准）
        }

        if (e.RemoveRoute.Count > 0)
        {
            var drop = KeySet(e.RemoveRoute);
            changed += RewriteDropping(pack, Routes, schema, repl, notes,
                (t, i) => drop.Contains(Cell(t, i, "upgrade_key")));
        }

        // ── 5) 界面连线（C1：游戏里真实显示的箭头）──
        if (e.AddLink.Count > 0)
        {
            // **一对组只留一行**（谁当 child 都算同一对），而且**已有行就地改**：
            //   · 作者文件里已经有这一对 → 就地改那一行（方向归一 + 出入口重写），**不留第二份**；
            //   · 我们自己的覆盖表里有 → 同样就地改；作者文件也有的那份在覆盖表里就是多余的 → 剔掉；
            //   · 哪都没有（工具本轮新建的 pair）→ 下面交给覆盖表。
            // 实测（用户报）：只调出入口时旧行没被覆盖 → 包里两份条目、游戏里两条线/冲突。
            var want = new Dictionary<string, (string Child, string Parent, int Cp, int Pp, double Mid)>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in e.AddLink)
                want[LinkPairKey(l.Child, l.Parent)] = (l.Child, l.Parent, l.ChildPos, l.ParentPos, l.MidOffset);
            static bool IsOursFile(string path)
            {
                var n = System.IO.Path.GetFileName(path);
                return n.StartsWith("studio", StringComparison.OrdinalIgnoreCase)
                    || n.StartsWith("zzzz_studio", StringComparison.OrdinalIgnoreCase);
            }
            var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);        // 已经落好的 pair（哪个文件里都算）
            var settledInAuthor = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // 作者文件里改过的 pair
            // 作者文件先处理（就地改的优先级最高），覆盖表后处理（多余的同对行剔掉）
            foreach (var f in TablePaths(pack, Links, repl).OrderBy(x => IsOursFile(x) ? 1 : 0))
            {
                var bytes0 = CurrentBytes(pack, repl, f);
                if (bytes0 is null) continue;
                DbTable t;
                try { t = DbTable.Decode(bytes0, Links, schema); } catch { continue; }
                var cc = t.Columns.FindIndex(x => x.Name.Equals("child_key", StringComparison.OrdinalIgnoreCase));
                var pc = t.Columns.FindIndex(x => x.Name.Equals("parent_key", StringComparison.OrdinalIgnoreCase));
                if (cc < 0 || pc < 0) continue;
                var def = t.Definition.Fields;
                var fcc = def.FindIndex(x => x.Name.Equals("child_key", StringComparison.OrdinalIgnoreCase));
                var fpc = def.FindIndex(x => x.Name.Equals("parent_key", StringComparison.OrdinalIgnoreCase));
                var fcp = def.FindIndex(x => x.Name.Equals("child_link_position", StringComparison.OrdinalIgnoreCase));
                var fpp = def.FindIndex(x => x.Name.Equals("parent_link_position", StringComparison.OrdinalIgnoreCase));
                // 三个 offset（两端沿边的平移 + 中间段的弯度）也要一起写：
                // 调整画线 = "这条线按我选的 position 走"。留着作者原来的 mid/端点 offset，
                // 方向一换就会把中间那段折到卡片另一侧 —— 画布上看起来就是"对向多出一小段线"（用户实测）。
                var fpo = def.FindIndex(x => x.Name.Equals("parent_link_position_offset", StringComparison.OrdinalIgnoreCase));
                var fco = def.FindIndex(x => x.Name.Equals("child_link_position_offset", StringComparison.OrdinalIgnoreCase));
                var fmo = def.FindIndex(x => x.Name.Equals("mid_link_offset", StringComparison.OrdinalIgnoreCase));
                var raws = new List<DbValue[]>();
                var hit = 0;
                var dupDropped = 0;
                for (var i = 0; i < t.Rows.Count; i++)
                {
                    var c = t.Rows[i][cc].ToTsv();
                    var p = t.Rows[i][pc].ToTsv();
                    var pk = LinkPairKey(c, p);
                    if (want.TryGetValue(pk, out var w))
                    {
                        // 作者文件里已经改过这一对 → 覆盖表里那份是多余的，剔掉（防止两份数据冲突）
                        if (IsOursFile(f) && settledInAuthor.Contains(pk)) { dupDropped++; continue; }
                        var raw = (DbValue[])t.RawRows[i].Clone();
                        if (fcc >= 0 && fcc < raw.Length) raw[fcc] = DbValue.OfStr(w.Child);
                        if (fpc >= 0 && fpc < raw.Length) raw[fpc] = DbValue.OfStr(w.Parent);
                        if (fcp >= 0 && fcp < raw.Length) raw[fcp] = DbValue.Of((long)w.Cp);
                        if (fpp >= 0 && fpp < raw.Length) raw[fpp] = DbValue.Of((long)w.Pp);
                        // mid_link_offset = 中间那段沿起始方向走多少像素（游戏里同一个走线；0 = 起点处就转）。
                        // **po/co 不动** —— 它们的数值不是像素（用户查证），不归这里管。
                        if (fmo >= 0 && fmo < raw.Length) raw[fmo] = DbValue.Of(w.Mid);
                        raws.Add(raw);
                        placed.Add(pk);
                        if (!IsOursFile(f)) settledInAuthor.Add(pk);
                        hit++;
                        continue;
                    }
                    raws.Add(t.RawRows[i]);
                }
                if (hit == 0 && dupDropped == 0) continue;
                repl[f] = EncodeLike(t, raws);
                changed++;
                notes.Add($"{f}：连线就地改 {hit} 行" + (dupDropped > 0 ? $"，剔掉多余的同对行 {dupDropped} 条" : ""));
            }
            // 包里哪都没有的 pair（工具新建）→ 覆盖表
            var rest = addLinks.Where(l => !placed.Contains(LinkPairKey(l.Child, l.Parent))).ToList();
            if (rest.Count > 0)
                changed += AddRows(pack, vanilla, Links, schema, repl, notes,
                    rest.Select(l => new Dictionary<string, object>
                    {
                        ["child_key"] = l.Child,
                        ["parent_key"] = l.Parent,
                        ["child_link_position"] = (long)l.ChildPos,
                        ["parent_link_position"] = (long)l.ParentPos,
                        ["parent_link_position_offset"] = l.ParentOffset,
                        ["child_link_position_offset"] = l.ChildOffset,
                        ["mid_link_offset"] = l.MidOffset,
                    }),
                    keyCols: ["child_key", "parent_key"]);   // 键表第一列 child_key 不唯一，不能只看它
        }

        if (e.RemoveLink.Count > 0)
        {
            // 按"对"删（两个方向都算）：一对组只允许有一条界面连线 ——
            // 精确按方向删会漏掉反方向那条，用户"删了再换个方向连"就会看到两条线。
            // 但**本轮要写的那条方向不能删**（交换方向 = 删旧方向 + 写新方向，两条编辑同时在，
            // 一刀切按对删会把刚写进去的新方向也删掉 —— 实测验收里"交换方向后连线没了"）。
            var drop = KeySet(e.RemoveLink.Select(l => LinkPairKey(l.Child, l.Parent)));
            var keepDirs = KeySet(e.AddLink.Select(l => l.Child + "" + l.Parent));
            changed += RewriteDropping(pack, Links, schema, repl, notes, (t, i) =>
            {
                var c = Cell(t, i, "child_key");
                var p = Cell(t, i, "parent_key");
                return drop.Contains(LinkPairKey(c, p)) && !keepDirs.Contains(c + "" + p);
            });
        }

        // ── 6) 成本（成本工坊：新建/改金额/删）──
        // **已有行就地改**（口径同坐标/页签/路线）：包里已经有这个 id 就改那一行的 treasury_cost，
        // 不留第二份；只有新 id 才写覆盖表。label 列（expenditure/income）保持作者原样。
        if (e.AddCost.Count > 0)
        {
            var wantCost = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in e.AddCost) wantCost[kv.Key] = kv.Value;
            static bool IsOursCost(string path)
            {
                var n = System.IO.Path.GetFileName(path);
                return n.StartsWith("studio", StringComparison.OrdinalIgnoreCase)
                    || n.StartsWith("zzzz_studio", StringComparison.OrdinalIgnoreCase);
            }
            var placedCost = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var settledCost = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in TablePaths(pack, Costs, repl).OrderBy(x => IsOursCost(x) ? 1 : 0))
            {
                var bytes0 = CurrentBytes(pack, repl, f);
                if (bytes0 is null) continue;
                DbTable t;
                try { t = DbTable.Decode(bytes0, Costs, schema); } catch { continue; }
                var kc = t.Columns.FindIndex(x => x.Name.Equals("id", StringComparison.OrdinalIgnoreCase));
                if (kc < 0) kc = TableFiles.FindKeyColumn(t);
                if (kc < 0) continue;
                var ti = t.Definition.Fields.FindIndex(x => x.Name.Equals("treasury_cost", StringComparison.OrdinalIgnoreCase));
                var raws = new List<DbValue[]>();
                var hit = 0;
                var dup = 0;
                for (var i = 0; i < t.Rows.Count; i++)
                {
                    var k = t.Rows[i][kc].ToTsv();
                    if (k.Length > 0 && wantCost.TryGetValue(k, out var gold))
                    {
                        if (IsOursCost(f) && settledCost.Contains(k)) { dup++; continue; }
                        var raw = (DbValue[])t.RawRows[i].Clone();
                        if (ti >= 0 && ti < raw.Length) raw[ti] = DbValue.Of((long)Math.Round(gold));
                        raws.Add(raw);
                        placedCost.Add(k);
                        if (!IsOursCost(f)) settledCost.Add(k);
                        hit++;
                        continue;
                    }
                    raws.Add(t.RawRows[i]);
                }
                if (hit == 0 && dup == 0) continue;
                repl[f] = EncodeLike(t, raws);
                changed++;
                notes.Add($"{f}：成本就地改 {hit} 行" + (dup > 0 ? $"，剔掉多余的同 id 行 {dup} 条" : ""));
            }
            var restCost = e.AddCost.Where(kv => !placedCost.Contains(kv.Key)).ToList();
            if (restCost.Count > 0)
            {
                var costTable = TableFiles.ReadMerged(pack, Costs, schema, null);
            var proto = costTable?.Rows.FirstOrDefault(r =>
            {
                var v = r.Length > 0 ? r[0].ToTsv() : "";
                return v.Contains("Cost", StringComparison.OrdinalIgnoreCase);
            }) ?? costTable?.Rows.FirstOrDefault();
            string Proto(string col, string fallback)
            {
                if (costTable is null || proto is null) return fallback;
                var c = costTable.Columns.FindIndex(x => x.Name.Equals(col, StringComparison.OrdinalIgnoreCase));
                return c < 0 ? fallback : proto[c].ToTsv();
            }

            changed += AddRows(pack, vanilla, Costs, schema, repl, notes,
                restCost.Select(kv => new Dictionary<string, object>
                {
                    ["id"] = kv.Key,
                    ["treasury_cost"] = (long)Math.Round(kv.Value),   // 带符号：负 = 消耗、正 = 获得（成本工坊要区分）
                    ["expenditure_type"] = Proto("expenditure_type", "RECRUITMENT"),
                    ["income_type"] = Proto("income_type", "CANCELLED_RECRUITMENT"),
                    ["expenditure_type_when_regular"] = Proto("expenditure_type_when_regular", "ARMY_UPKEEP"),
                    ["income_type_when_regular"] = Proto("income_type_when_regular", "BACKGROUND_INCOME"),
                }),
                keyCols: ["id"]);
                notes.Add($"成本：新建 {restCost.Count} 个 Key（金额按输入的正负原样写，其余四列照抄包里现有成本行的写法）；" +
                          $"就地改 {placedCost.Count} 个已有 Key。");
            }
        }

        // 删成本 / 删"成本↔资源池"关联（改资源、清空资源、删成本都走这里；改原文件才能真的删掉作者的行）
        if (e.RemoveCost.Count > 0)
        {
            var dropIds = KeySet(e.RemoveCost);
            changed += RewriteDropping(pack, Costs, schema, repl, notes,
                (t, i) => dropIds.Contains(Cell(t, i, "id")));
        }
        if (e.RemovePoolCost.Count > 0)
        {
            var dropIds = KeySet(e.RemovePoolCost);
            // 本轮要写的那几条关联不能被误删（先按 resource_cost 删、再把本轮新建的排除掉）
            var keepPairs = KeySet(e.PoolCosts.Select(x => x.ResourceCost + "\u0001" + x.PoolFactor));
            changed += RewriteDropping(pack, "resource_cost_pooled_resource_junctions_tables", schema, repl, notes,
                (t, i) =>
                {
                    var c = Cell(t, i, "resource_cost");
                    if (!dropIds.Contains(c)) return false;
                    var f = Cell(t, i, "pooled_resource_factor");
                    return !keepPairs.Contains(c + "\u0001" + f);
                });
        }

        // ── 6a-2) 成本工坊：资源池 + 成本↔资源池（都是新增 → 覆盖表）──
        if (e.PoolFactors.Count > 0)
            changed += AddRows(pack, vanilla, "pooled_resource_factor_junctions_tables", schema, repl, notes,
                e.PoolFactors.Select(x => new Dictionary<string, object>
                {
                    ["unique_id"] = x.UniqueId,
                    ["factor"] = x.Factor,
                    ["resource"] = x.Resource,
                    ["minimum"] = x.Minimum,
                    ["maximum"] = x.Maximum,
                    ["specific_faction_set"] = x.SpecificFactionSet,   // 空 = 不填（用户口径）
                    ["sort_order"] = (long)x.SortOrder,
                }),
                keyCols: ["unique_id"]);
        if (e.PoolCosts.Count > 0)
        {
            // **同对就地覆盖**：包里已经有 (池, 成本) 这一对的（作者文件或我们的旧覆盖表）先剔掉，
            // 免得同键出现两份（游戏按加载顺序取我们的，但 RPFM 里能看到两条 —— "只改数量"最容易踩）。
            var pairDrop = KeySet(e.PoolCosts.Select(x => x.PoolFactor + "\u0001" + x.ResourceCost));
            changed += RewriteDropping(pack, "resource_cost_pooled_resource_junctions_tables", schema, repl, notes,
                (t, i) =>
                {
                    var f = Cell(t, i, "pooled_resource_factor");
                    var c = Cell(t, i, "resource_cost");
                    return f.Length > 0 && c.Length > 0 && pairDrop.Contains(f + "\u0001" + c);
                });
            changed += AddRows(pack, vanilla, "resource_cost_pooled_resource_junctions_tables", schema, repl, notes,
                e.PoolCosts.Select(x => new Dictionary<string, object>
                {
                    ["pooled_resource_factor"] = x.PoolFactor,
                    ["resource_cost"] = x.ResourceCost,
                    ["amount"] = x.Amount,
                    ["context"] = x.Context,                           // 照抄包里已有行（只作标签）
                    ["ui_resource_transaction_pooled_resource"] = x.UiPooled,
                }),
                keyCols: ["pooled_resource_factor", "resource_cost"]);  // 组合键：一个池能给多个成本条目用
        }

        // ── 6b) 兵 → 军事组 授权（加兵时按种族/派系自动填）──
        if (e.AddUnitGroup.Count > 0)
            changed += AddRows(pack, vanilla, "units_to_groupings_military_permissions_tables", schema, repl, notes,
                e.AddUnitGroup.Select(x => new Dictionary<string, object>
                {
                    ["unit"] = x.Unit,
                    ["military_group"] = x.MilitaryGroup,
                }));

        // ── 6b-2) 移出军事组（删 units_to_groupings_military_permissions 的行）──
        if (e.RemoveUnitGroup.Count > 0)
        {
            var drop = KeySet(e.RemoveUnitGroup.Select(x => x.Unit + "\u0001" + x.MilitaryGroup));
            changed += RewriteDropping(pack, "units_to_groupings_military_permissions_tables", schema, repl, notes,
                (t, i) => drop.Contains(Cell(t, i, "unit") + "\u0001" + Cell(t, i, "military_group")));
        }

        // ── 6c) 文件替换（换背景/按钮图、素材库往包里搬图：目标条目的字节换成来源的）──
        // 来源按顺序找：**本包** → **原版包** → **本地文件**（素材库里那份）。
        // 注意读字节要用"它来自哪个包"的那个 PackArchive —— 早先这里统一用 pack.ReadDecoded，
        // 一旦来源是原版包就会读错（或直接抛异常）。
        foreach (var (target, source) in e.FileReplacements)
        {
            byte[]? data = null;
            var inPack = pack.Find(source) ?? pack.VisibleEntries.FirstOrDefault(x =>
                string.Equals(x.Path.Replace('/', '\\'), source.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase));
            if (inPack is not null) data = pack.ReadDecoded(inPack);
            else
            {
                var inVan = vanilla?.Find(source) ?? vanilla?.VisibleEntries.FirstOrDefault(x =>
                    string.Equals(x.Path.Replace('/', '\\'), source.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase));
                if (inVan is not null && vanilla is not null) data = vanilla.ReadDecoded(inVan);
                else if (File.Exists(source)) data = File.ReadAllBytes(source);
            }
            if (data is null) { notes.Add($"换图：找不到来源 {source}（跳过）"); continue; }
            repl[target] = data;
            changed++;
            notes.Add($"换图：{target} ← {source}" + (File.Exists(source) ? "（本地素材）" : ""));
        }

        // ── 6d) 页签重命名：categories 行 + infos.category + twui + 两张图 ──
        foreach (var (oldKey, newKey) in e.TabRenames)
        {
            const string Cats = "unit_upgrade_group_ui_categories_tables";
            const string Infos2 = "unit_upgrade_group_ui_infos_tables";
            // categories：加新行、删旧行
            changed += AddRows(pack, vanilla, Cats, schema, repl, notes,
                new[] { new Dictionary<string, object> { [CatColumn(pack, vanilla, schema)] = newKey } });
            changed += RewriteDropping(pack, Cats, schema, repl, notes,
                (t, i) => Cell(t, i, CatColumn(pack, vanilla, schema)).Equals(oldKey, StringComparison.OrdinalIgnoreCase));
            // infos.category：把该页签的组改成新 key（改原文件里那一列的值）
            changed += RewriteChanging(pack, Infos2, schema, repl, notes, "category", oldKey, newKey);
            // **两张图 + twui 里的图路径/组件名/背景状态**（先定下图片的"真实新名字"，再拿它去改 twui）
            //   · 源名按 **twui 里实际引用的名字**取，不按约定名猜 —— 老版本改名只改了 holder_tab，
            //     页签的按钮组件/背景状态还叫旧 key，图也就还是旧名（写约定名 = 改到页签不看的文件上 ✗）；
            //   · 目标名已被占用（包里或本轮 repl 里）→ **不覆盖**，自动加 _1/_2 后缀
            //     （用户实测：SKVG→SKV2 把 SKV 正在用的 background_images_skv2.png 顶掉了 ✗）；
            //   · 源图如果是"本次换图"改过的（在 repl 里）→ 取**本轮缓冲**的字节，并撤掉旧名那条替换：
            //     旧文件保持包里原样（真·改名），新名字带上换的图（不然换图会被改名整块吃掉 ✗）。
            static bool InPackOrRepl(PackArchive pk, Dictionary<string, byte[]> rp, string path)
            {
                var norm = path.Replace((char)92, '/');
                if (rp.Keys.Any(k => k.Replace((char)92, '/').Equals(norm, StringComparison.OrdinalIgnoreCase))) return true;
                return pk.VisibleEntries.Any(x => x.Path.Replace((char)92, '/').Equals(norm, StringComparison.OrdinalIgnoreCase));
            }
            static void DropRepl(Dictionary<string, byte[]> rp, string path)
            {
                foreach (var k in rp.Keys.Where(k => k.Replace((char)92, '/').Equals(path.Replace((char)92, '/'),
                                                                                     StringComparison.OrdinalIgnoreCase)).ToList())
                    rp.Remove(k);
            }
            // twui（本轮缓冲优先：本会话先换图、后改名时，改名要接着改那一份）
            var twui = pack.VisibleEntries.FirstOrDefault(x => x.Path.EndsWith("warband_upgrades.twui.xml", StringComparison.OrdinalIgnoreCase));
            var xml = twui is null ? null : CurrentBytes(pack, repl, twui.Path) is { } tb
                ? System.Text.Encoding.UTF8.GetString(tb) : null;
            var bom = xml is { Length: > 0 } && xml[0] == '\uFEFF' ? "\uFEFF" : "";
            xml = xml?.TrimStart('\uFEFF');
            // 这个页签**现在**实际用的图与组件名（老遗留：页签 key 是 SKV2，按钮组件/状态还叫 skvg）
            var art = TwuiTabs.Of(xml, oldKey);
            var oldLow = oldKey.ToLowerInvariant();
            var newLow = newKey.ToLowerInvariant();
            var btnCompOld = art?.BtnComponent ?? oldLow;
            var stateOld = art?.PanelState ?? oldLow;
            var hasStructure = xml is not null && TwuiTabs.HasStructure(xml, oldKey);
            // **别的页签也在用同一张图 / 同一个按钮组件**（老遗留里会有）→ 那一处**不改名**：
            // 改名是"挪名字"，但 twui 里的图路径/组件名是**全局字符串替换** ——
            // 动了就会把别的页签一起改掉（用户明确要"别影响其他图"）。宁可名字不齐整。
            var allTabs = TwuiTabs.Parse(xml);
            bool UsedByOthers(string file) => file.Length > 0 && allTabs.Any(kv =>
                !kv.Key.Equals(oldKey, StringComparison.OrdinalIgnoreCase)
                && ((kv.Value.BgFile ?? "").Equals(file, StringComparison.OrdinalIgnoreCase)
                 || (kv.Value.BtnFile ?? "").Equals(file, StringComparison.OrdinalIgnoreCase)));
            bool BtnCompUsedByOthers() => allTabs.Any(kv =>
                !kv.Key.Equals(oldKey, StringComparison.OrdinalIgnoreCase)
                && (kv.Value.BtnComponent ?? "").Equals(btnCompOld, StringComparison.OrdinalIgnoreCase));
            var artRenames = new List<(string Kind, string OldLow, string NewLow)>();
            foreach (var (kind, actual) in new[] { ("background_images_", art?.BgFile), ("button_upgrade_", art?.BtnFile) })
            {
                var oldLowFile = actual ?? kind + oldLow + ".png";
                var srcPath = $"ui/skins/default/warband_upgrades/{oldLowFile}";
                var srcBytes = CurrentBytes(pack, repl, srcPath);
                if (srcBytes is null) { notes.Add($"重命名：{srcPath} 不在包里（这张图跳过）"); continue; }
                if (UsedByOthers(oldLowFile))
                {
                    notes.Add($"重命名：{oldLowFile} 还被别的页签用着 → **这张图不改名**（免得动到别的页签）；" +
                              $"{newKey} 的页签会继续用它，换图也照旧写这个文件。");
                    continue;
                }
                // 新名字：**正名优先**（<前缀><新key>.png，用户 2026-10-07 定的规则，TwuiTabs.PlanRename）——
                // 正名被一张**没人引用**的图占着 → 它让位成 <正名>_1（真·挪，内容不丢），自己的图用回正名；
                // 占用者在 twui 里有人引用 → 不动它，自己的图退回 _1（旧行为，免得动到别人的图）。
                // evictable 判据 = 文件名**不在 twui 文本里出现**（出现 = 有 component/状态引用它）。
                var (newLowFile, evictFrom, evictTo) = TwuiTabs.PlanRename(kind, newKey,
                    n => InPackOrRepl(pack, repl, $"ui/skins/default/warband_upgrades/{n}"),
                    n => xml is null || xml.IndexOf(n, StringComparison.OrdinalIgnoreCase) < 0);
                if (evictFrom is not null && evictTo is not null)
                {
                    var fromPath = $"ui/skins/default/warband_upgrades/{evictFrom}";
                    var toPath = $"ui/skins/default/warband_upgrades/{evictTo}";
                    var parked = CurrentBytes(pack, repl, fromPath);
                    if (parked is not null)
                    {
                        repl[toPath] = parked;                 // 让位：老图挪到 _1（和"自己改名"同构：真·挪，不复制）
                        DropRepl(repl, fromPath);
                        if (pack.Find(fromPath) is not null && !e.RemoveFiles.Contains(fromPath, StringComparer.OrdinalIgnoreCase))
                            e.RemoveFiles.Add(fromPath);
                        changed++;
                        notes.Add($"重命名：正名 {evictFrom} 原来被一张没人引用的图占着 → 它挪成 {evictTo}（内容不丢）；" +
                                  $"{newKey} 的图直接占正名。");
                    }
                    else
                    {   // 读不到内容就当没让位（不该发生）：退回"自己带 _1"的旧行为
                        newLowFile = TwuiTabs.PlanTargetName(kind, newKey,
                            n => InPackOrRepl(pack, repl, $"ui/skins/default/warband_upgrades/{n}"));
                        notes.Add($"重命名：{evictFrom} 读不到内容，让位跳过（改名后的图用 {newLowFile}）。");
                    }
                }
                var dstPath = $"ui/skins/default/warband_upgrades/{newLowFile}";
                repl[dstPath] = srcBytes;
                DropRepl(repl, srcPath);
                // **真·改名 = 挪**：旧条目从包里删掉（用户要的："直接把原图重命名"，不是复制一张留着旧的）。
                // 只有"别的页签还在用"才会走上面的分支保留旧名。
                if (pack.Find(srcPath) is not null && !e.RemoveFiles.Contains(srcPath, StringComparer.OrdinalIgnoreCase))
                    e.RemoveFiles.Add(srcPath);
                changed++;
                artRenames.Add((kind, oldLowFile, newLowFile));
                notes.Add($"重命名：{oldLowFile} → {newLowFile}" +
                          (newLowFile != kind + newLow + ".png" ? "（原名字已被占用 → 自动加 _N，不覆盖）" : "") +
                          "（旧名从包里删掉 = 真·改名）" +
                          (actual is not null && !actual.Equals(kind + oldLow + ".png", StringComparison.OrdinalIgnoreCase)
                              ? $"（页签实际用的就是 {actual}，按它改的）" : ""));
            }
            // twui：holder_tab_<旧> → 新、按钮组件名、**背景状态名**、value="<旧>" → 新、图路径 → 真实新名字
            if (xml is not null && twui is not null)
            {
                var n1 = Regex.Replace(xml, "holder_tab_" + Regex.Escape(oldKey) + @"\b", "holder_tab_" + newKey,
                                       RegexOptions.IgnoreCase);
                // 按钮组件：holder_tab 里挂着的是哪个组件就改哪个（老遗留的组件名可能不是 key）
                var btnRenamed = true;
                if (BtnCompUsedByOthers())
                {
                    btnRenamed = false;
                    notes.Add($"重命名：按钮组件 button_toggle_tab_{btnCompOld} 还被别的页签引用 → **组件名不改**（免得动到它）。");
                }
                else
                    n1 = Regex.Replace(n1, "button_toggle_tab_" + Regex.Escape(btnCompOld) + @"\b", "button_toggle_tab_" + newLow,
                                       RegexOptions.IgnoreCase);
                n1 = Regex.Replace(n1, "value=\"" + Regex.Escape(oldKey) + "\"", "value=\"" + newKey + "\"",
                                   RegexOptions.IgnoreCase);
                foreach (var (_, o, nn) in artRenames)
                {
                    n1 = n1.Replace(o, nn);                              // 正斜杠（twui 里的写法）
                    n1 = n1.Replace(o.Replace('/', (char)92), nn.Replace('/', (char)92));
                }
                // **背景状态名必须 = 页签 key**（游戏按 key 切这个状态；不改的话改名后面板背景不跟着走，
                // 用户实测"SKVG 改名 SKV2 后显示的还是 SKV 的图"就是这个）；没有这个状态就补一个。
                if (WarbandTabArt.FindState(n1, stateOld) is not null)
                {
                    if (!stateOld.Equals(newLow, StringComparison.OrdinalIgnoreCase))
                        n1 = WarbandTabArt.RenameState(n1, stateOld, newLow);
                }
                else if (WarbandTabArt.FindState(n1, newLow) is null)
                {
                    var donor = TwuiTabs.StemOf(artRenames.Count > 0 ? artRenames[0].NewLow : null) ?? stateOld;
                    if (WarbandTabArt.FindState(n1, donor) is not null)
                    {
                        n1 = WarbandTabArt.CloneState(n1, donor, newLow);
                        notes.Add($"重命名：页签 {newKey} 原本没有背景状态 → 按 {donor} 补了一个 <{newLow}>" +
                                  "（缺这个状态时游戏里面板背景不跟着页签走）。");
                    }
                }
                if (n1 != xml)
                {
                    repl[twui.Path] = System.Text.Encoding.UTF8.GetBytes(bom + n1);
                    changed++;
                    notes.Add($"重命名：twui 里 holder_tab_{oldKey} → {newKey}" +
                              (btnRenamed ? $"、按钮组件 {btnCompOld} → {newLow}" : "、按钮组件不动") +
                              (artRenames.Count > 0 ? $"、图路径 {string.Join("、", artRenames.Select(x => x.OldLow + "→" + x.NewLow))}" : ""));
                }
            }
            // **建了一半的页签**（有 categories 行/组/图，twui 里却没有 holder_tab）：
            // 只改图没用 —— 游戏里这个页签压根不出现（页签体检一直在报）。改名时顺手**补全**：
            // 克隆一个母版页签（holder 本体 + hierarchy 节点 + 按钮组件 + 背景状态 + 图条目），
            // 图就用**刚改好名字的那两张**（已在 repl 里 → Build 不覆盖）。
            if (xml is not null && twui is not null && !hasStructure)
            {
                string? bgTarget = artRenames.FirstOrDefault(x => x.Kind == "background_images_").NewLow
                                   ?? art?.BgFile ?? TwuiTabs.ConventionName("background_images_", newLow);
                string? btnTarget = artRenames.FirstOrDefault(x => x.Kind == "button_upgrade_").NewLow
                                   ?? art?.BtnFile ?? TwuiTabs.ConventionName("button_upgrade_", newLow);
                var r = WarbandNewTab.Build(pack, newKey, null, repl, notes,
                                            TwuiTabs.SkinDir + bgTarget, TwuiTabs.SkinDir + btnTarget,
                                            bgTarget, btnTarget);
                if (r.Ok)
                {
                    changed += r.Files.Count;
                    notes.Add($"重命名：页签 {newKey} 在 twui 里**本来就没有结构**（建了一半的页签）→ 顺手按母版 {r.Donor} 补全了" +
                              $"（holder/按钮/背景状态/图条目，图用改名后的 {bgTarget} / {btnTarget}）。");
                }
                else notes.Add($"重命名：页签 {newKey} 的结构补全失败（{r.Error}）——游戏里这个页签不会显示，" +
                               "用「新建页签」把 key 填成同名也能补。");
            }
            notes.Add($"页签重命名：{oldKey} → {newKey}");
        }

        // ── 6d-2) 页签背景状态自愈：**状态名必须 = 页签 key** ──
        // 页签在游戏里的面板背景 = `warband_upgrades` 组件 <states> 下按页签 key 命名的那个状态
        // （TwuiTabs 解析；缺了它游戏按新 key 找不到状态，面板背景不跟着页签走 —— 用户实测
        // "SKVG 改名 SKV2 后显示的是 SKV 的图"、"换图没生效"都是它）。老版本改名只改了 holder_tab，
        // 状态名还留在旧 key 上 → 这里给"有组在用的页签"补一个按 key 命名的状态（幂等，已有就跳过）。
        try
        {
            var twui2 = pack.VisibleEntries.FirstOrDefault(x => x.Path.EndsWith("warband_upgrades.twui.xml", StringComparison.OrdinalIgnoreCase));
            var tb2 = twui2 is null ? null : CurrentBytes(pack, repl, twui2.Path);
            var xml2 = tb2 is null ? null : System.Text.Encoding.UTF8.GetString(tb2);
            if (xml2 is not null && twui2 is not null)
            {
                var bom2 = xml2.Length > 0 && xml2[0] == '\uFEFF' ? "\uFEFF" : "";
                xml2 = xml2.TrimStart('\uFEFF');
                var want = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in e.InfoEdits)
                    if (kv.Value.Category is { Length: > 0 } c0) want.Add(c0);
                foreach (var f in TablePaths(pack, Infos, repl))
                {
                    var b = CurrentBytes(pack, repl, f);
                    if (b is null) continue;
                    DbTable t;
                    try { t = DbTable.Decode(b, Infos, schema); } catch { continue; }
                    var c = t.Columns.FindIndex(x => x.Name.Equals("category", StringComparison.OrdinalIgnoreCase));
                    if (c < 0) continue;
                    foreach (var r in t.Rows) { var v = r[c].ToTsv(); if (v.Length > 0) want.Add(v); }
                }
                // 本次换过图的页签（换图目标 = 某个页签实际用的背景图）也要保状态
                foreach (var (target, _) in e.FileReplacements)
                {
                    var tn = TwuiTabs.FileName(target);
                    if (!tn.StartsWith("background_images_", StringComparison.OrdinalIgnoreCase)) continue;
                    var stem = TwuiTabs.StemOf(tn);
                    if (stem is not null) want.Add(stem);
                }
                var adds = new List<string>();
                foreach (var key in want)
                {
                    if (WarbandTabArt.FindState(xml2, key) is not null) continue;
                    var art2 = TwuiTabs.Of(xml2, key);
                    var donor = art2?.PanelState ?? TwuiTabs.StemOf(art2?.BgFile) ?? TwuiTabs.StemOf(art2?.BtnFile);
                    if (donor is null || WarbandTabArt.FindState(xml2, donor) is null) continue;
                    xml2 = WarbandTabArt.CloneState(xml2, donor, key.ToLowerInvariant());
                    adds.Add($"{key}（母版状态 {donor}）");
                }
                if (adds.Count > 0)
                {
                    repl[twui2.Path] = System.Text.Encoding.UTF8.GetBytes(bom2 + xml2);
                    changed++;
                    notes.Add($"页签背景状态：补了 {adds.Count} 个（{string.Join("、", adds.Take(6))}{(adds.Count > 6 ? " 等" : "")}）" +
                              "—— 状态名必须等于页签 key，缺了它游戏里面板背景不跟着页签走（换图会看着像没生效）。");
                }
            }
        }
        catch (Exception ex) { notes.Add("页签背景状态自愈跳过：" + ex.Message); }

        // ── 6e) 加进画布的兵自动解锁战役经验（main_units.restrict_xp_gain_in_campaign = false）──
        if (e.UnlockXp.Count > 0)
        {
            const string MU = "main_units_tables";
            const string Col2 = "restrict_xp_gain_in_campaign";
            if (TryMeta(pack, vanilla, MU, schema, out var muMeta, out var muOwner))
            {
                var src = muOwner is null ? null : TableFiles.ReadMerged(muOwner, MU, schema, null);
                var kIdx = src is null ? -1 : TableFiles.FindKeyColumn(src);
                var rows = new List<DbValue[]>();
                if (src is not null && kIdx >= 0)
                    foreach (var u in e.UnlockXp.Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        var at = src.Rows.FindIndex(r => r[kIdx].ToTsv().Equals(u, StringComparison.OrdinalIgnoreCase));
                        if (at < 0) { notes.Add($"解锁经验：原版 main_units 里没有 {u}（跳过）"); continue; }
                        var raw = (DbValue[])src.RawRows[at].Clone();
                        Set(raw, muMeta, Col2, DbValue.Of(false));
                        rows.Add(raw);
                    }
                if (rows.Count > 0)
                {
                    // 和已有的 studio_elite_unlock 合并（不覆盖上一次的结果；本轮缓冲优先）
                    var inner = $"db/{MU}/{WarbandExporter.UnlockTableFileName}";
                    var existingRows = new List<DbValue[]>();
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var cur = CurrentBytes(pack, repl, inner);
                    if (cur is not null)
                    {
                        var t = DbTable.Decode(cur, MU, schema);
                        foreach (var r in t.RawRows)
                        {
                            existingRows.Add(r);
                            if (r.Length > 0) seen.Add(r[0].RawStr ?? r[0].Str ?? "");
                        }
                    }
                    var added = 0;
                    foreach (var r in rows)
                    {
                        var k2 = r.Length > 0 ? r[0].RawStr ?? r[0].Str ?? "" : "";
                        if (k2.Length > 0 && !seen.Add(k2)) continue;
                        existingRows.Add(r); added++;
                    }
                    if (added > 0)
                    {
                        repl[inner] = Encode(muMeta, existingRows);
                        changed++;
                        notes.Add($"解锁经验：{added} 个兵写进 {inner}（restrict_xp_gain_in_campaign=false）");
                    }
                }
            }
        }

        // ── 7) 新建页签（twui + 两张图 + categories 行）──
        if (e.NewTabs.Count > 0)
        {
            const string Cats = "unit_upgrade_group_ui_categories_tables";
            foreach (var (key, donor, bgSrc, btnSrc) in e.NewTabs)
            {
                var r = WarbandNewTab.Build(pack, key, donor, repl, notes, bgSrc, btnSrc);
                if (!r.Ok) { notes.Add($"新建页签 {key} 失败：{r.Error}"); continue; }
                changed += r.Files.Count;
                changed += AddRows(pack, vanilla, Cats, schema, repl, notes,
                    new[] { new Dictionary<string, object> { [CatColumn(pack, vanilla, schema)] = r.Key } },
                    keyCols: [CatColumn(pack, vanilla, schema)]);
                notes.Add($"页签 {r.Key}：母版 {r.Donor}，改了 {r.Files.Count} 个文件 + categories 行。");
            }
        }

        // ── 8) 页签体检：有组在用的分类，结构（categories 行 + twui holder_tab_<key>）必须齐 ──
        // 用户实测踩过：建了 SKVG 页签、也把组放进去了，但 categories 行 / twui 块没写进包 →
        // 进游戏这个页签**根本不出现**（页签要 categories + 分类下有组 + twui 块三样齐全）。
        // 这里只**提醒**，不自动补（补页签是「新建页签」的活，key 与母版有讲究）。
        try
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in e.InfoEdits)
                if (kv.Value.Category is { Length: > 0 } c0) used.Add(c0);
            foreach (var f in TablePaths(pack, Infos, repl))
            {
                var b = CurrentBytes(pack, repl, f);
                if (b is null) continue;
                DbTable t;
                try { t = DbTable.Decode(b, Infos, schema); } catch { continue; }
                var c = t.Columns.FindIndex(x => x.Name.Equals("category", StringComparison.OrdinalIgnoreCase));
                if (c < 0) continue;
                foreach (var r in t.Rows) { var v = r[c].ToTsv(); if (v.Length > 0) used.Add(v); }
            }
            var haveCat = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in TablePaths(pack, "unit_upgrade_group_ui_categories_tables", repl))
            {
                var b = CurrentBytes(pack, repl, f);
                if (b is null) continue;
                DbTable t;
                try { t = DbTable.Decode(b, "unit_upgrade_group_ui_categories_tables", schema); } catch { continue; }
                foreach (var r in t.Rows) { var v = r[0].ToTsv(); if (v.Length > 0) haveCat.Add(v); }
            }
            string? twuiTxt = null;
            var twuiEntry = pack.VisibleEntries.FirstOrDefault(x => x.Path.EndsWith("warband_upgrades.twui.xml", StringComparison.OrdinalIgnoreCase));
            if (twuiEntry is not null)
            {
                var b = CurrentBytes(pack, repl, twuiEntry.Path);
                if (b is not null) twuiTxt = System.Text.Encoding.UTF8.GetString(b);
            }
            var bad = new List<string>();
            foreach (var c in used)
            {
                var ok = haveCat.Contains(c);
                if (ok && twuiTxt is not null && twuiTxt.IndexOf("holder_tab_" + c, StringComparison.OrdinalIgnoreCase) < 0) ok = false;
                if (!ok) bad.Add(c);
            }
            if (bad.Count > 0)
                notes.Add($"⚠ 页签体检：{string.Join("、", bad.Take(8))}{(bad.Count > 8 ? " 等" : "")} 有组在用，但 categories 行或 twui 块缺着" +
                          " → 游戏里这些页签不会显示。**改一次名**（换成别的 key）会自动补全结构（holder/按钮/背景状态/图条目）" +
                          "并把图挪成新名字；也可以「新建页签」把 key 填成同名（放到这一页的组会自动归到它底下）。");
        }
        catch { }

        // **本轮写入赢过"删除名单"**：同一路径既在 RemoveFiles 又要在 repl 里写新内容时，不能把它一起跳过
        //（PackWriter 的 drop 判断在替换之前）。改名"正名优先"让位时就会撞上：让位者把正名加进删除名单、
        //  自己的图又写进同一个正名 —— 曾经导出后正名图整个丢失（v1.4.3 用 probe tab-art 实测到）。
        {
            var writing = new HashSet<string>(repl.Keys.Select(k => k.Replace((char)92, '/')), StringComparer.OrdinalIgnoreCase);
            e.RemoveFiles.RemoveAll(p => writing.Contains(p.Replace((char)92, '/')));
        }

        return changed;
    }

    /// <summary>categories 表的列名（原版/模组里叫 category）。</summary>
    private static string CatColumn(PackArchive pack, PackArchive? vanilla, Schema schema)
    {
        if (TryMeta(pack, vanilla, "unit_upgrade_group_ui_categories_tables", schema, out var meta, out _)
            && meta.Def.Fields.Count > 0)
            return meta.Def.Fields[0].Name;
        return "category";
    }

    // ───────────────────────────── 内部工具 ─────────────────────────────

    /// <summary>老版本用的覆盖表名 → 现在的名字（改了名才排得最后，见 MigrateLegacy）。</summary>
    private static readonly (string Old, string New)[] LegacyOverrideNames =
    [
        ("studio_edits", EditFileName),
        ("studio_layout", LayoutFileName),
        ("studio_elite_unlock", WarbandExporter.UnlockTableFileName),
    ];

    /// <summary>
    /// 旧名覆盖表搬家：老版本把覆盖表写成 <c>db/&lt;表&gt;/studio_edits</c> / <c>studio_layout</c> /
    /// <c>studio_elite_unlock</c>，名字排不到最后 —— 同一张表多文件按文件名定"谁后加载谁赢"，
    /// MOD 自己的文件（Yukino_* 等）会把它们顶掉（实测：坐标/页签改了又回退，就是这个）。
    /// 这里把旧文件整份搬到 <c>zzzz_</c> 新名下（本轮后续写入会在它基础上合并），旧条目记进删除名单。
    /// </summary>
    private static void MigrateLegacy(PackArchive pack, Dictionary<string, byte[]> repl, WarbandEdits e, List<string> notes)
    {
        foreach (var entry in pack.VisibleEntries)
        {
            var path = entry.Path.Replace('\\', '/');
            if (!path.StartsWith("db/", StringComparison.OrdinalIgnoreCase)) continue;
            var slash = path.LastIndexOf('/');
            if (slash < 0) continue;
            var name = path[(slash + 1)..];
            foreach (var (oldName, newName) in LegacyOverrideNames)
            {
                if (!name.Equals(oldName, StringComparison.OrdinalIgnoreCase)) continue;
                var newPath = path[..(slash + 1)] + newName;
                // 新名文件已经存在（repl 里刚写过 / 包里已经有）→ **不搬**：搬过去会盖掉它。
                // 这种"两个文件都在"的状态只可能来自半途升级，留着旧文件不影响正确性
                // （同名 key 以 zzzz_ 为准，旧文件里独有的行照旧生效）。
                var exists = repl.Keys.Any(k => k.Replace('\\', '/').Equals(newPath, StringComparison.OrdinalIgnoreCase))
                             || pack.VisibleEntries.Any(x => x.Path.Replace('\\', '/').Equals(newPath, StringComparison.OrdinalIgnoreCase));
                if (exists)
                {
                    notes.Add($"迁移：{path} 跳过（{newName} 已存在，避免把新内容盖掉）");
                    continue;
                }
                repl[newPath] = pack.ReadDecoded(entry);
                if (!e.RemoveFiles.Contains(path, StringComparer.OrdinalIgnoreCase)) e.RemoveFiles.Add(path);
                notes.Add($"迁移：{path} → {newPath}（旧名排不到最后，会被 MOD 自己的文件顶掉）");
            }
        }
    }

    private static HashSet<string> KeySet(IEnumerable<string> keys) => new(keys, StringComparer.OrdinalIgnoreCase);

    /// <summary>一对组的"无序"身份键：一对组只允许有一条界面连线（谁当 child 都算同一对）。</summary>
    private static string LinkPairKey(string a, string b) =>
        string.Compare(a, b, StringComparison.OrdinalIgnoreCase) <= 0
            ? a + "\u0001" + b : b + "\u0001" + a;

    /// <summary>
    /// 读某个包内路径的"当前内容"：**先看本轮缓冲（repl），再看原包**。
    /// repl 里放的是本轮已经算好的结果（含本轮新增的行），而原包是旧内容；
    /// 若后面的步骤（删行 / 改列 / 页签克隆）读原包，就会把本轮的写入整块盖掉 ——
    /// 实测：导出包里已经有 studio_edits 时，"先加后删""先重命名后建页签"都会静默丢行。
    /// </summary>
    private static byte[]? CurrentBytes(PackArchive pack, Dictionary<string, byte[]> repl, string path)
    {
        if (repl.TryGetValue(path, out var buf)) return buf;
        foreach (var kv in repl)
            if (kv.Key.Replace('/', '\\').Equals(path.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase))
                return kv.Value;
        var e = pack.Find(path) ?? pack.VisibleEntries.FirstOrDefault(x =>
            string.Equals(x.Path.Replace('/', '\\'), path.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase));
        return e is null ? null : pack.ReadDecoded(e);
    }

    private static bool TryMeta(PackArchive pack, PackArchive? vanilla, string table, Schema schema,
                                out Meta meta, out PackArchive? owner)
    {
        foreach (var src in new[] { pack, vanilla })
        {
            if (src is null) continue;
            foreach (var f in TableFiles.EntriesFor(src, table))
            {
                var t = DbTable.Decode(src.ReadDecoded(f), table, schema);
                meta = new Meta(t.Definition, t.Guid ?? Guid.NewGuid().ToString(), t.MysteriousByte);
                owner = src;
                return true;
            }
        }
        meta = new Meta(new SchemaDefinition { TableName = table, Version = 0 }, Guid.NewGuid().ToString(), false);
        owner = null;
        return false;
    }

    /// <summary>按字段名建一行"原始值"（RawRows 的顺序 = 定义顺序）。</summary>
    private static DbValue[] Build(Meta meta, Dictionary<string, object> values)
    {
        var fields = meta.Def.Fields;
        var raw = new DbValue[fields.Count];
        for (var i = 0; i < raw.Length; i++)
        {
            var f = fields[i];
            if (!values.TryGetValue(f.Name, out var v))
            {
                raw[i] = Default(f);
                continue;
            }
            raw[i] = v switch
            {
                string s => f.Type == SchemaFieldType.ColourRGB ? DbValue.OfColour(s) : DbValue.OfStr(s),
                long l => DbValue.Of(l),
                int n => DbValue.Of((long)n),
                double d => DbValue.Of(d),
                float fl => DbValue.Of((double)fl),
                bool b => DbValue.Of(b),
                _ => DbValue.OfStr(v.ToString() ?? ""),
            };
        }
        return raw;
    }

    private static DbValue Default(SchemaField f) => f.Type switch
    {
        SchemaFieldType.Boolean => DbValue.Of(false),
        SchemaFieldType.F32 or SchemaFieldType.F64 => DbValue.Of(0d),
        SchemaFieldType.I16 or SchemaFieldType.I32 or SchemaFieldType.I64 => DbValue.Of(0L),
        SchemaFieldType.ColourRGB => DbValue.OfColour("FFFFFF"),
        SchemaFieldType.OptionalStringU8 or SchemaFieldType.OptionalStringU16 =>
            new DbValue(DbValueKind.Str, s: "", present: false),
        SchemaFieldType.OptionalI16 or SchemaFieldType.OptionalI32 or SchemaFieldType.OptionalI64 =>
            new DbValue(DbValueKind.Int, i: 0, present: false),
        _ => DbValue.OfStr(""),
    };

    private static void Set(DbValue[] raw, Meta meta, string field, DbValue v)
    {
        var i = meta.Def.Fields.FindIndex(f => f.Name.Equals(field, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) raw[i] = v;
    }

    /// <summary>原始行 → 显示行（编码只看 RawRows，显示行只是凑齐列数）。</summary>
    private static DbValue[] ToDisplay(DbValue[] raw, Meta meta)
    {
        var n = Schema.ProcessedColumns(meta.Def).Count;
        var row = new DbValue[Math.Max(n, raw.Length)];
        for (var i = 0; i < row.Length; i++) row[i] = i < raw.Length ? raw[i] : DbValue.OfStr("");
        return row;
    }

    private static byte[] Encode(Meta meta, List<DbValue[]> raws)
    {
        var t = new DbTable
        {
            TableName = meta.Def.TableName,
            Version = meta.Def.Version,
            Guid = Guid.NewGuid().ToString(),
            MysteriousByte = meta.Mysterious,
            Columns = Schema.ProcessedColumns(meta.Def),
            Rows = raws.Select(r => ToDisplay(r, meta)).ToList(),
            RawRows = raws,
            Definition = meta.Def,
        };
        return DbEncoder.Encode(t);
    }

    /// <summary>按"这张表原来长什么样"重编（保留它自己的定义与 GUID，就地改行时用）。</summary>
    private static byte[] EncodeLike(DbTable t, List<DbValue[]> raws)
    {
        var nt = new DbTable
        {
            TableName = t.TableName,
            Version = t.Version,
            Guid = t.Guid,
            MysteriousByte = t.MysteriousByte,
            Columns = t.Columns,
            Rows = raws.Select(r => ToDisplay(r, new Meta(t.Definition, t.Guid ?? "", t.MysteriousByte))).ToList(),
            RawRows = raws,
            Definition = t.Definition,
        };
        return DbEncoder.Encode(nt);
    }

    /// <summary>
    /// 往 <c>db/&lt;表&gt;/studio_edits</c> 追加行（本包里已有同名文件就接着写）。
    /// <paramref name="keyCols"/> = 这张表的"身份列"：
    ///   · 有身份列（键表）→ **同身份以最后一条为准**（覆盖表语义；否则"建了路线再改金额/交换方向"会被静默丢掉）；
    ///   · 没有身份列（键不唯一的表，如 junction / links）→ 按**整行**去重，追加而不是互相覆盖
    ///     （同一个兵可以同时在两个组里，按第一列去重会把另一行吃掉）。
    /// </summary>
    private static int AddRows(PackArchive pack, PackArchive? vanilla, string table, Schema schema,
                               Dictionary<string, byte[]> repl, List<string> notes,
                               IEnumerable<Dictionary<string, object>> rows, string[]? keyCols = null)
    {
        if (!TryMeta(pack, vanilla, table, schema, out var meta, out _))
        {
            notes.Add($"{table}：原始包与原版包里都没有这张表，这部分编辑没落表。");
            return 0;
        }
        var innerPath = $"db/{table}/{EditFileName}";
        var raws = new List<DbValue[]>();
        var at = new Dictionary<string, int>(StringComparer.Ordinal);

        string Sig(DbValue[] raw)
        {
            if (keyCols is null || keyCols.Length == 0)
                return string.Join("\u0001", raw.Select(v => v.ToTsv()));
            var parts = new List<string>();
            foreach (var c in keyCols)
            {
                var i = meta.Def.Fields.FindIndex(f => f.Name.Equals(c, StringComparison.OrdinalIgnoreCase));
                parts.Add(i >= 0 && i < raw.Length ? raw[i].ToTsv() : "");
            }
            return string.Join("\u0001", parts);
        }

        void Take(byte[]? bytes)
        {
            if (bytes is null) return;
            var t = DbTable.Decode(bytes, table, schema);
            foreach (var r in t.RawRows)
            {
                var k = Sig(r);
                if (k.Length > 0 && at.TryGetValue(k, out var old)) { raws[old] = r; continue; }
                if (k.Length > 0) at[k] = raws.Count;
                raws.Add(r);
            }
        }

        var existing = CurrentBytes(pack, repl, innerPath);      // repl 优先：本轮早先写过的行要保住
        if (existing is not null) Take(existing);

        var added = 0;
        var replaced = 0;
        foreach (var values in rows)
        {
            var raw = Build(meta, values);
            var key = Sig(raw);
            if (key.Length > 0 && at.TryGetValue(key, out var old))
            {
                raws[old] = raw;                       // 同身份：后写的覆盖先写的
                replaced++;
                continue;
            }
            if (key.Length > 0) at[key] = raws.Count;
            raws.Add(raw);
            added++;
        }
        if (added == 0 && replaced == 0) return 0;

        repl[innerPath] = Encode(meta, raws);
        notes.Add($"{innerPath}：新增 {added} 行" +
                  (replaced > 0 ? $"，覆盖同键 {replaced} 行（后写的为准）" : "") + "。");
        return 1;
    }

    /// <summary>把某张表里 column 列等于 oldValue 的行改成 newValue（页签重命名改 infos.category 用）。</summary>
    private static int RewriteChanging(PackArchive pack, string table, Schema schema,
                                       Dictionary<string, byte[]> repl, List<string> notes,
                                       string column, string oldValue, string newValue)
    {
        var changed = 0;
        foreach (var f in TablePaths(pack, table, repl))
        {
            var bytes0 = CurrentBytes(pack, repl, f);
            if (bytes0 is null) continue;
            var t = DbTable.Decode(bytes0, table, schema);
            var c = t.Columns.FindIndex(x => x.Name.Equals(column, StringComparison.OrdinalIgnoreCase));
            if (c < 0) continue;
            var raws = new List<DbValue[]>();
            var hit = 0;
            for (var i = 0; i < t.Rows.Count; i++)
            {
                var raw = t.RawRows[i];
                if (t.Rows[i][c].ToTsv().Equals(oldValue, StringComparison.OrdinalIgnoreCase))
                {
                    var rawCopy = (DbValue[])raw.Clone();
                    var rc = t.Definition.Fields.FindIndex(x => x.Name.Equals(column, StringComparison.OrdinalIgnoreCase));
                    if (rc >= 0) rawCopy[rc] = DbValue.OfStr(newValue);
                    raws.Add(rawCopy);
                    hit++;
                }
                else raws.Add(raw);
            }
            if (hit == 0) continue;
            var nt = new DbTable
            {
                TableName = t.TableName, Version = t.Version, Guid = t.Guid, MysteriousByte = t.MysteriousByte,
                Columns = t.Columns,
                Rows = raws.Select(r => ToDisplay(r, new Meta(t.Definition, t.Guid ?? "", t.MysteriousByte))).ToList(),
                RawRows = raws, Definition = t.Definition,
            };
            repl[f] = DbEncoder.Encode(nt);
            notes.Add($"{f}：{hit} 行的 {column} 改成 {newValue}");
            changed++;
        }
        return changed;
    }

    /// <summary>
    /// 这张表要处理哪些文件：原包里的 + **本轮新写进 repl 的**（例如本轮新建的 studio_edits）。
    /// 少了后者，"先加后删/先加后改"就会漏掉本轮的新文件。
    /// </summary>
    private static List<string> TablePaths(PackArchive pack, string table, Dictionary<string, byte[]> repl)
    {
        var paths = new List<string>();
        foreach (var f in TableFiles.EntriesFor(pack, table)) paths.Add(f.Path);
        var prefix = $"db/{table}/";
        foreach (var p in repl.Keys)
            if (p.Replace('\\', '/').StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && !paths.Contains(p, StringComparer.OrdinalIgnoreCase))
                paths.Add(p);
        return paths;
    }

    /// <summary>删行：只能改原文件（覆盖表删不掉别人的行）。逐文件滤掉目标行后原样重编。</summary>
    private static int RewriteDropping(PackArchive pack, string table, Schema schema,
                                       Dictionary<string, byte[]> repl, List<string> notes,
                                       Func<DbTable, int, bool> drop)
    {
        var changed = 0;
        foreach (var f in TablePaths(pack, table, repl))
        {
            var bytes0 = CurrentBytes(pack, repl, f);
            if (bytes0 is null) continue;
            var t = DbTable.Decode(bytes0, table, schema);
            var keepRows = new List<DbValue[]>();
            var keepRaw = new List<DbValue[]>();
            var dropped = 0;
            for (var i = 0; i < t.Rows.Count; i++)
            {
                if (drop(t, i)) { dropped++; continue; }
                keepRows.Add(t.Rows[i]);
                keepRaw.Add(t.RawRows[i]);
            }
            if (dropped == 0) continue;
            var nt = new DbTable
            {
                TableName = t.TableName,
                Version = t.Version,
                Guid = t.Guid,
                MysteriousByte = t.MysteriousByte,
                Columns = t.Columns,
                Rows = keepRows,
                RawRows = keepRaw,
                Definition = t.Definition,
            };
            repl[f] = DbEncoder.Encode(nt);
            notes.Add($"{f}：删掉 {dropped} 行");
            changed++;
        }
        return changed;
    }

    /// <summary>某行某列的值（按列名找）。</summary>
    private static string Cell(DbTable t, int row, string column)
    {
        var c = t.Columns.FindIndex(x => x.Name.Equals(column, StringComparison.OrdinalIgnoreCase));
        return c < 0 ? "" : t.Rows[row][c].ToTsv();
    }
}
