using WarbandStudio.Packfile;
using WarbandStudio.Rpfm;

namespace WarbandStudio.Pack;

/// <summary>
/// 一次编辑会话打开的 pack。
/// **读**走原生格式层（<see cref="PackArchive"/>：毫秒级、内存里、不起进程）；
/// **写**暂时还走随包的 rpfm_cli（原生写回是下一步 M6）。
/// </summary>
public sealed class PackSession(RpfmCli? cli)
{
    private readonly RpfmCli? _cli = cli;
    private PackArchive? _archive;
    private List<RFileInfo> _files = [];

    public string? PackPath { get; private set; }

    /// <summary>文件树里这棵树的根名（默认文件名；原版那棵叫「原版战帮升级」）。</summary>
    public string DisplayName { get; set; } = "";
    /// <summary>
    /// "兵种包"（加载为兵种 mod）：只当**参考包**用 —— 它的 main_units / land_units / unit_variants / loc
    /// 会被并进图标与中文名的候选链、并出现在「mod 兵种」页；但它不当"当前包"（不占画布、不进战帮树、可读不可写）。
    /// </summary>
    public bool IsUnitPack { get; set; }

    /// <summary>
    /// 战帮相关的表（用户给的清单 + 教程里的 Effect 连接表）。
    /// 文件树只显示这些表与页签相关的 ui 文件，其余（兵牌、绑定表等）不在这里占地方。
    /// </summary>
    public static readonly string[] WarbandTables =
    [
        // 兵组与升级路线
        "unit_upgrade_groups_tables",
        "unit_to_unit_group_junctions_tables",
        "unit_upgrade_to_unit_groups_tables",
        "unit_upgrade_to_building_level_requirements_tables",
        "unit_upgrade_to_tech_requirements_tables",
        // 界面：分类 / 位置 / 连接线
        "unit_upgrade_group_ui_categories_tables",
        "unit_upgrade_group_ui_infos_tables",
        "unit_upgrade_group_ui_links_tables",
        // 成本与派系接入
        "resource_costs_tables",
        "resource_cost_pooled_resource_junctions_tables",
        "campaign_features_tables",
        "ui_features_to_cultures_tables",
        "units_to_groupings_military_permissions_tables",
        "units_to_exclusive_faction_permissions_tables",
        // Effect 影响升级路线
        "effect_bonus_value_unit_upgrade_to_unit_group_junctions_tables",
        "campaign_bonus_value_ids_unit_upgrade_to_unit_groups_tables",
        // 注意：main_units / land_units / unit_variants / factions 不在这里 —— 它们归"右下兵种库"实时读取
    ];

    /// <summary>这个包内路径要不要出现在文件树里（战帮相关）。</summary>
    /// <summary>
    /// "只加行"的表：工具只会往里**新增**覆盖表文件（`studio_*`），不展示原版全量内容
    /// （用户要求：这类表初始只展示一个空文件，加入画布时才新建）。
    /// </summary>
    public static readonly string[] AddOnlyTables =
    [
        "main_units_tables",
        "units_to_groupings_military_permissions_tables",
        "resource_costs_tables",
        "ui_features_to_cultures_tables",
    ];

    public static bool IsWarbandFile(string innerPath)
    {
        var p = innerPath.Replace('\\', '/');
        if (p.StartsWith("db/", StringComparison.OrdinalIgnoreCase))
        {
            var table = p.Split('/')[1];
            if (!WarbandTables.Contains(table, StringComparer.OrdinalIgnoreCase)) return false;
            // "只加行"的表：只显示工具写的覆盖表文件（studio_*），原版数据文件不列出来
            if (AddOnlyTables.Contains(table, StringComparer.OrdinalIgnoreCase))
            {
                var file = p.Split('/').Length > 2 ? p.Split('/')[2] : "";
                return file.StartsWith("studio_", StringComparison.OrdinalIgnoreCase);
            }
            return true;
        }
        if (p.StartsWith("ui/", StringComparison.OrdinalIgnoreCase))
            return p.Contains("warband_upgrades", StringComparison.OrdinalIgnoreCase)
                || p.Contains("campaign ui/", StringComparison.OrdinalIgnoreCase);
        return false;
    }
    public ContainerInfo? Info { get; private set; }
    public IReadOnlyList<RFileInfo> Files => _files;
    public bool IsOpen => _archive is not null;

    /// <summary>原生打开的包（读表用）。</summary>
    public PackArchive? Archive => _archive;

    /// <summary>打开一个 .pack：原生读一遍索引（加密包/老格式会抛出不支持）。</summary>
    public Task OpenAsync(string packPath, CancellationToken ct = default)
    {
        Close();
        var archive = PackArchive.Open(packPath);
        _archive = archive;
        PackPath = packPath;
        Info = new ContainerInfo { FileName = Path.GetFileName(packPath), FilePath = packPath };
        if (DisplayName.Length == 0) DisplayName = Path.GetFileName(packPath);
        _files = archive.VisibleEntries
            .Where(e => IsWarbandFile(e.Path))
            .Select(e => new RFileInfo
            {
                Path = e.Path,
                ContainerName = Info.FileName,
                Timestamp = e.Timestamp == 0 ? null : e.Timestamp,
                FileType = GuessFileType(e.Path),
            })
            .ToList();
        return Task.CompletedTask;
    }

    public Task CloseAsync(CancellationToken ct = default)
    {
        Close();
        return Task.CompletedTask;
    }

    private void Close()
    {
        _archive?.Dispose();
        _archive = null;
        PackPath = null;
        Info = null;
        _files = [];
    }

    /// <summary>原生解一张 DB 表（包内路径形如 db/xxx_tables/data__）。</summary>
    public DbTable ReadTable(string innerPath, Schema schema)
    {
        var archive = _archive ?? throw new InvalidOperationException("还没打开 pack。");
        var entry = archive.Find(innerPath) ?? throw new FileNotFoundException("包里没有：" + innerPath);
        var bytes = archive.ReadDecoded(entry);
        return DbTable.Decode(bytes, TableNameOf(innerPath), schema);
    }

    /// <summary>从包内路径取表名：db/&lt;表名&gt;/…</summary>
    public static string TableNameOf(string innerPath)
    {
        var parts = innerPath.Replace('\\', '/').Split('/');
        return parts.Length > 1 ? parts[1] : throw new InvalidDataException("看不出这是哪张表：" + innerPath);
    }

    /// <summary>是不是一张 DB 表（决定能不能用表视图打开）。</summary>
    public static bool IsDbTable(string innerPath)
    {
        var p = innerPath.Replace('\\', '/');
        return p.StartsWith("db/", StringComparison.OrdinalIgnoreCase)
               && (p.EndsWith("/data__", StringComparison.OrdinalIgnoreCase) || !p.Contains('.'));
    }

    /// <summary>把磁盘上的目录/文件加回包（写回仍走 rpfm_cli：TSV 按 schema 编译成二进制表）。</summary>
    public async Task AddAsync(string sourcePath, string destInPack, CancellationToken ct = default)
    {
        var pack = PackPath ?? throw new InvalidOperationException("还没打开 pack。");
        if (_cli is null) throw new InvalidOperationException("写回需要 rpfm_cli 引擎，但它没就绪。");
        var r = await _cli.AddAsync(pack, sourcePath, destInPack, tsvToBinary: true, ct);
        if (!r.Ok) throw new RpfmException("写回失败：" + r.All);
    }

    /// <summary>用 rpfm_cli 把一个包内目录导出到磁盘（写回流程的中间步骤用）。</summary>
    public async Task ExtractAsync(string inPackFolder, string destDir, CancellationToken ct = default)
    {
        var pack = PackPath ?? throw new InvalidOperationException("还没打开 pack。");
        if (_cli is null) throw new InvalidOperationException("导出需要 rpfm_cli 引擎，但它没就绪。");
        var r = await _cli.ExtractFolderAsync(pack, inPackFolder, destDir, asTsv: true, ct);
        if (!r.Ok) throw new RpfmException("导出失败：" + r.All);
    }

    /// <summary>按路径猜文件类型（给文件树的角标；和 rpfm_cli 的列表语义一致）。</summary>
    public static string GuessFileType(string path) => RpfmCli.GuessFileType(path);

    /// <summary>战帮相关的路径关键词（文件树默认只展开这些，其余折叠）。</summary>
    public static readonly string[] WarbandKeywords =
    [
        "unit_upgrade", "unit_to_unit", "units_to_groupings", "campaign_features",
        "ui_features_to_cultures", "resource_cost", "warband", "main_units", "land_units",
    ];

    public static bool IsWarbandRelated(string path) =>
        WarbandKeywords.Any(k => path.Contains(k, StringComparison.OrdinalIgnoreCase));
}
