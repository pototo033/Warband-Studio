using System.Diagnostics;
using System.Text;

namespace WarbandStudio.Rpfm;

/// <summary>一次 rpfm_cli 调用的结果。</summary>
public sealed record CliResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;

    /// <summary>合并输出（报错时给用户看的原文）。</summary>
    public string All => (StdOut + "\n" + StdErr).Trim();
}

/// <summary>
/// rpfm_cli 引擎 —— 随包内置的 **RPFM 4.7.4**（MIT，单文件 22MB）。
///
/// 为什么用 CLI 而不是 5.0.6 的常驻 server：
///   · 每次操作起一个独立进程，互不干扰（一次崩不影响别的调用）；
///   · 不占端口、不驻留后台、不会和用户自己的 RPFM 抢后端；
///   · schema 用命令行参数直接指定（随包带一份），不需要"首跑把 schema 装进配置目录"。
///
/// 命令面都是实测过的（4.7.4）：
///   pack list     -p &lt;pack&gt;                              列文件树
///   pack extract  -p &lt;pack&gt; -t &lt;schema&gt; -F "&lt;包内目录&gt;;&lt;落地目录&gt;"   导表为 TSV
///   pack add      -p &lt;pack&gt; -t &lt;schema&gt; -f "&lt;磁盘路径&gt;;&lt;包内目标&gt;"   写回（TSV→二进制）
///   pack create   -p &lt;pack&gt;                              新建空包
///   pack diagnose -g &lt;游戏目录&gt; -P &lt;依赖缓存&gt; -s &lt;schema&gt; -p &lt;pack&gt;…   诊断（JSON）
///   dependencies generate -P &lt;缓存&gt; -g &lt;游戏目录&gt; -s &lt;schema&gt;        生成依赖缓存
/// </summary>
public sealed class RpfmCli(string exePath, string schemaPath)
{
    public const string GameKey = "warhammer_3";
    public const string CliExeName = "rpfm_cli.exe";
    public const string SchemaFileName = "schema_wh3.ron";

    public string ExePath { get; } = exePath;
    public string SchemaPath { get; } = schemaPath;

    /// <summary>游戏安装目录（diagnose / 依赖缓存要用；导出打包本身不需要）。</summary>
    public string? GamePath { get; set; }

    /// <summary>依赖缓存（.pak2）路径；没生成过就是 null。</summary>
    public string? DependenciesCachePath { get; set; }

    /// <summary>命令行明细（界面上有日志区）。</summary>
    public event Action<string>? Log;

    /// <summary>跑一次 rpfm_cli。参数里不要带 -g &lt;game&gt;（这里统一补上）。</summary>
    public async Task<CliResult> RunAsync(IEnumerable<string> args, CancellationToken ct = default)
    {
        var argv = new List<string> { "--game", GameKey };
        argv.AddRange(args);

        var psi = new ProcessStartInfo(ExePath)
        {
            WorkingDirectory = Path.GetDirectoryName(ExePath) ?? ".",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in argv) psi.ArgumentList.Add(a);

        Log?.Invoke("rpfm_cli " + string.Join(' ', argv.Select(QuoteIfNeeded)));

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("起不来 rpfm_cli 进程。");
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        var result = new CliResult(p.ExitCode, await stdout, await stderr);
        if (!result.Ok) Log?.Invoke($"rpfm_cli 退出码 {result.ExitCode}：{Trim(result.All)}");
        return result;
    }

    // ── 读 ────────────────────────────────────────────────────────

    /// <summary>列 pack 内容（文件树的数据源）。</summary>
    public async Task<List<RFileInfo>> ListAsync(string packPath, CancellationToken ct = default)
    {
        var r = await RunAsync(["pack", "list", "-p", packPath], ct);
        if (!r.Ok) throw new RpfmException("列 pack 内容失败：" + Trim(r.All));

        var files = new List<RFileInfo>();
        foreach (var raw in r.StdOut.Split('\n'))
        {
            var line = raw.Trim();
            // 日志行（"16:53:25 [INFO] Logger initialized."）跳过，只留包内路径
            if (line.Length == 0 || line.Contains("[INFO]", StringComparison.Ordinal)
                || line.Contains("[WARN]", StringComparison.Ordinal)
                || line.Contains("[ERROR]", StringComparison.Ordinal)) continue;
            files.Add(new RFileInfo
            {
                Path = line.Replace('\\', '/'),
                ContainerName = System.IO.Path.GetFileName(packPath),
                FileType = GuessFileType(line),
            });
        }
        return files;
    }

    /// <summary>把一个包内目录导出到磁盘；asTsv=true 时表写成 TSV（双表头，与旧工具一致）。</summary>
    public Task<CliResult> ExtractFolderAsync(string packPath, string inPackFolder, string destDir,
                                             bool asTsv = true, CancellationToken ct = default)
    {
        var args = new List<string> { "pack", "extract", "-p", packPath };
        if (asTsv) { args.Add("-t"); args.Add(SchemaPath); }
        args.Add("-F"); args.Add($"{inPackFolder};{destDir}");
        return RunAsync(args, ct);
    }

    /// <summary>导出单个包内文件。</summary>
    public Task<CliResult> ExtractFileAsync(string packPath, string inPackFile, string destDir,
                                           bool asTsv = true, CancellationToken ct = default)
    {
        var args = new List<string> { "pack", "extract", "-p", packPath };
        if (asTsv) { args.Add("-t"); args.Add(SchemaPath); }
        args.Add("-f"); args.Add($"{inPackFile};{destDir}");
        return RunAsync(args, ct);
    }

    // ── 写 ────────────────────────────────────────────────────────

    /// <summary>新建一个空包。</summary>
    public Task<CliResult> CreateAsync(string packPath, CancellationToken ct = default) =>
        RunAsync(["pack", "create", "-p", packPath], ct);

    /// <summary>
    /// 把磁盘上的文件/目录加进包。<paramref name="destInPack"/> 以 / 结尾=放进该目录并保留原名，
    /// 否则最后一段当作包内新文件名。带 -t 时 TSV 会按 schema 编译成二进制表。
    /// </summary>
    public Task<CliResult> AddAsync(string packPath, string sourcePath, string destInPack,
                                    bool tsvToBinary = true, CancellationToken ct = default)
    {
        var args = new List<string> { "pack", "add", "-p", packPath };
        if (tsvToBinary) { args.Add("-t"); args.Add(SchemaPath); }
        args.Add("-f"); args.Add($"{sourcePath};{destInPack}");
        return RunAsync(args, ct);
    }

    // ── 诊断 / 依赖缓存 ───────────────────────────────────────────

    /// <summary>诊断（坏引用/非法枚举/空 key…），返回 JSON 原文。需要先有依赖缓存。</summary>
    public Task<CliResult> DiagnoseAsync(IEnumerable<string> packPaths, CancellationToken ct = default)
    {
        var args = new List<string> { "pack", "diagnose" };
        if (!string.IsNullOrWhiteSpace(GamePath)) { args.Add("-g"); args.Add(GamePath!); }
        if (!string.IsNullOrWhiteSpace(DependenciesCachePath)) { args.Add("-P"); args.Add(DependenciesCachePath!); }
        args.Add("-s"); args.Add(SchemaPath);
        foreach (var p in packPaths) { args.Add("-p"); args.Add(p); }
        return RunAsync(args, ct);
    }

    /// <summary>生成依赖缓存（原版表查询/诊断用；要读一遍游戏 data，慢一次）。</summary>
    public Task<CliResult> GenerateDependenciesAsync(string cachePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(GamePath))
            throw new RpfmException("还没设游戏目录，生成依赖缓存需要它。");
        return RunAsync(["dependencies", "generate", "-P", cachePath, "-g", GamePath!, "-s", SchemaPath], ct);
    }

    // ── 定位 ──────────────────────────────────────────────────────

    /// <summary>随包 rpfm_cli.exe / 环境变量 / 开发机兜底。</summary>
    public static string? LocateCli(params string[] extraCandidates)
    {
        var cands = new List<string>();
        cands.AddRange(extraCandidates);
        if (Environment.GetEnvironmentVariable("RPFM_CLI_PATH") is { Length: > 0 } env) cands.Add(env);
        cands.Add(Path.Combine(AppContext.BaseDirectory, "rpfm", CliExeName));
        cands.Add(@"E:\AAA战锤工作区\rpfm-v4.74\rpfm_cli.exe");   // 开发机
        return cands.FirstOrDefault(File.Exists);
    }

    /// <summary>随包 schema。</summary>
    public static string? LocateSchema(params string[] extraCandidates)
    {
        var cands = new List<string>();
        cands.AddRange(extraCandidates);
        if (Environment.GetEnvironmentVariable("RPFM_SCHEMA_PATH") is { Length: > 0 } env) cands.Add(env);
        cands.Add(Path.Combine(AppContext.BaseDirectory, "schemas", SchemaFileName));
        cands.Add(@"E:\AAA战锤工作区\tools\warband-tree-studio\schemas\schema_wh3.ron");   // 开发机
        return cands.FirstOrDefault(File.Exists);
    }

    // ── 小工具 ────────────────────────────────────────────────────

    /// <summary>按路径猜文件类型（给文件树上的 [DB]/[Image] 角标用）。</summary>
    public static string GuessFileType(string path)
    {
        var p = path.Replace('\\', '/').ToLowerInvariant();
        if (p.StartsWith("db/") || p.Contains("/db/")) return p.EndsWith(".loc") ? "Loc" : "DB";
        if (p.EndsWith(".loc")) return "Loc";
        if (p.EndsWith(".png") || p.EndsWith(".dds") || p.EndsWith(".jpg")) return "Image";
        if (p.EndsWith(".twui") || p.EndsWith(".xml")) return "Text";
        if (p.EndsWith(".lua") || p.EndsWith(".txt") || p.EndsWith(".csv")) return "Text";
        if (p.StartsWith("ui/")) return "UI";
        return "?";
    }

    private static string QuoteIfNeeded(string s) => s.Contains(' ') ? '"' + s + '"' : s;

    private static string Trim(string s) => s.Length > 400 ? s[..400] + "…" : s;
}
