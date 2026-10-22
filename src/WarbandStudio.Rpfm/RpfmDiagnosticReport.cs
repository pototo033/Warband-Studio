using System.Text.Json;

namespace WarbandStudio.Rpfm;

public enum DiagnosticSeverity { Info, Warning, Error }

public sealed record RpfmDiagnostic(DiagnosticSeverity Severity, string Path, string Type, string Detail)
{
    public IReadOnlyList<(int Row, int Column)> Cells { get; init; } = [];
    public string? Field { get; init; }
    public string? Value { get; init; }
    public string? RowContext { get; init; }
    public override string ToString()
    {
        var rows = Cells.Where(c => c.Row >= 0).Select(c => c.Row + 1).Distinct().ToArray();
        var location = rows.Length > 0 ? "；数据行 " + string.Join(", ", rows) + "（从 1 开始，不含表头）" : "";
        var detail = Type == "InvalidReference" && Field is not null
            ? $"无效引用：列 {Field}，值「{Value}」" : Type + " " + Detail;
        return $"{Severity}: {Path}{location} — {detail}" + (RowContext is null ? "" : "；" + RowContext);
    }
}

/// <summary>RPFM 4.7.4 serializes report_type, not level. Match its DiagnosticReport::level exactly.</summary>
public sealed record RpfmDiagnosticReport(IReadOnlyList<RpfmDiagnostic> Items)
{
    public int Errors => Items.Count(x => x.Severity == DiagnosticSeverity.Error);
    public int Warnings => Items.Count(x => x.Severity == DiagnosticSeverity.Warning);
    public string Summary => $"RPFM：{Errors} Error，{Warnings} Warning";

    public void RequireSaveAllowed()
    {
        if (Errors > 0) throw new InvalidDataException("RPFM 校验未通过，原包和未保存编辑保持不变。" + Summary + "；" +
            Environment.NewLine + string.Join(Environment.NewLine, Items.Where(x => x.Severity == DiagnosticSeverity.Error).Take(8)) +
            (Errors > 8 ? Environment.NewLine + $"另有 {Errors - 8} 条错误；完整明细已写入日志，可“导出编辑与日志”。" : ""));
    }

    public static RpfmDiagnosticReport Parse(string json)
    {
        using var document = JsonDocument.Parse(RpfmCli.ParseDiagnosticsJson(json));
        var items = new List<RpfmDiagnostic>();
        foreach (var group in document.RootElement.GetProperty("results").EnumerateArray())
        {
            if (group.ValueKind != JsonValueKind.Object || group.EnumerateObject().Count() != 1)
                throw new RpfmException("诊断未完成：RPFM 报告组格式错误。");
            var category = group.EnumerateObject().Single();
            if (category.Name is not ("DB" or "Loc" or "Config" or "Dependency" or "Pack" or "AnimFragmentBattle" or "PortraitSettings" or "Text"))
                throw new RpfmException("诊断未完成：未知的 RPFM 报告组 " + category.Name);
            if (category.Value.ValueKind != JsonValueKind.Object ||
                !category.Value.TryGetProperty("results", out var reports) || reports.ValueKind != JsonValueKind.Array)
                throw new RpfmException("诊断未完成：RPFM 报告缺少明细。");
            var path = category.Value.TryGetProperty("path", out var p) ? p.GetString() ?? category.Name : category.Name;
            foreach (var report in reports.EnumerateArray())
            {
                if (report.ValueKind != JsonValueKind.Object || !report.TryGetProperty("report_type", out var type))
                    throw new RpfmException("诊断未完成：RPFM 明细缺少 report_type。");
                string name, detail;
                if (type.ValueKind == JsonValueKind.String) { name = type.GetString()!; detail = ""; }
                else if (type.ValueKind == JsonValueKind.Object && type.EnumerateObject().Count() == 1)
                {
                    var entry = type.EnumerateObject().Single(); name = entry.Name; detail = entry.Value.GetRawText();
                }
                else throw new RpfmException("诊断未完成：RPFM report_type 格式错误。");
                var severity = Level(category.Name, name);
                var cells = new List<(int Row, int Column)>();
                if (report.TryGetProperty("cells_affected", out var affected))
                    foreach (var cell in affected.EnumerateArray()) cells.Add((cell[0].GetInt32(), cell[1].GetInt32()));
                string? field = null, value = null;
                if (name == "InvalidReference" && type.ValueKind == JsonValueKind.Object)
                {
                    var pair = type.GetProperty(name);
                    if (pair.ValueKind == JsonValueKind.Array && pair.GetArrayLength() == 2)
                    { value = pair[0].GetString(); field = pair[1].GetString(); }
                }
                items.Add(new(severity, path, name, detail) { Cells = cells, Field = field, Value = value });
            }
        }
        return new(items);
    }

    private static DiagnosticSeverity Level(string category, string type) => (category, type) switch
    {
        ("DB" or "Loc", "OutdatedTable" or "InvalidReference" or "EmptyKeyField" or "InvalidLocKey" or
            "TableNameEndsInNumber" or "TableNameHasSpace" or "BannedTable" or "ValueCannotBeEmpty" or "AlteredTable") => DiagnosticSeverity.Error,
        ("DB" or "Loc", "EmptyRow" or "EmptyKeyFields" or "DuplicatedCombinedKeys" or "NoReferenceTableNorColumnFoundNoPak" or
            "InvalidEscape" or "DuplicatedRow" or "TableIsDataCoring" or "FieldWithPathNotFound") => DiagnosticSeverity.Warning,
        ("DB" or "Loc", "NoReferenceTableFound" or "NoReferenceTableNorColumnFoundPak") => DiagnosticSeverity.Info,
        ("Config", "DependenciesCacheNotGenerated" or "DependenciesCacheOutdated" or "DependenciesCacheCouldNotBeLoaded" or "IncorrectGamePath") => DiagnosticSeverity.Error,
        ("Dependency", "InvalidDependencyPackName") => DiagnosticSeverity.Error,
        ("Pack", "InvalidPackName" or "InvalidFileName") => DiagnosticSeverity.Error,
        ("Pack", "MissingLocDataFileDetected" or "FileITM" or "FileDuplicated") => DiagnosticSeverity.Warning,
        ("Pack", "FileOverwrite") => DiagnosticSeverity.Info,
        ("AnimFragmentBattle", "LocomotionGraphPathNotFound" or "FilePathNotFound" or "MetaFilePathNotFound" or "SndFilePathNotFound") => DiagnosticSeverity.Warning,
        ("PortraitSettings", "DatacoredPortraitSettings" or "InvalidArtSetId" or "InvalidVariantFilename" or
            "FileDiffuseNotFoundForVariant" or "FileMask1NotFoundForVariant" or "FileMask2NotFoundForVariant" or "FileMask3NotFoundForVariant") => DiagnosticSeverity.Warning,
        ("Text", "InvalidKey") => DiagnosticSeverity.Error,
        _ => throw new RpfmException($"诊断未完成：不支持的 RPFM 诊断类型 {category}.{type}，未写入目标包。"),
    };
}
