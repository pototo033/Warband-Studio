using System.IO;
using System.Text.Json;

namespace WarbandStudio.Pack;

/// <summary>工程里记的东西（`&lt;工程目录&gt;/project.json`）。</summary>
public sealed class ProjectInfo
{
    /// <summary>工程名（默认取目录名，用户可以改）</summary>
    public string Name { get; set; } = "";

    /// <summary>**项目 key**：新建单位组的命名前缀（`&lt;key&gt;_&lt;页签&gt;_&lt;兵种词…&gt;`，见 GroupNaming）。</summary>
    public string ProjectKey { get; set; } = "";

    /// <summary>**生成 Pack 所在目录**（工作包放这；相对工程目录，或绝对路径）。默认 `packs`（工程目录的子文件夹）。</summary>
    public string PackDir { get; set; } = ProjectStore.DefaultPackDir;

    /// <summary>上次打开的包（打开工程时它是**激活**的那个；旧的单包字段，迁移进 <see cref="Packs"/>）。</summary>
    public string LastPack { get; set; } = "";

    /// <summary>**识别清单**：属于本工程、打开工程时要一起打开的包（相对工程目录的路径，正斜杠）。
    /// "导入 Pack 到工程"会自动登记；工程里其它没登记的包不会自动开（用「工程内的包…」勾选）。</summary>
    public List<string> Packs { get; set; } = [];

    public string Created { get; set; } = "";
}

/// <summary>
/// 工程目录（用户选一个文件夹 = 一个工程；多个战帮/项目各用各的目录，互不影响）：
/// <code>
/// &lt;工程目录&gt;/
/// ├── project.json      # 工程名 / 项目 key / 生成 Pack 目录 / 上次打开的包
/// ├── packs/            # 工作包（"生成 Pack 所在目录"，默认在工程目录下、可另选）
/// └── old/              # 历史版本（每次保存前自动备份，保留最近 N 份，可随时还原）
/// </code>
/// 编辑集本来就是按包路径分的（Backend），所以多工程切换不会串台；
/// 这里负责的是：工程骨架、项目 key、历史版本（备份/清扫/列举）。
/// </summary>
public static class ProjectStore
{
    public const string ProjectFile = "project.json";
    public const string HistoryDir = "old";
    /// <summary>默认的"生成 Pack 所在目录"（工程目录的子文件夹）。</summary>
    public const string DefaultPackDir = "packs";

    /// <summary>这个目录是不是工程（有 project.json）。</summary>
    public static bool IsProject(string dir) =>
        !string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, ProjectFile));

    /// <summary>把包所在目录当工程目录（兼容两种布局：包在工程根 / 包在"生成 Pack 目录"里）。</summary>
    public static string? ProjectDirOfPack(string packPath)
    {
        try
        {
            var full = Path.GetFullPath(packPath);
            var dir = Path.GetDirectoryName(full);
            if (dir is null) return null;
            if (IsProject(dir)) return dir;                         // 旧布局：包就在工程根
            var parent = Path.GetDirectoryName(dir);                // 新布局：包在「生成 Pack 目录」（默认 工程\packs）
            if (parent is not null && IsProject(parent)
                && Path.GetFullPath(PackDirOf(parent)).TrimEnd(Path.DirectorySeparatorChar)
                       .Equals(Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                return parent;
            return null;
        }
        catch { return null; }
    }

    /// <summary>"生成 Pack 所在目录"的绝对路径（相对路径按工程目录拼；空则退回默认 packs/）。</summary>
    public static string PackDirOf(string projectDir, ProjectInfo? info = null)
    {
        info ??= Load(projectDir);
        var d = (info.PackDir ?? "").Trim();
        if (d.Length == 0) d = DefaultPackDir;
        return Path.IsPathRooted(d) ? d : Path.Combine(projectDir, d);
    }

    public static ProjectInfo Load(string dir)
    {
        try
        {
            var f = Path.Combine(dir, ProjectFile);
            if (File.Exists(f))
                return JsonSerializer.Deserialize<ProjectInfo>(File.ReadAllText(f)) ?? new ProjectInfo();
        }
        catch { /* 工程文件坏了 → 用目录名兜底，别拦着开工程 */ }
        return new ProjectInfo { Name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar)) };
    }

    public static void Save(string dir, ProjectInfo p)
    {
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, ProjectFile),
                JsonSerializer.Serialize(p, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 存不下不致命 */ }
    }

    /// <summary>
    /// 把目录登记成工程（已有 project.json 就沿用，只补目录）。新建时用
    /// <paramref name="prefillKey"/>（旧版全局"新组名前缀"的迁移种子）预填项目 key，
    /// <paramref name="packDir"/> 指定"生成 Pack 目录"（空 = 默认 packs/）。
    /// </summary>
    public static ProjectInfo Ensure(string dir, string? prefillKey = null, string? packDir = null)
    {
        Directory.CreateDirectory(dir);
        var p = IsProject(dir) ? Load(dir) : new ProjectInfo
        {
            Name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar)),
            ProjectKey = prefillKey ?? "",
            PackDir = string.IsNullOrWhiteSpace(packDir) ? DefaultPackDir : packDir!.Trim(),
            Created = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        };
        if (string.IsNullOrWhiteSpace(p.Name)) p.Name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar));
        Directory.CreateDirectory(HistoryDirOf(dir));
        try { Directory.CreateDirectory(PackDirOf(dir, p)); } catch { /* 目录建不出来不拦（后面打开会报） */ }
        Save(dir, p);
        return p;
    }

    public static string HistoryDirOf(string projectDir) => Path.Combine(projectDir, HistoryDir);

    /// <summary>工程目录下的 .pack（按名字排序；old/ 里的不算）。</summary>
    public static List<string> PacksIn(string projectDir)
    {
        try
        {
            var dir = PackDirOf(projectDir);
            var list = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.pack").ToList() : [];
            if (list.Count == 0
                && !Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar)
                        .Equals(Path.GetFullPath(projectDir).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(projectDir))
                list.AddRange(Directory.GetFiles(projectDir, "*.pack"));   // 兼容旧布局（包直接放工程根）
            return list.OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch { return []; }
    }

    /// <summary>包 → 存在 project.json 里的相对路径（正斜杠；工程搬家也不怕）。</summary>
    public static string RelPath(string projectDir, string packFullPath)
    {
        try
        {
            return Path.GetRelativePath(Path.GetFullPath(projectDir), Path.GetFullPath(packFullPath))
                       .Replace((char)92, '/');
        }
        catch { return Path.GetFileName(packFullPath); }
    }

    /// <summary>相对路径 → 绝对路径（坏路径返回空串）。</summary>
    public static string FullPath(string projectDir, string rel)
    {
        try
        {
            return string.IsNullOrWhiteSpace(rel) ? "" : Path.GetFullPath(Path.Combine(projectDir, rel.Replace('/', (char)92)));
        }
        catch { return ""; }
    }

    /// <summary>把相对路径并进识别清单（去重、保持原顺序）；返回是否新增。</summary>
    public static bool AddPack(ProjectInfo info, string rel)
    {
        if (string.IsNullOrWhiteSpace(rel)) return false;
        if (info.Packs.Any(p => p.Equals(rel, StringComparison.OrdinalIgnoreCase))) return false;
        info.Packs.Add(rel);
        return true;
    }

    /// <summary>把一个包备份进 `old/`（**用 .pack 后缀**，直接拷出来就能当包用），返回备份路径；
    /// 备份后按 <paramref name="keep"/> 份数清扫（保留最近 N 份）。</summary>
    public static string? BackupPack(string projectDir, string packPath, int keep)
    {
        try
        {
            var dir = HistoryDirOf(projectDir);
            Directory.CreateDirectory(dir);
            var stem = Path.GetFileNameWithoutExtension(packPath);
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var dest = Path.Combine(dir, $"{stem}.{stamp}.pack");
            for (var k = 2; File.Exists(dest) && k <= 99; k++)
                dest = Path.Combine(dir, $"{stem}.{stamp}_{k}.pack");
            File.Copy(packPath, dest);
            PruneHistory(projectDir, stem, keep);
            return dest;
        }
        catch { return null; }
    }

    /// <summary>历史版本：保留最近 <paramref name="keep"/> 份（文件名里的时间戳固定宽度 → 名字倒序=新到旧），
    /// 多出来的删掉。返回删掉的份数。</summary>
    public static int PruneHistory(string projectDir, string packStem, int keep)
    {
        try
        {
            if (keep <= 0) return 0;
            var dir = HistoryDirOf(projectDir);
            if (!Directory.Exists(dir)) return 0;
            var files = Directory.GetFiles(dir, packStem + ".*.pack")
                                 .OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal)
                                 .ToList();
            var n = 0;
            foreach (var f in files.Skip(keep))
            {
                try { File.Delete(f); n++; } catch { /* 删不掉就留着 */ }
            }
            return n;
        }
        catch { return 0; }
    }

    /// <summary>列出某个包的历史版本（新到旧）。</summary>
    public static List<FileInfo> HistoryOf(string projectDir, string packPath)
    {
        try
        {
            var stem = Path.GetFileNameWithoutExtension(packPath);
            var dir = HistoryDirOf(projectDir);
            if (!Directory.Exists(dir)) return [];
            return Directory.GetFiles(dir, stem + ".*.pack")
                            .Select(f => new FileInfo(f))
                            .OrderByDescending(f => f.Name, StringComparer.Ordinal)
                            .ToList();
        }
        catch { return []; }
    }

    /// <summary>登记"最近工程"（去重、新到旧、最多 <paramref name="max"/> 个）；调用方负责把列表存盘。</summary>
    public static void TouchRecent(List<string> recent, string dir, int max = 5)
    {
        recent.RemoveAll(p => p.Equals(dir, StringComparison.OrdinalIgnoreCase));
        recent.Insert(0, dir);
        while (recent.Count > max) recent.RemoveAt(recent.Count - 1);
    }
}
