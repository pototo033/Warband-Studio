using System.IO;
using WarbandStudio.Pack;
using WarbandStudio.Packfile;

namespace WarbandStudio.Ui.Services;

/// <summary>
/// 本地化文本（.loc）：把 DB 里的 Loc Key 翻成游戏里显示的名字。
/// 来源优先级：**打开的包**（MOD 自带 text/db/*.loc 优先）→ 游戏 local_cn.pack（简中）→ local_zh.pack（繁中）→ local_en.pack。
/// 键的拼法：派系 factions_screen_name_&lt;派系Key&gt;、亚文化 cultures_subcultures_name_&lt;亚文化Key&gt;（见派系类表单详解）。
/// </summary>
public sealed class Localization(AppSettings settings, Action<string>? log)
{
    private Dictionary<string, string>? _map;

    /// <summary>键 → 显示名；查不到返回 null（调用方自己退回显示 Key）。</summary>
    public string? Text(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var map = Map();
        return map.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
    }

    private Dictionary<string, string> Map()
    {
        if (_map is not null) return _map;
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // 语言包从简中开始挑（用户的游戏界面是简中）
        var data = string.IsNullOrWhiteSpace(settings.GameDir) ? null : Path.Combine(settings.GameDir, "data");
        if (data is not null && Directory.Exists(data))
            foreach (var lang in new[] { "local_cn.pack", "local_zh.pack", "local_en.pack" })
            {
                var p = Path.Combine(data, lang);
                if (!File.Exists(p)) continue;
                var n = LoadFromPack(p, map);
                log?.Invoke($"本地化：{lang} 取到 {n} 条（累计 {map.Count}，{sw.ElapsedMilliseconds} ms）");
                if (map.Count > 0) break;                    // 有简中就不再看繁中/英文
            }

        sw.Stop();
        if (map.Count == 0) log?.Invoke("本地化：没找到语言包（游戏目录没设？），界面会显示 Key");
        _map = map;
        return _map;
    }

    /// <summary>把某个包里所有 text/db/*.loc（含 text/localisation__.loc）并进 map；返回新增条数。</summary>
    public int LoadFromPack(string packPath, Dictionary<string, string>? into = null)
    {
        if (_map is null) _map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var map = into ?? _map;
        var before = map.Count;
        try
        {
            using var pack = PackArchive.Open(packPath);
            foreach (var e in pack.VisibleEntries.Where(e =>
                         e.Path.EndsWith(".loc", StringComparison.OrdinalIgnoreCase) &&
                         e.Path.StartsWith("text/", StringComparison.OrdinalIgnoreCase)))
            {
                var t = LocFile.Read(pack.ReadDecoded(e));
                if (t is null) continue;
                foreach (var kv in t) map[kv.Key] = kv.Value;   // 后加载的覆盖
            }
        }
        catch (Exception ex) { log?.Invoke($"本地化：读 {Path.GetFileName(packPath)} 失败（{ex.GetType().Name}）"); }
        return map.Count - before;
    }

    /// <summary>打开的包自带的 loc（MOD 自己的名字）优先进来。</summary>
    public void MergePack(PackSession? pack)
    {
        if (pack?.Archive is null) return;
        var map = Map();       // 先把游戏语言包装好；MOD 的条目再覆盖上去（打开多个包时后开的赢）
        var n = 0;
        foreach (var e in pack.Archive.VisibleEntries.Where(e =>
                     e.Path.EndsWith(".loc", StringComparison.OrdinalIgnoreCase) &&
                     e.Path.StartsWith("text/", StringComparison.OrdinalIgnoreCase)))
        {
            var t = LocFile.Read(pack.Archive.ReadDecoded(e));
            if (t is null) continue;
            foreach (var kv in t) { map[kv.Key] = kv.Value; n++; }
        }
        if (n > 0) log?.Invoke($"本地化：包内 {pack.DisplayName} 覆盖 {n} 条");
    }
}
