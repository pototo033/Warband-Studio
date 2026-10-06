using System.IO;
using WarbandStudio.Pack;
using WarbandStudio.Packfile;

namespace WarbandStudio.Ui.Services;

/// <summary>
/// 画布要用的**游戏皮肤素材**（旧工坊那套背景 UI 用的就是这些）：
/// `ui/skins/default/warband_upgrades/background_images_&lt;页签&gt;.png`（页签背景）
/// 以及 UI 区那几个图（`unit_card_selected_hover.png` / `panel_title.png` / `icon_treasury.png`）。
/// 先在**打开的包**里找，再去游戏 data 各包里按路径找，抽到缓存目录，页面用 `skins.local` 取。
/// </summary>
public sealed class SkinAssets(PackArchive? pack, string? gameDir, Action<string>? log, string? packTag = null)
{
    /// <summary>
    /// 每个包一个子目录：**页签背景/按钮图按各自的包取**，不同 mod 的画布互不串（打开雪乃就用雪乃的图）。
    /// packTag 传包文件名（不含扩展名），为空则用公共目录。
    /// </summary>
    public string CacheDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WarbandStudio", "skincache",
        string.IsNullOrWhiteSpace(packTag) ? "common" : packTag!);

    /// <summary>站点里的子目录名（skins.local 映射到 skincache 根，各包在子目录里 —— url 必须带上它）。</summary>
    private readonly string _tag = string.IsNullOrWhiteSpace(packTag) ? "common" : packTag!;
    private string Url(string cacheName)
    {
        // 带版本号（文件最后写入时间）：换了图/重新抽过之后 URL 会变，WebView2 才会重新取图。
        // 不带的话同名 URL 会用浏览器缓存 —— 表现就是"换图应用了、画布/游戏里却还是旧图"。
        var v = "";
        try
        {
            var f = Path.Combine(CacheDir, cacheName);
            if (File.Exists(f)) v = "?v=" + File.GetLastWriteTimeUtc(f).Ticks;
        }
        catch { }
        return "http://skins.local/" + _tag + "/" + cacheName + v;
    }

    /// <summary>UI 区固定要的几个：名字 → 缓存文件名。</summary>
    private static readonly (string Name, string Inner)[] Fixed =
    [
        ("cardHover", "ui/skins/default/unit_card_selected_hover.png"),
        ("panelTitle", "ui/skins/default/panel_title.png"),
        ("treasury", "ui/skins/default/icon_treasury.png"),
        ("dilemmaFrame", "ui/skins/default/warband_upgrades/ability_frame_battle.png"),
    ];

    /// <summary>
    /// 现在挂在哪个包上。**保存写回原包会把旧 Archive 关掉重开一个新的** ——
    /// 外面（Backend.Skins）靠这个判断"缓存里这份 SkinAssets 是不是指向已经关掉的包"，
    /// 不然换图预览会报 "Cannot access a closed file"、当前图显示成空的（实测用户 02:50 那串报错）。
    /// </summary>
    public PackArchive? Pack => pack;

    private readonly Dictionary<string, string> _urls = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>每个缓存文件是"从哪来的"（`pack:&lt;包内路径&gt;` / `local:&lt;本地文件&gt;`）。
    /// 缓存目录是**跨会话**的：老版本只看"文件在不在"就复用 → 上一版抽的图会一直顶在上面
    /// （换了图/存过包之后画布还是旧图，看着就像"换图没生效"）。现在同一进程里第一次用**必重抽**，
    /// 之后按来源比对；`local:` 的（本会话换图/新建页签暂存的）优先，别被包里的旧图顶掉。</summary>
    private readonly Dictionary<string, string> _keys = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>把素材抽到缓存，返回"名字 → http://skins.local/xxx.png"。
    /// 页签的图**按各自 twui 里实际引用的文件名**给（改名后会带 _N、老遗留可能不是 key）。</summary>
    public Dictionary<string, string> Prepare(IEnumerable<(string Cat, string BgName, string BtnName)> pages)
    {
        Directory.CreateDirectory(CacheDir);
        foreach (var (name, inner) in Fixed)
            if (Extract(inner, $"{name}.png") is { } u) _urls[name] = u;

        foreach (var (cat, bgFile, btnFile) in pages)
        {
            if (string.IsNullOrWhiteSpace(cat)) continue;
            if (!string.IsNullOrWhiteSpace(bgFile) &&
                Extract($"ui/skins/default/warband_upgrades/{bgFile}", bgFile) is { } u2) _urls["bg:" + cat] = u2;
            // 页签按钮图也给页面（换图对话框要显示"当前按钮"长什么样）
            if (!string.IsNullOrWhiteSpace(btnFile) &&
                Extract($"ui/skins/default/warband_upgrades/{btnFile}", btnFile) is { } u3) _urls["btn:" + cat] = u3;
        }
        return _urls;
    }

    /// <summary>按包内路径抽一张图到缓存，返回页面用的 url（素材库缩略图）。</summary>
    public string? ExtractForPage(string inner, string cacheName) => Extract(inner, cacheName);

    /// <summary>把**本地素材文件**（素材库里的那份）拷进缓存，返回页面用的 url（画布/预览都用它）。</summary>
    public string? StageLocalFile(string localPath, string cacheName)
    {
        try
        {
            if (!File.Exists(localPath)) return null;
            Directory.CreateDirectory(CacheDir);
            var target = Path.Combine(CacheDir, cacheName);
            if (!File.Exists(target) || new FileInfo(target).Length != new FileInfo(localPath).Length)
                File.Copy(localPath, target, overwrite: true);
            _keys[cacheName] = LocalKey(localPath);      // 暂存的这张**优先**：Prepare 别拿包里的旧图顶掉它
            return Url(cacheName);
        }
        catch (Exception ex) { log?.Invoke($"素材 {cacheName} 抽到缓存失败：{ex.Message}"); return null; }
    }

    private static string LocalKey(string path) => "local:" + path;

    /// <summary>某页签的背景图 url（没有就 null）。</summary>
    public string? Background(string? category) =>
        category is not null && _urls.TryGetValue("bg:" + category, out var u) ? u : null;

    /// <summary>某页签的按钮图 url（没有就 null）。</summary>
    public string? Button(string? category) =>
        category is not null && _urls.TryGetValue("btn:" + category, out var u) ? u : null;

    /// <summary>清掉缓存里的某个文件（换图 / 重建页签时别让旧图顶上来）。</summary>
    public void DropCache(string cacheName)
    {
        try
        {
            var f = Path.Combine(CacheDir, cacheName);
            if (File.Exists(f)) File.Delete(f);
        }
        catch { /* 删不掉就用旧的，不致命 */ }
        _keys.Remove(cacheName);
    }

    /// <summary>撤掉本会话"暂存"的页签图（换图/新建页签预览）。撤销/放弃编辑后调一次：
    /// 不然画布还挂着已经撤掉的那张图，看着像"撤销没生效"。素材库缩略图（uilib_/art_）不动。</summary>
    public void DropStaged()
    {
        foreach (var name in _keys.Where(kv => kv.Value.StartsWith("local:", StringComparison.OrdinalIgnoreCase)
                                             && (kv.Key.StartsWith("background_images_", StringComparison.OrdinalIgnoreCase)
                                              || kv.Key.StartsWith("button_upgrade_", StringComparison.OrdinalIgnoreCase)))
                                  .Select(kv => kv.Key).ToList())
            DropCache(name);
    }

    private string? Extract(string inner, string cacheName)
    {
        var target = Path.Combine(CacheDir, cacheName);
        var key = "pack:" + inner;
        if (_keys.TryGetValue(cacheName, out var have) && File.Exists(target))
        {
            // 本会话换图/新建页签暂存的那张 → 画布要看它（包里的还没写进去）
            if (have.StartsWith("local:", StringComparison.OrdinalIgnoreCase)) return Url(cacheName);
            if (have.Equals(key, StringComparison.OrdinalIgnoreCase)) return Url(cacheName);
        }

        var e = pack?.Find(inner) ?? pack?.VisibleEntries.FirstOrDefault(x =>
            string.Equals(x.Path.Replace('/', '\\'), inner.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase));
        if (e is not null && pack is not null)
        {
            try
            {
                File.WriteAllBytes(target, pack.ReadDecoded(e));
                _keys[cacheName] = key;
                return Url(cacheName);
            }
            catch (Exception ex) { log?.Invoke($"皮肤素材 {inner} 抽出失败：{ex.Message}"); }
        }
        // 包里没有这个条目：缓存里要是有（新建页签拿母版图暂存的那份、或上次留下的）就用它，
        // 别让"新页签的画布预览"变成空背景。
        if (File.Exists(target)) return Url(cacheName);

        // **只从打开的包里取**。以前这里会去扫 data 下所有 pack 兜底，
        // 结果同名图（例如原版自己的 background_images_skv4.png）会把用户为这个页签选的图顶掉
        // —— 看起来就是"我没选过这张图"。原版素材以后由「UI 素材库」显式导入，不再自动兜底。
        return null;
    }
}
