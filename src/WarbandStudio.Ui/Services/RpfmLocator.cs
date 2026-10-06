using System.IO;
using Microsoft.Win32;
using WarbandStudio.Core;

namespace WarbandStudio.Ui.Services;

/// <summary>
/// "找已有 RPFM 引擎"的 Ui 侧：把注册表里的安装位置 + 常见目录喂给
/// <see cref="RpfmFinder"/>，并负责记住/回填用户手动指定的路径。
/// </summary>
public static class RpfmLocator
{
    /// <summary>上一轮探测到的路径（写回设置用）。</summary>
    public static string? LastFound { get; private set; }

    /// <summary>按顺序找 rpfm_server.exe；找不到返回 null。</summary>
    public static string? Find(string? settingsPath, AppSettings settings)
    {
        var env = Environment.GetEnvironmentVariable("RPFM_SERVER_PATH");

        var installDirs = new List<string>();
        installDirs.AddRange(RegistryInstallDirs());
        installDirs.AddRange(RpfmFinder.CommonInstallDirs());

        // 开发机兜底：工作区里那份 5.0.6（普通用户机器上没有，找不到会自动跳过）
        string[] devFallbacks =
        [
            @"E:\AAA战锤工作区\rpfm\rpfm_server.exe",
            Path.Combine(AppContext.BaseDirectory, "rpfm", "rpfm_server.exe"),  // 自带引擎的构建
        ];

        var candidates = RpfmFinder.Candidates(settingsPath, env, installDirs, devFallbacks);
        var found = RpfmFinder.FindExisting(candidates);

        LastFound = found;
        if (found is not null && settingsPath is not null && settings.RpfmServerPath != found)
        {
            settings.RpfmServerPath = found;   // 记下来，下次不用再扫
            settings.Save();
        }
        return found;
    }

    /// <summary>从注册表里挖 RPFM 的安装目录（安装版才写；绿色版挖不到）。</summary>
    private static IEnumerable<string> RegistryInstallDirs()
    {
        string[] roots =
        [
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        ];
        foreach (var root in roots)
        {
            RegistryKey? baseKey = null;
            try { baseKey = Registry.LocalMachine.OpenSubKey(root); } catch { }
            if (baseKey is null) continue;

            using (baseKey)
            {
                foreach (var sub in baseKey.GetSubKeyNames())
                {
                    RegistryKey? k = null;
                    try { k = baseKey.OpenSubKey(sub); } catch { }
                    if (k is null) continue;
                    using (k)
                    {
                        var name = k.GetValue("DisplayName") as string;
                        if (name is null || !name.Contains("RPFM", StringComparison.OrdinalIgnoreCase)) continue;
                        if (k.GetValue("InstallLocation") is string loc && loc.Length > 0)
                            yield return loc.TrimEnd('\\');
                    }
                }
            }
        }
    }
}
