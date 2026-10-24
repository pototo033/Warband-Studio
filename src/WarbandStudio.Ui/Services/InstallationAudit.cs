using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WarbandStudio.Ui.Services;

/// <summary>升级后首次检查完整发布物，涵盖 app 包不会覆盖的组件；只读安装文件并写独立报告。</summary>
public static class InstallationAudit
{
    public sealed record Report(string Version,string Installation,int Checked,string[] Problems,
        string[] UnlistedCode,Dictionary<string,string> EffectiveSources,string[] PreservedLocations);
    public static string? Run(string installation,string version,string stateRoot,Dictionary<string,string>? sources=null)
    {
        var manifestPath=Path.Combine(installation,InstallationCheck.ManifestName);
        if(!File.Exists(manifestPath)) return null; // Debug 输出没有发行清单。
        var manifest=JsonSerializer.Deserialize<InstallationManifest>(File.ReadAllText(manifestPath))!;
        if(manifest.Components is null) return null; // 旧发行清单；新构建始终包含 Components。
        var root=Path.GetFullPath(installation);
        var identity=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.TrimEnd('\\','/').ToUpperInvariant())));
        var reportPath=InstallationCheck.SafePath(stateRoot,identity+".json");
        var marker=reportPath+".ready";
        sources ??= new(StringComparer.OrdinalIgnoreCase);
        var token=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(manifestPath)))+"|"+version+"|"+
            string.Join("|",sources.Where(p=>p.Key is "RPFM" or "schema").OrderBy(p=>p.Key).Select(p=>p.Key+"="+p.Value));
        // 核心文件仍由 InstallationCheck 每次启动检查；完整检查按清单及实际来源记账。
        if(File.Exists(marker)&&File.Exists(reportPath)&&File.ReadAllText(marker)==token)
        {
            FileLog.Write("升级全检：本版本已完成；报告："+reportPath);
            return reportPath;
        }
        var problems=ComponentProblems(root,manifest).ToList();
        var expected=new HashSet<string>(manifest.Files.Keys.Concat(manifest.Components.Keys).Select(p=>p.Replace('\\','/')),StringComparer.OrdinalIgnoreCase);
        var count=manifest.Components.Count;
        foreach(var (key,path) in sources)
        {
            var relative=key=="RPFM" ? "rpfm/rpfm_cli.exe" : key=="schema" ? "schemas/schema_wh3.ron" : null;
            if(relative is null) continue;
            var hash=manifest.Components.GetValueOrDefault(relative)??manifest.Files.GetValueOrDefault(relative);
            if(hash is not null && (!File.Exists(path)||!Hash(path).Equals(hash,StringComparison.OrdinalIgnoreCase)))
                problems.Add(key+" 实际加载来源与发行版不一致："+path);
        }
        var extra=Directory.EnumerateFiles(root).Where(p=>Path.GetExtension(p) is ".dll" or ".exe")
            .Concat(EnumerateComponentFiles(root,"rpfm")).Concat(EnumerateComponentFiles(root,"bundled"))
            .Select(p=>Path.GetRelativePath(root,p).Replace('\\','/')).Where(n=>!expected.Contains(n)).OrderBy(n=>n).ToArray();
        var report=new Report(version,root,count,problems.ToArray(),extra,sources,
            ["packs/（包括用户副本，保留）","工程与作者 Pack（保留）","settings.json 与旧版缓存（保留）","recovery/ 与未保存编辑（保留）"]);
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        var temporary=reportPath+".writing";
        File.WriteAllText(temporary,JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));File.Move(temporary,reportPath,true);
        if(problems.Count>0) throw new InvalidDataException("升级全检发现未覆盖的旧组件或错误来源：\n"+string.Join("\n",problems.Take(12))+"\n请用本版本完整包覆盖安装；报告："+reportPath);
        File.WriteAllText(marker,token);
        FileLog.Write($"升级全检：通过，组件 {count} 个，清单外文件 {extra.Length} 个（仅报告，未删除）；报告：{reportPath}");
        return reportPath;
    }
    public static string[] ComponentProblems(string installation,InstallationManifest manifest,string? retainedInstallation=null)
    {
        if(manifest.Components is null) return [];
        var expected=new HashSet<string>(manifest.Files.Keys,StringComparer.OrdinalIgnoreCase);
        var problems=new List<string>();
        foreach(var (name,hash) in manifest.Components)
        {
            var relative=name.Replace('\\','/');
            if(!IsComponent(relative)||!expected.Add(relative)) throw new InvalidDataException("全检清单含无效或重复路径："+name);
            var path=InstallationCheck.SafePath(installation,relative);
            if (!File.Exists(path) && retainedInstallation is not null) path=InstallationCheck.SafePath(retainedInstallation,relative);
            if(!File.Exists(path)) problems.Add(relative+"（缺失）");
            else if(!Hash(path).Equals(hash,StringComparison.OrdinalIgnoreCase)) problems.Add(relative+"（与本版本不一致）");
        }
        return problems.ToArray();
    }
    public static IEnumerable<string> PublishedBundledFiles(string directory,string pattern,string? installation=null)
    {
        installation ??= AppContext.BaseDirectory;
        if(!Directory.Exists(directory)) return [];
        var manifestPath=Path.Combine(installation,InstallationCheck.ManifestName);
        var manifest=File.Exists(manifestPath)
            ? JsonSerializer.Deserialize<InstallationManifest>(File.ReadAllText(manifestPath)) : null;
        var components=manifest?.Components;
        var files=Directory.GetFiles(directory,pattern);
        if(components is null) return files; // 开发输出不带发行清单。
        var expected=new HashSet<string>(components.Keys.Concat(manifest?.ReferenceFiles?.Keys ?? Enumerable.Empty<string>()).Select(p=>p.Replace('\\','/')),StringComparer.OrdinalIgnoreCase);
        return files.Where(p=>expected.Contains(Path.GetRelativePath(installation,p).Replace('\\','/'))).ToArray();
    }
    private static string Hash(string path){using var input=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(input));}
    public static bool IsComponent(string name)=>!name.Contains('/')&&(name.EndsWith(".dll",StringComparison.OrdinalIgnoreCase)||name.EndsWith(".exe",StringComparison.OrdinalIgnoreCase)||name.EndsWith(".json",StringComparison.OrdinalIgnoreCase))
        || name.StartsWith("rpfm/",StringComparison.OrdinalIgnoreCase)||name.StartsWith("bundled/",StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("runtimes/",StringComparison.OrdinalIgnoreCase)
        || System.Text.RegularExpressions.Regex.IsMatch(name,@"^[a-z]{2}(-[A-Za-z]{2,4})?/[^/]+\.dll$",System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private static IEnumerable<string> EnumerateComponentFiles(string root,string relative)
    {
        var directory=InstallationCheck.SafePath(root,relative);
        if(!Directory.Exists(directory)) yield break;
        foreach(var path in Directory.EnumerateFiles(directory,"*",new EnumerationOptions{RecurseSubdirectories=true,AttributesToSkip=FileAttributes.ReparsePoint}))
            yield return InstallationCheck.SafePath(root,Path.GetRelativePath(root,path));
    }
}
