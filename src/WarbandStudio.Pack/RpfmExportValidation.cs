using WarbandStudio.Rpfm;
using WarbandStudio.Packfile;

namespace WarbandStudio.Pack;

public static class RpfmExportValidation
{
    /// <summary>Validate the fully materialized pack, without using Assembly Kit or a UI synchronization context.</summary>
    public static RpfmDiagnosticReport Validate(string stagedPack, string vanillaDbPack, Action<string>? log = null,
        IReadOnlyList<string>? referencePacks = null)
    {
        var cli = new RpfmCli(RpfmCli.LocateCli() ?? throw new FileNotFoundException("保存需要随包的 rpfm/rpfm_cli.exe。"),
            RpfmCli.LocateSchema() ?? throw new FileNotFoundException("保存需要随包的 schema_wh3.ron。"))
        { GamePath = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(vanillaDbPack))) };
        if (log is not null) cli.Log += log;
        var scratch = Path.Combine(Path.GetDirectoryName(stagedPack)!, "studio_diagnostic_" + Guid.NewGuid().ToString("N") + ".pack");
        RpfmDiagnosticReport report;
        try
        {
            var references = referencePacks ?? [];
            var injected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var input = stagedPack;
            if (references.Count > 0)
            {
                injected = BuildReferenceView(stagedPack, references, scratch);
                input = scratch;
                log?.Invoke($"RPFM 引用来源：{references.Count} 个外部兵种/依赖包，仅用于校验，不写入目标包。");
                foreach (var path in references) log?.Invoke("引用包：" + path);
            }
            var result = Task.Run(() => cli.DiagnoseAsync([input])).GetAwaiter().GetResult();
            var parsed = RpfmDiagnosticReport.Parse(result.StdOut);
            // 外部包自己的报错不属于本次保存；当前包和配置错误仍全部保留。
            report = DescribeRows(new(parsed.Items.Where(x => !injected.Contains(x.Path.Replace('\\', '/'))).ToList()),
                stagedPack, Schema.Load(cli.SchemaPath));
        }
        finally { if (File.Exists(scratch)) File.Delete(scratch); }
        log?.Invoke(report.Summary);
        foreach (var item in report.Items) log?.Invoke(item.ToString());
        return report;
    }

    public static HashSet<string> BuildReferenceView(string stagedPack, IReadOnlyList<string> references, string output)
    {
        using var staged = PackArchive.Open(stagedPack);
        var additions = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in references)
        {
            using var source = PackArchive.Open(path);
            foreach (var entry in source.VisibleEntries.Where(e => e.Path.StartsWith("db/", StringComparison.OrdinalIgnoreCase)))
            {
                // 独立文件名确保外部同名文件不会遮住待校验行；后缀字母避免 RPFM 数字结尾诊断。
                string target;
                do { target = $"db/{PackSession.TableNameOf(entry.Path)}/studio_reference_{Guid.NewGuid():N}_source"; }
                while (staged.Find(target) is not null || additions.ContainsKey(target));
                additions.Add(target, source.ReadDecoded(entry));
            }
        }
        PackWriter.WriteFrom(staged, additions, output);
        return additions.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public static RpfmDiagnosticReport DescribeRows(RpfmDiagnosticReport report, string packPath, Schema schema)
    {
        using var pack = PackArchive.Open(packPath);
        var tables = new Dictionary<string, DbTable>(StringComparer.OrdinalIgnoreCase);
        return new(report.Items.Select(item =>
        {
            if (!item.Path.StartsWith("db/", StringComparison.OrdinalIgnoreCase)) return item;
            try
            {
                if (!tables.TryGetValue(item.Path, out var table))
                {
                    var entry = pack.Find(item.Path);
                    if (entry is null) return item;
                    table = DbTable.Decode(pack.ReadDecoded(entry), PackSession.TableNameOf(item.Path), schema);
                    tables[item.Path] = table;
                }
                var rows = item.Cells.Select(c => c.Row).Where(r => r >= 0 && r < table.Rows.Count).Distinct();
                var columns = table.Columns.Select((c, i) => (c.Name, Index: i, IsKey: c.Source?.IsKey == true)).Where(c =>
                    c.Name is "unit" or "military_group" or "faction" ||
                    (c.Name == "key" && table.TableName == "units_to_exclusive_faction_permissions_tables") || c.IsKey).ToArray();
                var context = string.Join("；", rows.Select(r => $"行 {r + 1}: " + string.Join(", ", columns.Select(c =>
                    $"{(c.Name == "key" && table.TableName == "units_to_exclusive_faction_permissions_tables" ? "unit key" : c.Name)}={table.Rows[r][c.Index].ToTsv()}"))));
                return context.Length == 0 ? item : item with { RowContext = context };
            }
            catch (Exception ex) { return item with { RowContext = "行详情读取失败：" + ex.Message }; }
        }).ToList());
    }
}
