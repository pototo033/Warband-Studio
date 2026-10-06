using WarbandStudio.Pack;
using WarbandStudio.Packfile;
using WarbandStudio.Rpfm;

namespace WarbandStudio.Probe;

/// <summary>
/// 直接替换包内某个文件的字节（走工具的导出管线：FileReplacements → WarbandExporter）。
///
/// 用法：probe set-file &lt;pack&gt; &lt;包内路径&gt; &lt;来源文件&gt; [--out out.pack]
///   · 不给 --out 就**写回原文件**（先存 &lt;pack&gt;.setfile.bak，写到 .saving 再替换）。
/// </summary>
public static class SetFile
{
    public static int Run(string[] a)
    {
        if (a.Length < 4)
        {
            Console.Error.WriteLine("用法: probe set-file <pack> <包内路径> <来源文件> [--out out.pack]");
            return 1;
        }
        var src = Path.GetFullPath(a[1]);
        var inner = a[2].Replace('\\', '/');
        var from = Path.GetFullPath(a[3]);
        string? outArg = null;
        for (var i = 4; i < a.Length; i++) if (a[i] == "--out") outArg = ++i < a.Length ? a[i] : null;
        if (!File.Exists(from)) { Console.Error.WriteLine("来源文件不存在：" + from); return 1; }

        var schema = WarbandStudio.Packfile.Schema.Load(RpfmCli.LocateSchema() ?? throw new FileNotFoundException("找不到 schema。"));
        var vanilla = DetectVanilla() ?? throw new FileNotFoundException("找不到原版 db.pack（settings.json 的 GameDir）");

        var e = new WarbandEdits();
        e.FileReplacements.Add((inner, from));
        var dest = outArg is null ? src + ".saving" : Path.GetFullPath(outArg);
        var rep = WarbandExporter.Export(src, vanilla, dest, schema, e);
        foreach (var n in rep.Notes) Console.WriteLine("      · " + n);
        if (outArg is null)
        {
            var bak = src + ".setfile.bak";
            if (!File.Exists(bak)) File.Copy(src, bak);
            File.Move(dest, src, overwrite: true);
            Console.WriteLine("[i] 备份：" + Path.GetFileName(bak));
        }
        Console.WriteLine("[✓] 已写入：" + (outArg is null ? src : dest) + $"（{inner} ← {Path.GetFileName(from)}，{new FileInfo(from).Length} 字节）");
        return 0;
    }

    private static string? DetectVanilla()
    {
        try
        {
            var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WarbandStudio", "settings.json");
            if (!File.Exists(p)) return null;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(p));
            var game = doc.RootElement.TryGetProperty("GameDir", out var g) ? g.GetString() : null;
            if (string.IsNullOrWhiteSpace(game)) return null;
            var db = Path.Combine(game!, "data", "db.pack");
            return File.Exists(db) ? db : null;
        }
        catch { return null; }
    }
}
