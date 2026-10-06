using System.IO;
using WarbandStudio.Pack;
using WarbandStudio.Packfile;

namespace WarbandStudio.Ui.Services;

/// <summary>
/// 卡图解析（照 CRPFM 的做法：读 DB 的图标字段，而不是拿兵种 key 去猜文件名）。
///
/// 候选链（按兵种 key）：main_units.land_unit → unit_variants_tables[unit=land_unit].unit_card
///   → land_units[land_unit].icon → land_unit 名 → main key；每个候选再试 _2/_3/_4 数字变体。
/// 来源链：打开的包 → 游戏 data/*.pack 索引 → 随附素材（assets/icons）。
/// 命中的图片抽一份到 %APPDATA%\WarbandStudio\iconcache，页面通过 icons.local 虚拟主机读。
/// </summary>
public sealed class IconResolver(AppSettings settings, Func<Schema> getSchema, Action<string>? log)
{
    public string CacheDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WarbandStudio", "iconcache");

    public string? AssetIconsDir { get; } = new[]
    {
        Path.Combine(AppContext.BaseDirectory, "assets", "icons", "ui", "units", "icons"),
        @"E:\AAA战锤工作区\tools\warband-tree-studio\assets\icons\ui\units\icons",
    }.FirstOrDefault(Directory.Exists);

    // 统计（画布/兵种库共用一份）
    public int FromPack, FromGame, FromBundled, Missing;
    public readonly List<string> NotInGame = [];

    private readonly Dictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);   // 兵种 → url
    /// <summary>
    /// 兵 → 候选**层**（每层是一串名字，按优先级从高到低）。查找时**层优先**：
    /// 先把第 1 层在所有包里找完（再找游戏），找不到才进第 2 层 ——
    /// 不然"某个包里 unit-key 的占位图"会盖掉"另一个包里 unit_variants 指的正确兵牌"（snek 实测）。
    /// </summary>
    private readonly Dictionary<string, List<List<string>>> _candidateTiers = new(StringComparer.OrdinalIgnoreCase);
    private PackArchive? _archive;
    private int _extraPrepared = -1;
    private bool _prepared;

    /// <summary>
    /// Url / Prepare 的互斥锁：画布（UI 线程）与兵种库（后台线程）会**同时**用同一个解析器抽图
    /// （用户快速切种族/派系时两个 LoadLibraryAsync 重叠，实测 02:03 并发抽同一张图把兵种库整批弄失败），
    /// 共享的 _cache / _candidateTiers / 懒索引不能并发写 —— 整段串行化。
    /// </summary>
    private readonly object _gate = new();

    private Dictionary<string, (string Pack, string Inner)>? _gameIcons;
    private Dictionary<string, (string Pack, string Inner)>? _gameInfopics;

    /// <summary>建候选表（main_units / unit_variants / land_units，原版打底 + 本包覆盖）。</summary>
    private PackArchive[] _extraPacks = [];

    /// <summary>
    /// 额外的包（"兵种 mod"这类）：它们的 main_units / land_units / unit_variants 也要并进候选链 ——
    /// 不然"战帮包引用了别的兵种包的兵"时，图标和中文名都查不到（画布上只剩 key）。
    /// </summary>
    public void SetExtraPacks(IEnumerable<PackArchive> packs) => _extraPacks = packs.Where(p => p is not null).ToArray();

    public void Prepare(PackArchive pack)
    {
        // 和 Url 抢同一批共享状态（_candidateTiers / _cache / _archive）——后台的兵种库刷新可能还在跑，
        // "切包时清空候选表"不能和"读候选表"并行（图标并发那一批一起修的）
        lock (_gate) PrepareCore(pack);
    }

    private void PrepareCore(PackArchive pack)
    {
        if (_prepared && ReferenceEquals(_archive, pack) && _extraPrepared == _extraPacks.Length) return;
        _archive = pack;
        _prepared = true;
        _extraPrepared = _extraPacks.Length;
        _candidateTiers.Clear();
        _cache.Clear();

        var schema = getSchema();
        var notes = new List<string>();
        try
        {
            Directory.CreateDirectory(CacheDir);
            var vanillaPack = Path.Combine(settings.GameDir ?? "", "data", "db.pack");
            var packHasGame = File.Exists(vanillaPack);

            DbTable? mainT = null, uv = null, landT = null;
            if (packHasGame)
            {
                using var vpk = PackArchive.Open(vanillaPack);
                mainT = TableFiles.ReadMerged(vpk, "main_units_tables", schema, notes);
                uv = UvOf(vpk, schema, notes);                       // 组合键读，见 UvKey 的说明
                landT = TableFiles.ReadMerged(vpk, "land_units_tables", schema, notes);
            }
            // 额外包（兵种 mod）先并进来，当前包最后（它的定义优先）
            foreach (var xp in _extraPacks)
            {
                mainT = TableFiles.Merge(mainT, TableFiles.ReadMerged(xp, "main_units_tables", schema, notes));
                uv = TableFiles.MergeComposite(uv, UvOf(xp, schema, notes), UvKey);
                landT = TableFiles.Merge(landT, TableFiles.ReadMerged(xp, "land_units_tables", schema, notes));
            }
            mainT = TableFiles.Merge(mainT, TableFiles.ReadMerged(pack, "main_units_tables", schema, notes));
            uv = TableFiles.MergeComposite(uv, UvOf(pack, schema, notes), UvKey);
            landT = TableFiles.Merge(landT, TableFiles.ReadMerged(pack, "land_units_tables", schema, notes));

            var cardByUnit = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (uv is not null)
            {
                var vu = Col(uv, "unit");
                var vc = Col(uv, "unit_card");
                if (vu >= 0 && vc >= 0)
                    foreach (var row in uv.Rows)
                    {
                        var u = row[vu].ToTsv(); var c = Stem(row[vc].ToTsv());
                        if (u.Length == 0 || c.Length == 0) continue;
                        if (!cardByUnit.TryGetValue(u, out var l)) cardByUnit[u] = l = [];
                        if (!l.Contains(c)) l.Add(c);
                    }
            }
            if (mainT is not null && landT is not null)
            {
                var mu = Col(mainT, "unit");
                var ml = Col(mainT, "land_unit");
                var lk = Col(landT, "land_unit");
                if (lk < 0) lk = TableFiles.FindKeyColumn(landT);
                var li = Col(landT, "icon", "icon_name", "card", "unit_card");
                if (mu >= 0 && ml >= 0 && lk >= 0)
                {
                    var iconByLand = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var row in landT.Rows)
                    {
                        var k = row[lk].ToTsv();
                        if (k.Length > 0) iconByLand[k] = li >= 0 ? row[li].ToTsv() : "";
                    }
                    foreach (var row in mainT.Rows)
                    {
                        var u = row[mu].ToTsv();
                        if (u.Length == 0) continue;
                        var lu = row[ml].ToTsv();
                        // **分层**：① unit_variants.unit_card（兵↔兵牌的对照表，最权威；作者把兵指到哪张就用哪张）
                        //          ② land_units.icon
                        //          ③ 才退到 land_unit / main_units 的 key（这两个只是"按名字猜"，可能是占位图）
                        // unit_variants_tables.unit 用的是 land_units 的 key（用户查证 + 原版语义）
                        var t1 = new List<string>();
                        if (lu.Length > 0 && cardByUnit.TryGetValue(lu, out var c1)) t1.AddRange(c1);
                        if (cardByUnit.TryGetValue(u, out var c2)) t1.AddRange(c2);
                        var t2 = new List<string>();
                        if (iconByLand.TryGetValue(lu, out var ic) && ic.Length > 0) t2.Add(Stem(ic));
                        var t3 = new List<string>();
                        if (lu.Length > 0) t3.Add(lu);
                        t3.Add(u);
                        _candidateTiers[u] = [t1, t2, t3];
                    }
                }
            }
            log?.Invoke($"卡图候选：{_candidateTiers.Count} 个兵（main_units + unit_variants.unit_card + land_units.icon）");
        }
        catch (Exception ex) { log?.Invoke("卡图候选跳过：" + ex.Message); }
    }

    /// <summary>
    /// `unit_variants_tables` 的**组合键**（faction + unit）。faction 是 OptionalStringU8，mod 的行基本都空着 ——
    /// 按"第一个键"合并会把空 faction 的行全吃成一行 ✗，兵牌映射就丢了，只能退回按 key 找图
    /// （snek 的 swivel gun 实测：显示的是 unit-key 那张占位图，而 variant 指的正确卡片被吃掉）。
    /// </summary>
    private static readonly string[] UvKey = ["faction", "unit"];

    private static DbTable? UvOf(PackArchive p, Schema schema, List<string>? notes) =>
        TableFiles.ReadComposite(p, "unit_variants_tables", UvKey, schema, notes);

    private static int Col(DbTable t, params string[] names)
    {
        foreach (var n in names)
        {
            var i = t.Columns.FindIndex(c => c.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) return i;
        }
        return -1;
    }

    private int _srcLogged;

    /// <summary>
    /// 记一行"这张图是从哪个候选名命中的"（只记**不是本兵 key** 的，上限 60 条）——
    /// 以后有人问"这个兵的图哪来的/为什么是这张"，看 app.log 就有答案。
    /// </summary>
    private void NoteSource(string unit, string cand, string? cacheFile = null)
    {
        try
        {
            if (_srcLogged >= 60) return;
            if (cand.Equals(unit, StringComparison.OrdinalIgnoreCase)) return;
            _srcLogged++;
            log?.Invoke($"图标来源：{unit} ← {cand}.png" +
                        (cacheFile is null ? "" : $"（缓存 {Path.GetFileName(cacheFile)}）"));
        }
        catch { }
    }

    /// <summary>
    /// 图标缓存文件：**名字里带上命中的候选名**（`&lt;兵&gt;@&lt;候选&gt;.png`）。
    /// 为什么：缓存文件以前就叫 `&lt;兵&gt;.png`，一旦哪个候选先写进去（比如 unit-key 那张占位图），
    /// 之后就算映射修好了、正确卡片找到了，`File.Exists` 也会拦住重抽 ✗ —— 用户看到的还是旧占位图。
    /// 带上候选名 = 换候选就换文件，天然失效。
    /// </summary>
    private string CacheFile(string unit, string cand)
    {
        var safe = new string(cand.Where(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.').ToArray());
        if (safe.Length == 0) safe = "x";
        return Path.Combine(CacheDir, unit + "@" + safe + ".png");
    }

    /// <summary>
    /// 写缓存文件（并发安全）：先写临时文件、再原子改名 —— 目标一出现内容就是完整的。
    /// 为什么：画布与兵种库会**同时**给同一个兵抽图（快速切种族/派系时重叠），
    /// 直接 File.WriteAllBytes 会撞"文件正被另一个进程使用"（实测：整个兵种库刷新失败）。
    /// 撞上并发（目标已被别的任务写好）当作成功；真写不进去才抛，由调用方决定怎么办。
    /// </summary>
    private static void WriteCache(string file, byte[] bytes)
    {
        if (File.Exists(file)) return;
        var tmp = file + ".tmp" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, file, overwrite: true);
        }
        catch (IOException) when (!File.Exists(file))
        {
            throw;                                  // 目标也没有 → 真失败
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    /// <summary>图标 url（带缓存文件时间戳：后加载兵种包补上了图，画布要能立刻刷新）。</summary>
    private string IconUrl(string unit)
    {
        var v = "";
        try
        {
            var f = Path.Combine(CacheDir, unit + ".png");
            if (File.Exists(f)) v = "?v=" + File.GetLastWriteTimeUtc(f).Ticks;
        }
        catch { }
        return "http://icons.local/" + unit + ".png" + v;
    }

    /// <summary>缓存文件对应的 url（文件已经写好）。</summary>
    private string IconUrlOfFile(string file)
    {
        var v = "";
        try { v = "?v=" + File.GetLastWriteTimeUtc(file).Ticks; } catch { }
        return "http://icons.local/" + Path.GetFileName(file) + v;
    }

    /// <summary>DB 图标字段取值里的"文件名主干"（可能带路径或 .png 后缀）。</summary>
    public static string Stem(string v)
    {
        var t = v.Trim().Replace('\\', '/');
        var i = t.LastIndexOf('/');
        if (i >= 0) t = t[(i + 1)..];
        if (t.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) t = t[..^4];
        return t;
    }

    /// <summary>拿到兵种的卡图 url（缓存目录里没有就现抽一张）；找不到返回 null。</summary>
    public string? Url(string unit, bool countMiss = true)
    {
        lock (_gate) return UrlCore(unit, countMiss);
    }

    private string? UrlCore(string unit, bool countMiss)
    {
        if (string.IsNullOrWhiteSpace(unit)) return null;
        if (_cache.TryGetValue(unit, out var cached)) return cached;
        Directory.CreateDirectory(CacheDir);

        var tiers = _candidateTiers.TryGetValue(unit, out var tl) ? tl : [[unit]];
        var packList = new[] { _archive }.Concat(_extraPacks).Where(p => p is not null).ToArray();
        var index = GameIconIndex();
        GameIconIndex();      // 确保立绘索引也建好

        // **层优先**：第 1 层（unit_variants.unit_card）在所有包 + 游戏里都找不到，才进第 2 层。
        // 每层先搜"打开/已加载的包"（本 mod / 兵种 mod 自己的图），再搜游戏包（units/icons，退 units/infopics）。
        foreach (var tier in tiers)
            foreach (var n in tier)
            {
                if (n.Length == 0) continue;
                for (var k = 0; k <= 4; k++)
                {
                    var cand = k == 0 ? n : n + "_" + k;
                    foreach (var p in packList)
                    {
                        var hit = p!.VisibleEntries.FirstOrDefault(e =>
                            e.Path.EndsWith("/" + cand + ".png", StringComparison.OrdinalIgnoreCase)
                            && e.Path.Contains("units/icons", StringComparison.OrdinalIgnoreCase));
                        if (hit is null) continue;
                        var f = CacheFile(unit, cand);            // 缓存名带候选 → 换候选自然失效
                        try
                        {
                            WriteCache(f, p.ReadDecoded(hit));
                            FromPack++;
                            NoteSource(unit, cand, f);
                            return _cache[unit] = IconUrlOfFile(f);
                        }
                        catch { }
                    }
                    if (Try(index, cand, unit, out var url)) return url;
                    if (Try(_gameInfopics!, cand, unit, out var url2)) return url2;
                }
            }

        // ③ 随附素材（旧工坊的 assets/icons）——也抽一份进缓存，页面只认 icons.local 这一个站点
        if (AssetIconsDir is not null && File.Exists(Path.Combine(AssetIconsDir, unit + ".png")))
        {
            var f = Path.Combine(CacheDir, unit + ".png");
            try { WriteCache(f, File.ReadAllBytes(Path.Combine(AssetIconsDir, unit + ".png"))); } catch { }
            if (File.Exists(f))                         // 抽不出来就当没有（别给出指向不存在文件的 url）
            {
                FromBundled++;
                var cands = _candidateTiers.TryGetValue(unit, out var cl)
                    ? string.Join(" / ", cl.SelectMany(x => x)) : unit;
                NotInGame.Add($"{unit}   ← 试过: {cands}");
                return _cache[unit] = IconUrl(unit);
            }
        }

        if (countMiss) Missing++;
        return null;

        bool Try(Dictionary<string, (string Pack, string Inner)> idx, string name, string unitKey, out string url)
        {
            url = "";
            if (!idx.TryGetValue(name, out var e)) return false;
            var f = CacheFile(unitKey, name);
            if (!File.Exists(f))
            {
                using var gp = PackArchive.Open(e.Pack);
                var ge = gp.Find(e.Inner);
                if (ge is null) return false;
                try { WriteCache(f, gp.ReadDecoded(ge)); }
                catch { return false; }     // 真写不进缓存（罕见）→ 当这个候选没命中，别拖垮整批兵种库
            }
            if (!File.Exists(f)) return false;
            FromGame++;
            NoteSource(unitKey, name, f);
            url = _cache[unitKey] = IconUrlOfFile(f);   // url 跟着**候选名**那个缓存文件走（和文件名一致）
            return true;
        }
    }

    private Dictionary<string, (string Pack, string Inner)>? _byNameIcons;   // png 裸名 → (包, 包内路径)（资源图标用）

    /// <summary>
    /// 按**图片文件名**找一张图（成本工坊的资源图标就是这种：表里 `optional_icon_path` =
    /// `icon_chivalry.png` / **`skaven_food_icon.png`** / `dlc25_xxx\icon_fealty_kazyk.png` —— 命名没有统一规律）。
    /// 所以这里按"**所有 png 的裸文件名**"建索引（以前只收 `icon_*`，`skaven_food_icon.png` 这种就永远找不到 —— 用户实测）。
    /// 索引懒建一次就缓存；<see cref="GameIconIndex"/> 那次全包扫描会顺带把它一起建了（不多花一遍扫描时间）。
    /// </summary>
    private Dictionary<string, (string Pack, string Inner)> GameIconNameIndex()
    {
        if (_byNameIcons is not null) return _byNameIcons;
        _byNameIcons = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        var game = settings.GameDir;
        if (string.IsNullOrWhiteSpace(game) || !Directory.Exists(Path.Combine(game, "data"))) return _byNameIcons;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var packPath in Directory.GetFiles(Path.Combine(game, "data"), "*.pack"))
        {
            try
            {
                using var p = PackArchive.Open(packPath);
                foreach (var e in p.VisibleEntries)
                {
                    if (!e.Path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) continue;
                    var n = Path.GetFileNameWithoutExtension(e.Path);
                    if (!_byNameIcons.ContainsKey(n)) _byNameIcons[n] = (packPath, e.Path);
                }
            }
            catch { }
        }
        sw.Stop();
        log?.Invoke($"按名找图索引：{_byNameIcons.Count} 张 png（{sw.ElapsedMilliseconds} ms）");
        return _byNameIcons;
    }

    /// <summary>
    /// 按**图片文件名**找一张图（成本工坊的资源图标就是这种：表里 `optional_icon_path` = icon_chivalry.png）：
    /// 先看打开的包，再按名索引扫游戏包。抽到缓存目录，返回页面用的 url。
    /// </summary>
    public string? UrlByPngName(string pngName, string cacheKey) =>
        IconFileFor(pngName, cacheKey) is null ? null : "http://icons.local/by_" + cacheKey + ".png";

    /// <summary>同上，但要的是**本地文件**（WPF 的 Image 不能吃 http，画布的 img 要 url）。</summary>
    public string? IconFileFor(string pngName, string cacheKey)
    {
        var name = Stem(pngName);
        if (name.Length == 0 || string.IsNullOrWhiteSpace(cacheKey)) return null;
        Directory.CreateDirectory(CacheDir);
        var file = Path.Combine(CacheDir, "by_" + cacheKey + ".png");
        var url = file;   // 下面统一 return file
        if (File.Exists(file)) return file;
        // ① 打开的包里（作者可能把图标也放进包了）
        var pack = _archive;
        if (pack is not null)
        {
            var hit = pack.VisibleEntries.FirstOrDefault(e =>
                e.Path.EndsWith("/" + name + ".png", StringComparison.OrdinalIgnoreCase));
            if (hit is not null)
            {
                try { WriteCache(file, pack.ReadDecoded(hit)); return file; } catch { }
            }
        }
        // ② 游戏包（按名索引：所有 png 的裸文件名都收，见 GameIconNameIndex 的说明）
        if (GameIconNameIndex().TryGetValue(name, out var e2))
        {
            try
            {
                using var gp = PackArchive.Open(e2.Pack);
                var ge = gp.Find(e2.Inner);
                if (ge is not null) { WriteCache(file, gp.ReadDecoded(ge)); return file; }
            }
            catch { }
        }
        return null;
    }

    /// <summary>
    /// 按**图片文件名**找一张图（成本工坊的资源图标就是这种：表里 `optional_icon_path` =
    /// `icon_chivalry.png` / **`skaven_food_icon.png`** / `dlc25_xxx\icon_fealty_kazyk.png` —— 命名没有统一规律）。
    /// 所以这里按"**所有 png 的裸文件名**"建索引（以前只收 `icon_*`，`skaven_food_icon.png` 这种就永远找不到 —— 用户实测）。
    /// <summary>扫游戏 data 目录各包的索引，建"图片名 → (包, 包内路径)"（只读索引，很快）。</summary>
    private Dictionary<string, (string Pack, string Inner)> GameIconIndex()
    {
        if (_gameIcons is not null) return _gameIcons;
        _gameIcons = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        _gameInfopics = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);

        var game = settings.GameDir;
        if (string.IsNullOrWhiteSpace(game) || !Directory.Exists(Path.Combine(game, "data")))
        {
            log?.Invoke("卡图索引：没设游戏目录，跳过");
            return _gameIcons;
        }

        var packs = Directory.GetFiles(Path.Combine(game, "data"), "*.pack").OrderBy(f => new FileInfo(f).Length).ToList();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _byNameIcons ??= new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);   // 顺带建"按名找图"索引（同一次扫描）
        foreach (var packPath in packs)
        {
            try
            {
                using var p = PackArchive.Open(packPath);
                foreach (var e in p.VisibleEntries)
                {
                    if (!e.Path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) continue;
                    var name = Path.GetFileNameWithoutExtension(e.Path);
                    // 按名找图（资源图标等，命名没规律）：所有 png 的裸名都收一份
                    if (!_byNameIcons.ContainsKey(name)) _byNameIcons[name] = (packPath, e.Path);
                    if (e.Path.Contains("units/icons", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!_gameIcons.ContainsKey(name)) _gameIcons[name] = (packPath, e.Path);
                    }
                    else if (e.Path.Contains("units/infopics", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!_gameInfopics!.ContainsKey(name)) _gameInfopics[name] = (packPath, e.Path);
                    }
                }
            }
            catch (Exception ex) { log?.Invoke($"卡图索引：跳过 {Path.GetFileName(packPath)}（{ex.GetType().Name}）"); }
        }
        sw.Stop();
        log?.Invoke($"卡图索引：扫 {packs.Count} 个包，卡图 {_gameIcons.Count} 张 + 立绘 {_gameInfopics!.Count} 张 + 按名 {_byNameIcons.Count} 张（{sw.ElapsedMilliseconds} ms）");
        return _gameIcons;
    }
}
