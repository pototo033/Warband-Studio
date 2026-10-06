using System.IO;
using System.Text.Json;

namespace WarbandStudio.Ui.Services;

/// <summary>
/// 本工具自己的设置（跟 rpfm 的 settings.json 是两回事）。
/// 存 <c>%APPDATA%\WarbandStudio\settings.json</c>。
/// </summary>
public sealed class AppSettings
{
    /// <summary>战锤3 游戏目录（含 data 那一层）。</summary>
    public string GameDir { get; set; } = "";

    /// <summary>更新检查里"不再提示"的那个版本号（空 = 正常提示）。</summary>
    public string SkipUpdate { get; set; } = "";

    /// <summary>使用教程是否已经放过一遍（第一次打开自动放；工具栏/全局选项里可以再看）。</summary>
    public bool TourDone { get; set; }

    /// <summary>备份文件夹（保存写回原包时，原文件备份到这里；空 = 默认 %APPDATA%\WarbandStudioackups）。</summary>
    public string BackupDir { get; set; } = "";

    /// <summary>上次打开的 .pack（下次启动可以问一句要不要接着开）。</summary>
    public string LastPack { get; set; } = "";

    /// <summary>
    /// 用户的 RPFM 引擎路径（rpfm_server.exe）。本工具是 RPFM 的衍生工具，
    /// 引擎靠用户已装的 RPFM；这里记住探测/手动指定的那一份。
    /// </summary>
    public string RpfmServerPath { get; set; } = "";

    /// <summary>自动保存（RPFM 手感）：编辑停下约 2 秒就写回原包。</summary>
    public bool AutoSave { get; set; }

    /// <summary>首跑向导走完了没（跳过也算走完）。</summary>
    public bool FirstRunDone { get; set; }

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WarbandStudio");

    private static string FilePath => Path.Combine(Dir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { /* 设置文件坏了就当没有，别拦启动 */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 存不下就算了，不致命 */ }
    }
}
