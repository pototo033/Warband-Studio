using System.Text;
using WarbandStudio.Pack;
using WarbandStudio.Packfile;
using WarbandStudio.Rpfm;

namespace WarbandStudio.Probe;

/// <summary>用正式导出管线验证页签派系配置；只写显式指定的新文件。</summary>
public static class TabFactions
{
    public static int Run(string[] args)
    {
        if (args.Length < 5) throw new ArgumentException("用法：tab-factions <source.pack> <out.pack> <vanilla db.pack> <TAB=faction1,faction2>…；空值取消限制");
        var source = Path.GetFullPath(args[1]);
        var dest = Path.GetFullPath(args[2]);
        if (source.Equals(dest, StringComparison.OrdinalIgnoreCase) || File.Exists(dest))
            throw new IOException("验证输出必须是新文件，不覆盖源包或已有文件。");
        var edits = new WarbandEdits();
        foreach (var item in args.Skip(4))
        {
            var pair = item.Split('=', 2);
            if (pair.Length != 2) throw new ArgumentException("名单格式错误：" + item);
            edits.TabAllowedFactions[pair[0]] = TabFactionVisibility.Normalize(pair[1].Split(',', StringSplitOptions.RemoveEmptyEntries));
        }
        var schema = WarbandStudio.Packfile.Schema.Load(RpfmCli.LocateSchema() ?? throw new FileNotFoundException("找不到 schema。"));
        var report = WarbandExporter.Export(source, args[3], dest, schema, edits);
        using var pack = PackArchive.Open(dest);
        var entry = pack.VisibleEntries.Single(e => e.Path.EndsWith("warband_upgrades.twui.xml", StringComparison.OrdinalIgnoreCase));
        var actual = TabFactionVisibility.Read(Encoding.UTF8.GetString(pack.ReadDecoded(entry)));
        foreach (var (category, factions) in edits.TabAllowedFactions)
            if (!factions.SequenceEqual(actual.GetValueOrDefault(category) ?? [], StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("读回名单不一致：" + category);
        Console.WriteLine($"PASS 页签派系显示导出与读回：{edits.TabAllowedFactions.Count} 项 → {dest}");
        foreach (var note in report.Notes.Where(n => n.Contains("页签显示派系"))) Console.WriteLine(note);
        return 0;
    }
}
