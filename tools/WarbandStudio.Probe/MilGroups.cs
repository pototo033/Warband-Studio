using WarbandStudio.Packfile;
using WarbandStudio.Rpfm;

namespace WarbandStudio.Probe;

/// <summary>
/// 军事组归属分类（口径按用户 2026-09-30 的说明）：
///   · 只看**传奇派系**（frontend_factions_tables 里、且不带 prologue）的 military_group；
///   · 每个种族里，被最多传奇派系使用的那个军事组 = **通用军事组**；
///   · 其余（用得比它少的）= 该派系的**专属军事组**（哪怕这个组有叛军之类在用，也只数传奇）。
/// 例：帝国 wh_main_group_empire（通用）vs wh_main_group_empire_reikland（瑞克领专属）；
///     震旦 wh3_main_cth（通用）vs wh3_cp1_group_cth_bhashiva（珀西瓦专属）。
/// </summary>
public static class MilGroups
{
    public sealed record Row(string Faction, string Race, string Group, bool Generic);

    public static int Run(string[] a)
    {
        if (a.Length < 3) { Console.Error.WriteLine("用法: probe milgroups <pack> <原版db.pack> [种族]"); return 1; }
        var schema = WarbandStudio.Packfile.Schema.Load(RpfmCli.LocateSchema()!);
        var notes = new List<string>();
        using var pack = PackArchive.Open(Path.GetFullPath(a[1]));
        using var vanilla = PackArchive.Open(Path.GetFullPath(a[2]));
        var only = a.Length > 3 ? a[3] : null;

        DbTable? M(string t) => TableFiles.Merge(
            TableFiles.ReadMerged(vanilla, t, schema, notes), TableFiles.ReadMerged(pack, t, schema, notes));

        var fac = M("factions_tables") ?? throw new InvalidDataException("没有 factions_tables");
        var front = M("frontend_factions_tables");
        var subs = M("cultures_subcultures_tables");

        static int Col(DbTable t, params string[] names)
        {
            foreach (var n in names)
            {
                var i = t.Columns.FindIndex(c => c.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
                if (i >= 0) return i;
            }
            return -1;
        }

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
        // 亚文化的显示名（用于标题）
        var subName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (subs is not null)
        {
            var s0 = Col(subs, "subculture", "key"); var s1 = Col(subs, "name");
            if (s0 >= 0 && s1 >= 0)
                foreach (var r in subs.Rows) subName[r[s0].ToTsv()] = r[s1].ToTsv();
        }

        var rows = new List<Row>();
        foreach (var r in fac.Rows)
        {
            var key = r[fk].ToTsv();
            if (key.Length == 0 || !legendary.Contains(key)) continue;              // 只数传奇
            var race = (fs >= 0 ? r[fs].ToTsv() : "").Replace("_pro_sc_", "_sc_", StringComparison.OrdinalIgnoreCase);
            var mg = fm >= 0 ? r[fm].ToTsv() : "";
            if (mg.Length == 0) continue;
            rows.Add(new Row(key, race, mg, false));
        }

        var byRace = rows.GroupBy(x => x.Race).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);
        var plan = new Dictionary<string, object>();
        foreach (var g in byRace)
        {
            if (only is not null && !g.Key.Contains(only, StringComparison.OrdinalIgnoreCase)) continue;
            var counts = g.GroupBy(x => x.Group).OrderByDescending(x => x.Count()).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase).ToList();
            var generic = counts[0].Key;                                             // 用得最多的 = 通用军事组
            if (counts[0].Count() == 1)
            {
                // 传奇派系全是 1:1（每人一个组）时，退回统计**全部派系**：出现最多的那个一定是通用组
                var all = fac.Rows
                    .Where(r => (fs >= 0 ? r[fs].ToTsv() : "").Replace("_pro_sc_", "_sc_", StringComparison.OrdinalIgnoreCase)
                                .Equals(g.Key, StringComparison.OrdinalIgnoreCase))
                    .Select(r => fm >= 0 ? r[fm].ToTsv() : "")
                    .Where(v => v.Length > 0)
                    .GroupBy(v => v)
                    .OrderByDescending(x => x.Count()).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (all.Count > 0) generic = all[0].Key;
            }
            var list = g.Select(x => new Row(x.Faction, x.Race, x.Group, x.Group.Equals(generic, StringComparison.OrdinalIgnoreCase))).ToList();
            foreach (var x in list) plan[x.Faction] = new { x.Group, x.Generic };
            Console.WriteLine($"\n【{g.Key}】通用军事组 = {generic}（{counts[0].Count()} 个传奇派系）" + (subName.TryGetValue(g.Key, out var cn) ? $"　{subName[g.Key]}" : ""));
            foreach (var x in list.OrderByDescending(x => x.Generic).ThenBy(x => x.Faction, StringComparer.OrdinalIgnoreCase))
                Console.WriteLine($"    {(x.Generic ? "通用" : "专属")}  {x.Group,-45} {x.Faction}");
        }
        return 0;
    }
}
