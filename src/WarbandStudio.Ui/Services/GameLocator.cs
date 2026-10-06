using System.IO;
using Microsoft.Win32;
using WarbandStudio.Core;

namespace WarbandStudio.Ui.Services;

/// <summary>
/// 游戏目录探测（Ui 侧）：读 Steam 注册表 + libraryfolders.vdf，拼候选，
/// 判定交给 <see cref="GameFinder"/>。
/// </summary>
public static class GameLocator
{
    public static bool LooksLikeGame(string? dir) => GameFinder.LooksLikeGame(dir);

    /// <summary>探测到的第一个可用目录；找不到返回 null。</summary>
    public static string? Detect() => Candidates().FirstOrDefault(GameFinder.LooksLikeGame);

    public static IEnumerable<string> Candidates()
    {
        var steam = SteamPath();
        var libraries = steam is null ? [] : ReadLibraries(steam);

        foreach (var c in GameFinder.CandidatesFor(steam, libraries)) yield return c;
        foreach (var c in GameFinder.DefaultDriveCandidates()) yield return c;
    }

    /// <summary>注册表里的 Steam 安装路径（可能是 "e:/steam" 这种写法）。</summary>
    public static string? SteamPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            return (key?.GetValue("SteamPath") as string)
                   ?? (key?.GetValue("InstallPath") as string);
        }
        catch { return null; }
    }

    /// <summary>读 &lt;Steam&gt;\steamapps\libraryfolders.vdf 里的各库根。</summary>
    public static List<string> ReadLibraries(string steamPath)
    {
        try
        {
            var vdf = Path.Combine(steamPath.Replace('/', '\\'), "steamapps", "libraryfolders.vdf");
            return File.Exists(vdf) ? GameFinder.ParseSteamLibraries(File.ReadAllText(vdf)) : [];
        }
        catch { return []; }
    }
}
