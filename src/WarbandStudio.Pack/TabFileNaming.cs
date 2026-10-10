using WarbandStudio.Packfile;

namespace WarbandStudio.Pack;

/// <summary>
/// 页签内容表的新行落到哪个文件（用户定的规则，v1.5.9 起）：
///   · **五张"按页签组织"的表**（组 / 兵↔组 / 坐标 / 连线 / 路线）→ `&lt;项目key&gt;_Upgrade_&lt;页签key&gt;`
///     —— 和 MOD 作者那边的文件风格对齐（`Yukino_Upgrade_Skv`、`Yukino_Upgrade_SKV_B` 这一族），
///     一个页签一套文件，交换/合并时一眼知道哪几份属于谁；
///   · **页签本体 / 授权 / 成本**保持 `zzzz_studio_edits` 单文件：成本行跨页签共用（不属于某个页签），
///     授权是"兵→军事组"（跟页签无关），页签本体那边作者自己也是单文件（右侧ui种族表）。
///
/// 陷阱：这张表和作者已有的按种族文件**会撞名字**——页签 key 是 SKV/VMP/EMP…，作者的文件叫
/// `Yukino_Upgrade_Skv`/`Vmp`/`Emp`（只差大小写）。玩家侧的 MOD 里作者已经把"第二个 skv 文件"
/// 命名为 `SKV_B` 了，可见这种事真实存在。所以**落表前先做忽略大小写的同名查找**：
/// 已经有就并进那一份、沿用它的原始拼写，绝不造出大小写孪生文件（游戏按文件名加载，孪生 = 谁赢说不准）。
/// </summary>
public static class TabFileNaming
{
    public const string Groups = "unit_upgrade_groups_tables";
    public const string Junc = "unit_to_unit_group_junctions_tables";
    public const string Infos = "unit_upgrade_group_ui_infos_tables";
    public const string Links = "unit_upgrade_group_ui_links_tables";
    public const string Routes = "unit_upgrade_to_unit_groups_tables";
    public const string Cats = "unit_upgrade_group_ui_categories_tables";

    /// <summary>定不了页签的行落到哪个文件（极少：组既没有坐标行、名字也认不出页签）。</summary>
    public const string FallbackTab = "其他";

    private static readonly HashSet<string> Scoped = new(StringComparer.OrdinalIgnoreCase)
    {
        Groups, Junc, Infos, Links, Routes,
    };

    /// <summary>这张表的内容是不是"属于某个页签"（决定新行分页签落，还是写 zzzz_studio_edits）。</summary>
    public static bool IsScoped(string table) => Scoped.Contains(table);

    /// <summary>取文件名里能用的片段（只留字母数字与 _-；空 → fallback）。</summary>
    public static string Sanitize(string? s, string fallback)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var ch in (s ?? "").Trim())
            if (char.IsLetterOrDigit(ch) || ch is '_' or '-') sb.Append(ch);
        return sb.Length == 0 ? fallback : sb.ToString();
    }

    /// <summary>`&lt;项目key&gt;_Upgrade_&lt;页签key&gt;`（项目key 空 → studio）。</summary>
    public static string FileNameOf(string? projectKey, string? tab) =>
        $"{Sanitize(projectKey, "studio")}_Upgrade_{Sanitize(tab, FallbackTab)}";

    /// <summary>`db/&lt;表&gt;/&lt;项目key&gt;_Upgrade_&lt;页签key&gt;`。</summary>
    public static string PathOf(string table, string? projectKey, string? tab) =>
        $"db/{table}/{FileNameOf(projectKey, tab)}";
}

/// <summary>
/// "这一行属于哪个页签"的解析器（Amender 落表与 Backend 红标共用）：
///   ① 本会话编辑（InfoEdits 里的 category，最权威）；
///   ② 包里的 infos 行（组 → category；多文件时后读到的为准 = 后加载的赢）；
///   ③ 名字兜底：`&lt;项目key&gt;_&lt;页签key&gt;_...` 且那个 key 真的是已存在的页签（工具新建的组就是这个格式，
///      比如 `Yukino_VMP1_death`；VMP1 这种还没写坐标行的组靠这条认出来）。
/// 都认不出来 → null（调用方落"其他"文件）。
/// </summary>
public sealed class TabResolver
{
    private readonly PackArchive _pack;
    private readonly WarbandEdits _e;
    private readonly Schema _schema;
    private readonly Dictionary<string, byte[]>? _repl;
    private Dictionary<string, string>? _infoCats;
    private HashSet<string>? _cats;

    public TabResolver(PackArchive pack, WarbandEdits e, Schema schema, Dictionary<string, byte[]>? repl = null)
    {
        _pack = pack; _e = e; _schema = schema; _repl = repl;
    }

    /// <summary>组 → 页签 key（认不出 = null）。</summary>
    public string? TabOf(string? group)
    {
        var g = (group ?? "").Trim();
        if (g.Length == 0) return null;
        if (_e.InfoEdits.TryGetValue(g, out var ed) && !string.IsNullOrWhiteSpace(ed.Category))
            return ed.Category.Trim();
        if (InfoCats().TryGetValue(g, out var c) && c.Length > 0) return c;
        var parts = g.Split('_');
        var start = parts.Length > 1
                    && parts[0].Equals(TabFileNaming.Sanitize(_e.ProjectKey, "studio"), StringComparison.OrdinalIgnoreCase)
            ? 1 : 0;
        if (start < parts.Length - 1)
        {
            var hit = Cats().FirstOrDefault(x => x.Equals(parts[start], StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
        }
        return null;
    }

    /// <summary>组这一行的目标文件（含"忽略大小写同名即沿用原件拼写"的归并）。</summary>
    public string PathFor(string table, string? group) =>
        Canonical(table, TabFileNaming.PathOf(table, _e.ProjectKey, TabOf(group)));

    /// <summary>按页签取目标文件（不知道组、只认页签时用，比如坐标行分组）。</summary>
    public string PathForTab(string table, string? tab) =>
        Canonical(table, TabFileNaming.PathOf(table, _e.ProjectKey, tab));

    /// <summary>表内已存在的同名（忽略大小写）条目 → 用它的原始拼写；没有就用候选名。</summary>
    public string Canonical(string table, string want)
    {
        foreach (var p in PathsOf(table))
            if (p.Replace('\\', '/').Equals(want, StringComparison.OrdinalIgnoreCase)) return p.Replace('\\', '/');
        return want;
    }

    private IEnumerable<string> PathsOf(string table)
    {
        foreach (var f in TableFiles.EntriesFor(_pack, table)) yield return f.Path;
        if (_repl is not null)
        {
            var prefix = $"db/{table}/";
            foreach (var p in _repl.Keys)
                if (p.Replace('\\', '/').StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) yield return p;
        }
    }

    private byte[]? Bytes(string path)
    {
        if (_repl is not null && _repl.TryGetValue(path, out var buf)) return buf;
        var e = _pack.Find(path) ?? _pack.VisibleEntries.FirstOrDefault(x =>
            x.Path.Replace('\\', '/').Equals(path.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
        return e is null ? null : _pack.ReadDecoded(e);
    }

    private Dictionary<string, string> InfoCats()
    {
        if (_infoCats is not null) return _infoCats;
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in PathsOf(TabFileNaming.Infos))
        {
            var b = Bytes(f);
            if (b is null) continue;
            try
            {
                var t = DbTable.Decode(b, TabFileNaming.Infos, _schema);
                var kc = t.Columns.FindIndex(c => c.Name.Equals("unit_upgrade_group", StringComparison.OrdinalIgnoreCase));
                var cc = t.Columns.FindIndex(c => c.Name.Equals("category", StringComparison.OrdinalIgnoreCase));
                if (kc < 0 || cc < 0) continue;
                foreach (var r in t.Rows)
                {
                    var g = r[kc].ToTsv();
                    if (g.Length > 0) map[g] = r[cc].ToTsv();     // 后读到的文件为准
                }
            }
            catch { }
        }
        return _infoCats = map;
    }

    private HashSet<string> Cats()
    {
        if (_cats is not null) return _cats;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in PathsOf(TabFileNaming.Cats))
        {
            var b = Bytes(f);
            if (b is null) continue;
            try
            {
                var t = DbTable.Decode(b, TabFileNaming.Cats, _schema);
                var cc = t.Columns.FindIndex(c => c.Name.Equals("category", StringComparison.OrdinalIgnoreCase));
                if (cc < 0) continue;
                foreach (var r in t.Rows)
                {
                    var v = r[cc].ToTsv();
                    if (v.Length > 0) set.Add(v);
                }
            }
            catch { }
        }
        foreach (var (key, _, _, _) in _e.NewTabs)
            if (key.Length > 0) set.Add(key);
        return _cats = set;
    }
}
