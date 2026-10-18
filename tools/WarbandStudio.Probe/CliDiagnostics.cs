using System.Text.Json;
using WarbandStudio.Rpfm;

namespace WarbandStudio.Probe;

public static class CliDiagnostics
{
    public static async Task<int> Run(string[] args)
    {
        if (args.Length != 5) throw new ArgumentException("cli-diagnose <pack> <game-dir> <cache-dir> <out.json>");
        var cli = new RpfmCli(RpfmCli.LocateCli() ?? throw new FileNotFoundException("缺少 CLI"),
            RpfmCli.LocateSchema() ?? throw new FileNotFoundException("缺少 schema"))
        { GamePath = args[2], DiagnosticCacheDirectory = args[3] };
        cli.Log += Console.WriteLine;
        var report = await cli.DiagnoseAsync([args[1]]);
        await File.WriteAllTextAsync(args[4], report.StdOut);
        using var doc = JsonDocument.Parse(report.StdOut);
        Console.WriteLine($"PASS 诊断已完成，{doc.RootElement.GetProperty("results").GetArrayLength()} 个报告组；结果：{args[4]}");
        return 0;
    }
}
