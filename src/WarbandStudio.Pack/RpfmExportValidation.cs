using WarbandStudio.Rpfm;

namespace WarbandStudio.Pack;

public static class RpfmExportValidation
{
    /// <summary>Validate the fully materialized pack, without using Assembly Kit or a UI synchronization context.</summary>
    public static RpfmDiagnosticReport Validate(string stagedPack, string vanillaDbPack, Action<string>? log = null)
    {
        var cli = new RpfmCli(RpfmCli.LocateCli() ?? throw new FileNotFoundException("保存需要随包的 rpfm/rpfm_cli.exe。"),
            RpfmCli.LocateSchema() ?? throw new FileNotFoundException("保存需要随包的 schema_wh3.ron。"))
        { GamePath = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(vanillaDbPack))) };
        if (log is not null) cli.Log += log;
        var result = Task.Run(() => cli.DiagnoseAsync([stagedPack])).GetAwaiter().GetResult();
        var report = RpfmDiagnosticReport.Parse(result.StdOut);
        log?.Invoke(report.Summary);
        foreach (var item in report.Items) log?.Invoke(item.ToString());
        return report;
    }
}
