using WarbandStudio.Pack;
using WarbandStudio.Rpfm;
using WarbandStudio.Packfile;

namespace WarbandStudio.Ui.Services;

public sealed partial class Backend
{
    internal DbTable? AutomationTable(string table, string[] keys)
    {
        var (bytes, snapshot) = ProjectTables();
        using var view = OpenCanvasSource(bytes, snapshot.RemoveFiles);
        return TableFiles.ReadComposite(view, table, keys, GetSchema());
    }
    internal RpfmDiagnosticReport? LastExportDiagnostics { get; private set; }
    internal void ResetExportDiagnostics() => LastExportDiagnostics = null;

    private RpfmDiagnosticReport ValidateExport(string staged, string vanilla, string source)
    {
        var report = (validateStaged ?? (p => RpfmExportValidation.Validate(
            p, vanilla, line => Log?.Invoke(line), PermissionReferencePacks(source))))(staged);
        report = WarbandReferenceIntegrity.Combine(report, staged, vanilla, GetSchema(), PermissionReferencePacks(source));
        foreach (var issue in report.Items.Where(item => item.RowContext?.StartsWith("最终覆盖校验") == true)) Log?.Invoke(issue.ToString());
        LastExportDiagnostics = report;
        return report;
    }

    internal IReadOnlyList<string> AutomationFiles()
    {
        var (bytes, snapshot) = ProjectTables();
        using var view = OpenCanvasSource(bytes, snapshot.RemoveFiles);
        return view.VisibleEntries.Select(e => e.Path).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal byte[] AutomationReadFile(string path)
    {
        var (bytes, snapshot) = ProjectTables();
        using var view = OpenCanvasSource(bytes, snapshot.RemoveFiles);
        return view.ReadDecoded(path);
    }

    internal void AutomationRequireUnit(string unit) => RequireKnownPermissionUnit(unit);

    internal void AutomationAttachProject(string? directory, string? key)
    {
        if (_pack is { } pack) BindProject(pack, directory, key);
    }
}
