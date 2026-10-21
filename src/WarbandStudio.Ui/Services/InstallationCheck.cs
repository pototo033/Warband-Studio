using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace WarbandStudio.Ui.Services;

public sealed record InstallationManifest(int Format, string Version, Dictionary<string,string> Files,
    Dictionary<string,string>? Components = null);

/// <summary>发行文件一致性检查；清单只管理程序文件，不触碰用户数据。</summary>
public static class InstallationCheck
{
    public const string ManifestName = "schemas/installation-manifest.json";
    public static void Verify(string directory, string version, bool required = true)
    {
        var root = Path.GetFullPath(directory);
        var manifestPath = Path.Combine(root, ManifestName);
        if (!File.Exists(manifestPath) && !required) return;
        if (!File.Exists(manifestPath)) throw new InvalidDataException("缺少程序文件校验清单，请重新解压最新版程序包并完整覆盖安装目录。");
        var manifest = JsonSerializer.Deserialize<InstallationManifest>(File.ReadAllText(manifestPath))
            ?? throw new InvalidDataException("程序文件校验清单无法读取。");
        static Version? Normalize(string text) => Version.TryParse(text,out var v) ? new Version(v.Major,v.Minor,Math.Max(v.Build,0)) : null;
        if (manifest.Format != 1 || Normalize(manifest.Version) is not { } declared || declared != Normalize(version) || manifest.Files is null)
            throw new InvalidDataException("程序与文件清单版本不一致，请重新完整覆盖最新版程序包。");
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var problems = new List<string>();
        foreach (var (relative, hash) in manifest.Files)
        {
            var name = relative.Replace('\\','/');
            if (!IsManaged(name) || !expected.Add(name)) throw new InvalidDataException("程序文件清单含无效或重复路径。");
            var path = SafePath(root, name);
            if (!File.Exists(path)) problems.Add(name + "（缺失）");
            else if (!Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).Equals(hash, StringComparison.OrdinalIgnoreCase))
                problems.Add(name + "（版本不一致或损坏）");
        }
        foreach (var name in new[]{"WarbandStudio.exe","WarbandStudio.dll","WarbandStudio.Core.dll","WarbandStudio.Pack.dll","WarbandStudio.Packfile.dll","WarbandStudio.Rpfm.dll","WarbandStudio.Editors.dll","WarbandStudio.deps.json","WarbandStudio.runtimeconfig.json","Web/canvas.html","schemas/schema_wh3.ron","update_repo.txt"})
            if (!expected.Contains(name)) throw new InvalidDataException("程序文件清单不完整：" + name);
        // 先验证，失败时不移动任何文件，也不加载工程或恢复未保存的编辑。
        if (problems.Count > 0) throw new InvalidDataException("检测到程序文件缺失或混版：\n" + string.Join("\n",problems) + "\n请退出软件，重新解压最新版程序包并完整覆盖安装目录。");
        var extras = Directory.EnumerateFiles(root).Where(p => IsManaged(Path.GetFileName(p)))
            .Concat(Directory.Exists(Path.Combine(root,"Web"))
                ? Directory.EnumerateFiles(Path.Combine(root,"Web"),"*",SearchOption.TopDirectoryOnly).Where(p=>IsManaged("Web/"+Path.GetFileName(p))) : [])
            .Where(p=>!expected.Contains(Path.GetRelativePath(root,p).Replace('\\','/'))).ToArray();
        if (extras.Length == 0) return;
        var quarantine = SafePath(root,".file-quarantine/"+Guid.NewGuid().ToString("N"));
        foreach (var extra in extras)
        {
            var relative = Path.GetRelativePath(root,extra);
            var source = SafePath(root,relative);
            var target = SafePath(quarantine,relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(source,target);
            FileLog.Write("启动文件校验：隔离旧程序文件 " + relative + " → " + target);
        }
    }

    private static bool IsManaged(string name) =>
        !name.Contains('/') && (name.Equals("WarbandStudio.exe",StringComparison.OrdinalIgnoreCase)
            || name.Equals("update_repo.txt",StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("WarbandStudio",StringComparison.OrdinalIgnoreCase) &&
                (name.EndsWith(".dll",StringComparison.OrdinalIgnoreCase) || name.EndsWith(".deps.json",StringComparison.OrdinalIgnoreCase) || name.EndsWith(".runtimeconfig.json",StringComparison.OrdinalIgnoreCase)))
        || name.Equals("schemas/schema_wh3.ron",StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Web/",StringComparison.OrdinalIgnoreCase) && !name[4..].Contains('/') &&
            new[]{".html",".js",".css"}.Contains(Path.GetExtension(name),StringComparer.OrdinalIgnoreCase);

    internal static string SafePath(string root, string relative)
    {
        var boundary = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(boundary,relative));
        if (!path.StartsWith(boundary,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("程序文件路径越界。");
        for(var cursor=path; cursor is not null && cursor.Length>=boundary.Length-1; cursor=Path.GetDirectoryName(cursor))
            if ((File.Exists(cursor)||Directory.Exists(cursor)) && (File.GetAttributes(cursor)&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("程序文件路径包含链接："+relative);
        return path;
    }
}
