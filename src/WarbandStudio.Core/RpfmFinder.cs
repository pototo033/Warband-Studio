using System.IO;

namespace WarbandStudio.Core;

/// <summary>
/// 找"已有的 RPFM 引擎"（rpfm_server.exe）—— 纯逻辑部分。
/// 本工具定位是 RPFM 的衍生工具：**依赖用户已装的 RPFM**，不自带引擎。
/// 查找顺序（前面优先）：
///   1. 用户在本工具设置里指定的路径
///   2. 环境变量 RPFM_SERVER_PATH
///   3. RPFM 常见安装位置（安装版 / 绿色版）
///   4. 开发机兜底（工作区里那份，普通用户机器上不存在）
/// </summary>
public static class RpfmFinder
{
    /// <summary>构造候选路径列表（不判断存在性，方便测试/展示）。</summary>
    public static List<string> Candidates(
        string? settingsPath = null,
        string? envPath = null,
        IEnumerable<string>? installDirs = null,
        IEnumerable<string>? extraFallbacks = null)
    {
        var list = new List<string>();
        void Add(string? p)
        {
            if (!string.IsNullOrWhiteSpace(p)) list.Add(p!.Trim());
        }

        Add(settingsPath);
        Add(envPath);
        if (installDirs is not null)
            foreach (var d in installDirs) Add(Path.Combine(d, "rpfm_server.exe"));
        if (extraFallbacks is not null)
            foreach (var f in extraFallbacks) Add(f);
        return list;
    }

    /// <summary>候选里第一个真实存在的 rpfm_server.exe；没有返回 null。</summary>
    public static string? FindExisting(IEnumerable<string> candidates) =>
        candidates.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p));

    /// <summary>RPFM 安装/绿色版的常见目录（Ui 会再补注册表里读到的安装位置）。</summary>
    public static IEnumerable<string> CommonInstallDirs()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var progFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        yield return Path.Combine(local, "Programs", "RPFM");
        yield return Path.Combine(local, "RPFM");
        yield return Path.Combine(progFiles, "RPFM");
        yield return Path.Combine(progFilesX86, "RPFM");
        yield return Path.Combine(@"C:\", "RPFM");
        yield return Path.Combine(@"D:\", "RPFM");
        yield return Path.Combine(@"E:\", "RPFM");
    }
}
