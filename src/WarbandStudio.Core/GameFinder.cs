using System.IO;
using System.Text.RegularExpressions;

namespace WarbandStudio.Core;

/// <summary>
/// 找战锤3 游戏目录 —— 纯逻辑部分（读注册表那步留给调用方，Ui 里做）。
/// 放在 Core 是为了能被 Probe/单测直接验证。
/// </summary>
public static partial class GameFinder
{
    public const string GameFolderName = "Total War WARHAMMER III";

    /// <summary>"&lt;目录&gt;\data 里真有 .pack"才算游戏目录（光看名字会误判）。</summary>
    public static bool LooksLikeGame(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
        var data = Path.Combine(dir, "data");
        return Directory.Exists(data) && Directory.EnumerateFiles(data, "*.pack").Any();
    }

    /// <summary>
    /// 解析 Steam 的 libraryfolders.vdf，返回各个"库根"（如 <c>C:\SteamLibrary</c>）。
    /// 只认 <c>"path" "值"</c> 这种键值对 —— 注意键和值之间的空白，
    /// 直接按引号 Split 会切出空片段（踩过）。
    /// </summary>
    public static List<string> ParseSteamLibraries(string vdfText)
    {
        var list = new List<string>();
        foreach (Match m in PathLine().Matches(vdfText))
        {
            var p = Normalize(m.Groups[1].Value);
            if (p.Length > 0 && !list.Contains(p, StringComparer.OrdinalIgnoreCase))
                list.Add(p);
        }
        return list;
    }

    /// <summary>优先 Steam 安装目录，再按各库列出候选游戏路径。</summary>
    public static IEnumerable<string> CandidatesFor(string? steamPath, IEnumerable<string> libraryRoots)
    {
        if (!string.IsNullOrWhiteSpace(steamPath))
            yield return GameDirUnder(Normalize(steamPath));

        foreach (var lib in libraryRoots)
            yield return GameDirUnder(Normalize(lib));
    }

    /// <summary>没有任何 Steam 信息时的兜底：常见盘符 + 常见安装位置。</summary>
    public static IEnumerable<string> DefaultDriveCandidates()
    {
        string[] drives = ["C:", "D:", "E:", "F:", "G:", "H:"];
        string[] tails =
        [
            @"\SteamLibrary\steamapps\common\",
            @"\Program Files (x86)\Steam\steamapps\common\",
            @"\Steam\steamapps\common\",
            @"\Games\Steam\steamapps\common\",
            @"\Games\steamapps\common\",
        ];
        foreach (var d in drives)
            foreach (var t in tails)
                yield return d + t + GameFolderName;
    }

    private static string GameDirUnder(string libraryRoot) =>
        Path.Combine(libraryRoot, "steamapps", "common", GameFolderName);

    /// <summary>统一成反斜杠（注册表里的 SteamPath 可能是 <c>e:/steam</c> 这种）。</summary>
    private static string Normalize(string path) =>
        path.Trim().Replace('/', '\\').Replace(@"\\", @"\").TrimEnd('\\');

    [GeneratedRegex("\"path\"\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex PathLine();
}
