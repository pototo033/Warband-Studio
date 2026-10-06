using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;

namespace WarbandStudio.Ui.Services;

/// <summary>
/// **启动时检查更新**（用户口径：只检查、提示，不自动替换 —— 点"是"打开 Release 下载页）。
/// 仓库名从 exe 旁边的 `update_repo.txt` 读（build.ps1 会把根目录 `GITHUB_REPO.txt` 带进 release）；
/// 没有这个文件 / 内容是空的 → 直接跳过（别人自己编译的版本不会乱查）。
/// </summary>
public static class Updater
{
    /// <summary>本程序版本（build.ps1 用 -p:Version= 编进来的）。</summary>
    public static string CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version is { } v
            ? $"{v.Major}.{v.Minor}" + (v.Build > 0 ? $".{v.Build}" : "")
            : "0.0";

    /// <summary>仓库名（owner/repo）；读不到返回空。</summary>
    public static string Repo()
    {
        try
        {
            var f = Path.Combine(AppContext.BaseDirectory, "update_repo.txt");
            if (!File.Exists(f)) return "";
            foreach (var line in File.ReadAllLines(f))
            {
                var t = line.Trim();
                if (t.Length > 0 && !t.StartsWith("#")) return t;
            }
        }
        catch { }
        return "";
    }

    /// <summary>
    /// 查最新 Release；有更新就弹一句"是否打开下载页"（选"否"记下这个版本，不再提示）。
    /// 全程异步、失败静默（离线/被墙都不该影响工具启动）。
    /// </summary>
    public static async Task CheckAsync(AppSettings settings, Action<string>? status = null)
    {
        var repo = Repo();
        if (repo.Length == 0) { status?.Invoke("未配置更新仓库（update_repo.txt）"); return; }
        try
        {
            status?.Invoke("正在检查更新…");
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("WarbandStudio");
            var json = await http.GetStringAsync($"https://api.github.com/repos/{repo}/releases/latest");
            using var doc = JsonDocument.Parse(json);
            var tag = (doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() : null)?.TrimStart('v', 'V') ?? "";
            var page = doc.RootElement.TryGetProperty("html_url", out var h) ? h.GetString() : null;
            if (tag.Length == 0 || page is null) { status?.Invoke("检查更新：没读到版本号"); return; }
            if (!IsNewer(tag, CurrentVersion)) { status?.Invoke($"已是最新版本（v{CurrentVersion}）"); return; }
            if (tag.Equals(settings.SkipUpdate, StringComparison.OrdinalIgnoreCase))
            { status?.Invoke($"有新版本 v{tag}（已选择跳过）"); return; }

            status?.Invoke($"发现新版本 v{tag}（当前 v{CurrentVersion}）");
            var r = MessageBox.Show(
                $"发现新版本 v{tag}（当前 v{CurrentVersion}）。\n\n要打开下载页吗？\n\n选「否」= 这个版本不再提示。",
                "WarbandStudio 更新", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (r == MessageBoxResult.Yes) Process.Start(new ProcessStartInfo(page) { UseShellExecute = true });
            else { settings.SkipUpdate = tag; settings.Save(); }
        }
        catch (Exception ex) { status?.Invoke("检查更新失败（不影响使用）：" + ex.Message); }
    }

    /// <summary>tag 比当前新？（按 . 分段比数字，段数不同也能比）</summary>
    private static bool IsNewer(string tag, string cur)
    {
        var a = tag.Split('.'); var b = cur.Split('.');
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length && int.TryParse(a[i], out var n1) ? n1 : 0;
            var y = i < b.Length && int.TryParse(b[i], out var n2) ? n2 : 0;
            if (x != y) return x > y;
        }
        return false;
    }
}
