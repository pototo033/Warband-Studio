using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;

namespace WarbandStudio.Ui.Services;

/// <summary>启动检查尊重跳过；手动检查重新提示。更新包先验证，退出后由独立助手安装。</summary>
public static class Updater
{
    private static readonly SemaphoreSlim Job = new(1, 1);
    private static int ManualRequested;
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

    public static async Task CheckAsync(AppSettings settings, Action<string>? status = null, bool manual = false,
        Func<Task<bool>>? prepareRestart = null, Action? shutdown = null)
    {
        if (!await Job.WaitAsync(0))
        {
            if (manual) Interlocked.Exchange(ref ManualRequested, 1);
            status?.Invoke("已有更新任务正在进行…"); return;
        }
        Interlocked.Exchange(ref ManualRequested, manual ? 1 : 0);
        bool IsManual() => manual || Volatile.Read(ref ManualRequested) != 0;
        string? cache = null; var handedOff = false;
        UpdateProgressWindow? progressWindow = null;
        using var cancel = new CancellationTokenSource();
        try
        {
            var repo = Repo();
            if (repo.Length == 0) { status?.Invoke("未配置更新仓库（update_repo.txt）"); return; }
            status?.Invoke("正在检查更新…");
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("WarbandStudio");
            var json = await http.GetStringAsync($"https://api.github.com/repos/{repo}/releases/latest");
            var release = UpdatePackage.ParseRelease(json, repo);
            if (!UpdatePackage.ShouldOffer(release.Version, CurrentVersion, "", true))
            {
                var text = $"已是最新版本（v{CurrentVersion}）"; status?.Invoke(text);
                if (IsManual()) MessageBox.Show(text, "检查更新", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (!UpdatePackage.ShouldOffer(release.Version, CurrentVersion, settings.SkipUpdate, IsManual()))
            { status?.Invoke($"有新版本 v{release.Version}（自动提醒已跳过，可手动检查）"); return; }
            status?.Invoke($"发现新版本 v{release.Version}（当前 v{CurrentVersion}）");
            var r = MessageBox.Show(
                $"发现新版本 v{release.Version}（当前 v{CurrentVersion}）。\n\n「是」：下载安装，完成后重启应用。\n「否」：跳过此版本的自动提醒，仍可手动检查。\n「取消」：稍后再说。\n\n未保存编辑会在安装前询问处理。",
                "WarbandStudio 更新", MessageBoxButton.YesNoCancel, MessageBoxImage.Information);
            if (r == MessageBoxResult.No) { settings.SkipUpdate = release.Version; settings.Save(); status?.Invoke("已跳过此版本的自动提醒，手动检查仍会提示"); return; }
            if (r != MessageBoxResult.Yes) { status?.Invoke("已暂缓更新"); return; }
            if (prepareRestart is null || shutdown is null) throw new InvalidOperationException("更新退出处理尚未连接。");
            cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WarbandStudio", "updates", Guid.NewGuid().ToString("N"));
            progressWindow = new UpdateProgressWindow(release.Version, cancel);
            if (Application.Current?.MainWindow is { IsVisible: true } owner) progressWindow.Owner = owner;
            progressWindow.Show();
            using var download = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            download.DefaultRequestHeaders.UserAgent.ParseAdd("WarbandStudio");
            var progress = new Progress<(long Received, long Total)>(p =>
            {
                if (progressWindow is null) return;
                progressWindow.Report(p.Received, p.Total);
                status?.Invoke($"下载更新：{p.Received / 1048576d:F1} / {p.Total / 1048576d:F1} MiB");
            });
            var plan = await UpdatePackage.PrepareAsync(release, cache, AppContext.BaseDirectory, download, progress, cancel.Token);
            cancel.Token.ThrowIfCancellationRequested();
            progressWindow.Finish(); progressWindow = null;
            status?.Invoke("更新包已验证，准备安装…");
            if (!await prepareRestart()) { status?.Invoke("已取消安装，当前应用保持运行"); return; }
            var planPath = UpdatePackage.WriteInstaller(plan, cache);
            var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            var start = new ProcessStartInfo(powershell) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = cache };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(cache, "apply-update.ps1"), "-PlanPath", planPath }) start.ArgumentList.Add(arg);
            using var helper = Process.Start(start) ?? throw new IOException("无法启动更新助手。");
            handedOff = true;
            settings.SkipUpdate = ""; settings.Save();
            status?.Invoke("正在退出并安装更新，随后自动重启…");
            FileLog.Write($"更新交接：v{release.Version}；helper={helper.Id}；cache={cache}");
            shutdown();
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { status?.Invoke("已取消更新，当前应用保持运行"); }
        catch (Exception ex)
        {
            var message = "更新未完成，当前应用保持运行：" + ex.Message;
            status?.Invoke(message); FileLog.Write("更新失败", ex);
            if (IsManual()) MessageBox.Show(message, "WarbandStudio 更新", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            progressWindow?.Finish();
            if (!handedOff && cache is not null && Directory.Exists(cache))
                try { _ = UpdatePackage.Under(Path.GetDirectoryName(cache)!, Path.GetFileName(cache)); Directory.Delete(cache, true); }
                catch (Exception ex) { FileLog.Write("未完成的更新缓存清理失败", ex); }
            Job.Release();
        }
    }

    public static void ReportInstallation(Action<string> status)
    {
        var receipt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WarbandStudio", "updates", "last-result.json");
        try
        {
            if (!File.Exists(receipt)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(receipt));
            var root = doc.RootElement; var ok = root.GetProperty("Success").GetBoolean();
            var message = ok ? $"已安装更新 v{root.GetProperty("Version").GetString()}" : "更新失败，已尝试恢复旧版：" + root.GetProperty("Message").GetString() + "\n日志：" + root.GetProperty("LogPath").GetString();
            FileLog.Write(message); status(message); File.Delete(receipt);
            if (!ok) MessageBox.Show(message, "WarbandStudio 更新", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex) { FileLog.Write("更新结果读取失败", ex); }
    }
}
