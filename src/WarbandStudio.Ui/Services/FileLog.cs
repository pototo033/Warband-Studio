using System.IO;

namespace WarbandStudio.Ui.Services;

/// <summary>
/// 极简文件日志：<c>%APPDATA%\WarbandStudio\app.log</c>。
/// exe 是窗口程序没有控制台，启动/后端出的问题只能靠它看（用户机器上也靠它排查）。
/// </summary>
public static class FileLog
{
    private static readonly Lock Gate = new();
    private static string? _path;

    public static string Path
    {
        get { EnsureInit(); return _path!; }
    }

    public static void Write(string line)
    {
        try
        {
            EnsureInit();
            lock (Gate)
                File.AppendAllText(_path!, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {line}{Environment.NewLine}");
        }
        catch { /* 日志写不了也不能影响主流程 */ }
    }

    public static void Write(string tag, Exception e) =>
        Write($"[{tag}] {e.GetType().Name}: {e.Message}\n{e.StackTrace}");

    private static void EnsureInit()
    {
        if (_path is not null) return;
        var dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WarbandStudio");
        Directory.CreateDirectory(dir);
        _path = System.IO.Path.Combine(dir, "app.log");
    }
}
