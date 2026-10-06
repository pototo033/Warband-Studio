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

    /// <summary>把素材抽到缓存，返回"名字 → http://skins.local/xxx.png"。</summary>
    public Dictionary<string, string> Prepare(IEnumerable<string> categories)
    {
        Directory.CreateDirectory(CacheDir);
        foreach (var (name, inner) in Fixed)
            if (Extract(inner, $"{name}.png") is { } u) _urls[name] = u;

        foreach (var cat in categories)
        {
            if (string.IsNullOrWhiteSpace(cat)) continue;
            var low = cat.ToLowerInvariant();
            var bgFile = $"background_images_{low}.png";
            if (Extract($"ui/skins/default/warband_upgrades/{bgFile}", bgFile) is { } u2) _urls["bg:" + cat] = u2;
            // 页签按钮图也给页面（换图对话框要显示"当前按钮"长什么样）
            var btnFile = $"button_upgrade_{low}.png";
            if (Extract($"ui/skins/default/warband_upgrades/{btnFile}", btnFile) is { } u3) _urls["btn:" + cat] = u3;
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
            return Url(cacheName);
        }
        catch (Exception ex) { log?.Invoke($"素材 {cacheName} 抽到缓存失败：{ex.Message}"); return null; }
    }

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
    }

    private string? Extract(string inner, string cacheName)
    {
        var target = Path.Combine(CacheDir, cacheName);
        if (File.Exists(target)) return Url(cacheName);

        var e = pack?.Find(inner) ?? pack?.VisibleEntries.FirstOrDefault(x =>
            string.Equals(x.Path.Replace('/', '\\'), inner.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase));
        if (e is not null && pack is not null)
        {
            try { File.WriteAllBytes(target, pack.ReadDecoded(e)); return Url(cacheName); }
            catch (Exception ex) { log?.Invoke($"皮肤素材 {inner} 抽出失败：{ex.Message}"); }
        }

        // **只从打开的包里取**。以前这里会去扫 data 下所有 pack 兜底，
        // 结果同名图（例如原版自己的 background_images_skv4.png）会把用户为这个页签选的图顶掉
        // —— 看起来就是"我没选过这张图"。原版素材以后由「UI 素材库」显式导入，不再自动兜底。
        return null;
    }
}
