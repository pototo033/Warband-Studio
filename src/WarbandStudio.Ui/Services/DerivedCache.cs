using System.IO;

namespace WarbandStudio.Ui.Services;

/// <summary>可重建缓存按程序版本隔离，旧缓存与恢复快照不做删除。</summary>
public static class DerivedCache
{
    public static string Root(string kind) => PathFor(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),Updater.CurrentVersion,kind);
    public static string PathFor(string localRoot,string version,string kind)
    {
        if (!Version.TryParse(version,out _) || kind is not ("iconcache" or "skincache" or "webview" or "diagnostics"))
            throw new ArgumentException("无效的缓存版本或类型。");
        return Path.Combine(localRoot,"WarbandStudio","derived",version,kind);
    }
}
