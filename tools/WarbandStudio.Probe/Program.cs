using System.Text;
using System.Text.Json;
using WarbandStudio.Rpfm;

// 阶段 0 的验证工具：直接对着 rpfm_server 5.0.6 跑一遍工具要用到的每条路径，
// 也当以后的冒烟测试用。
//
//   probe version
//   probe tree <pack> [过滤词]
//   probe tsv <pack> <pack内路径> <输出.tsv>
//   probe schema <表名>
//   probe refs <pack> <要找的值> <表:列[,表:列…]>
//   probe diag <pack>
//   probe saveas <pack> <目标.pack>
//   probe roundtrip <pack> <pack内路径> <工作目录>

Console.OutputEncoding = Encoding.UTF8;

if (args.Length == 0) { Usage(); return 1; }

var verbose = Environment.GetEnvironmentVariable("PROBE_TRACE") == "1";

try
{
    return args[0].ToLowerInvariant() switch
    {
        "version"   => await Version(),
        "cmd"       => await Cmd(args),
        "ensure-schema" => await EnsureSchema(),
        "game-detect" => await GameDetect(args),
        "native-list" => await NativeList(args),
        "native-extract" => await NativeExtract(args),
        "native-tsv" => await NativeTsv(args),
        "loc" => await LocDump(args),
        "native-tsv-all" => await NativeTsvAll(args),
        "native-roundtrip" => await NativeRoundTrip(args),
        "table-rows" => await TableRows(args),
        "native-encode-file" => await NativeEncodeFile(args),
        "export-unlock" => await ExportUnlock(args),
        "refcheck" => await RefCheck(args),
        "export-edit" => await ExportEdit(args),
        "amend-test" => WarbandStudio.Probe.AmendTestRunner.Run(args),
        "milgroups" => WarbandStudio.Probe.MilGroups.Run(args),
        "tidy-overrides" => WarbandStudio.Probe.TidyOverrides.Run(args),
        "tab-split" => WarbandStudio.Probe.TabSplit.Run(args),
        "new-tab" => WarbandStudio.Probe.NewTabFix.Run(args),
        "add-cost" => WarbandStudio.Probe.AddCost.Run(args),
        "set-file" => WarbandStudio.Probe.SetFile.Run(args),
        "dedupe-grants" => WarbandStudio.Probe.DedupeGrants.Run(args),
        "tab-art" => WarbandStudio.Probe.TabArtCheck.Run(args),
        "native-write-pack" => await NativeWritePack(args),
        "tree"      => await Tree(args),
        "tsv"       => await Tsv(args),
        "schema"    => await Schema(args),
        "refs"      => await Refs(args),
        "diag"      => await Diag(args),
        "saveas"    => await SaveAs(args),
        "roundtrip" => await RoundTrip(args),
        _           => Unknown(),
    };
}
catch (RpfmException e)
{
    Console.Error.WriteLine("[rpfm 报错] " + e.Message);
    return 2;
}

void Usage()
{
    Console.WriteLine("""
        阶段 0 验证工具（对 rpfm_server 5.0.6）

          probe version
          probe ensure-schema
          probe cmd <命令名> [JSON载荷]
          probe tree <pack> [过滤词]
          probe tsv <pack> <pack内路径> <输出.tsv>
          probe schema <表名>
          probe refs <pack> <要找的值> <表:列[,表:列…]>
          probe diag <pack>
          probe saveas <pack> <目标.pack>
          probe roundtrip <pack> <pack内路径> <工作目录>
          probe table-rows <pack> <表名> <组合键列（逗号分隔）> [原版.pack]
          probe tidy-overrides <pack> [out.pack]
          probe tab-split <pack> [out.pack] --key Yukino [--drop <包内路径>]... [--dry-run]
          probe new-tab <pack> <KEY> [--donor MOD2] [--bg 路径] [--btn 路径] [--vanilla db.pack] [--out out.pack] [--empty-group]

        环境变量 PROBE_TRACE=1 打印每条 WS 收发。
        """);
}

int Unknown()
{
    Console.Error.WriteLine("不认识的命令：" + args[0]);
    Usage();
    return 1;
}

async Task<(RpfmServerHost Host, RpfmClient Client)> Start(bool needGame = true)
{
    var host = new RpfmServerHost();
    var exe = WarbandStudio.Core.RpfmFinder.FindExisting(
                  WarbandStudio.Core.RpfmFinder.Candidates(
                      settingsPath: Environment.GetEnvironmentVariable("RPFM_SERVER_PATH"),
                      installDirs: WarbandStudio.Core.RpfmFinder.CommonInstallDirs(),
                      extraFallbacks:
                      [
                          @"E:\AAA战锤工作区\rpfm\rpfm_server.exe",
                          System.IO.Path.Combine(AppContext.BaseDirectory, "rpfm", "rpfm_server.exe"),
                      ]))
              ?? throw new FileNotFoundException("找不到 rpfm_server.exe（装个 RPFM 5.x，或设 RPFM_SERVER_PATH）");
    var ver = await host.EnsureRunningAsync(exe);
    var client = new RpfmClient();
    if (verbose) client.Trace += s => Console.WriteLine("[ws] " + s);
    await client.ConnectAsync();
    Console.WriteLine($"[i] rpfm_server {ver}（pid {host.Pid}）已连接，会话 {client.SessionId}");
    if (needGame)
    {
        // 协议示例的第一步：先选游戏，server 才会去载入该游戏的 schema
        await client.SetGameAsync();
        Console.WriteLine("[i] 已选游戏 warhammer_3；schema 已加载：" + await client.IsSchemaLoadedAsync());
    }
    return (host, client);
}

async Task<int> Cmd(string[] a)
{
    if (a.Length < 2) { Usage(); return 1; }
    var (host, client) = await Start();
    object command = a.Length > 2
        ? RpfmProtocol.Newtype(a[1], JsonSerializer.Deserialize<JsonElement>(a[2]))
        : RpfmProtocol.Unit(a[1]);
    var data = await client.SendAsync(command);
    var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
    Console.WriteLine(json.Length > 4000 ? json[..4000] + "\n…（截断）" : json);
    await client.DisconnectAsync();
    host.StopIfOwned();
    return 0;
}

async Task<int> Version()
{
    var (host, client) = await Start();
    await client.DisconnectAsync();
    host.StopIfOwned();
    return 0;
}

async Task<int> EnsureSchema()
{
    var (host, client) = await Start(needGame: false);
    var folder = await SchemaInstaller.GetSchemasFolderAsync(client);
    Console.WriteLine("[i] server 的 schema 目录：" + folder);
    Console.WriteLine("[i] 随包 schema：" + Path.Combine(SchemaInstaller.BundledFolder, "schema_wh3.ron"));

    var rep = await SchemaInstaller.EnsureAsync(client);
    Console.WriteLine($"[{(rep.Loaded ? "✓" : "!")}] {rep.Result}：{rep.Message}");
    Console.WriteLine($"[i] 目标文件：{rep.SchemaFile}（存在={File.Exists(rep.SchemaFile)}）");

    await client.DisconnectAsync();
    host.StopIfOwned();
    return rep.Loaded ? 0 : 4;
}

/// <summary>游戏目录探测（纯逻辑那半，不读注册表）：probe game-detect [Steam安装目录]</summary>
async Task<int> GameDetect(string[] a)
{
    await Task.CompletedTask;

    string? steam = a.Length > 1 ? a[1] : null;
    if (steam is null)
    {
        string[] guesses =
        [
            @"E:\steam", @"C:\Program Files (x86)\Steam", @"C:\Steam",
            @"D:\Steam", @"E:\Steam", @"D:\Program Files (x86)\Steam",
        ];
        steam = guesses.FirstOrDefault(Directory.Exists);
    }
    Console.WriteLine("[i] Steam 安装目录：" + (steam ?? "(没找到)"));

    var libraries = new List<string>();
    if (steam is not null)
    {
        var vdf = System.IO.Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        Console.WriteLine("[i] libraryfolders.vdf：" + vdf + "（存在=" + File.Exists(vdf) + "）");
        if (File.Exists(vdf))
        {
            libraries = WarbandStudio.Core.GameFinder.ParseSteamLibraries(File.ReadAllText(vdf));
            Console.WriteLine("[i] 解析出 " + libraries.Count + " 个库：");
            foreach (var l in libraries) Console.WriteLine("      " + l);
        }
    }

    var candidates = WarbandStudio.Core.GameFinder
        .CandidatesFor(steam, libraries)
        .Concat(WarbandStudio.Core.GameFinder.DefaultDriveCandidates())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    Console.WriteLine($"[i] 候选 {candidates.Count} 个，命中的：");
    var hits = candidates.Where(WarbandStudio.Core.GameFinder.LooksLikeGame).ToList();
    foreach (var h in hits) Console.WriteLine("      ✓ " + h);
    if (hits.Count == 0) Console.WriteLine("      （一个都没命中）");
    return hits.Count > 0 ? 0 : 5;
}

/// <summary>原生读 pack：native-list &lt;pack&gt; [--plain]（--plain 只打印路径，方便和 CLI 的 list 对拍）</summary>
async Task<int> NativeList(string[] a)
{
    await Task.CompletedTask;
    if (a.Length < 2) { Usage(); return 1; }
    var plain = a.Contains("--plain");
    var sw = System.Diagnostics.Stopwatch.StartNew();
    using var pack = WarbandStudio.Packfile.PackArchive.Open(Path.GetFullPath(a[1]));
    sw.Stop();

    if (plain || a.Contains("--sizes"))
    {
        // --plain：全量路径（和 RPFM 自己的文件列表语义一致，不含 *.rpfm_reserved 内部文件）
        // --sizes：全量路径 + 存储大小（两个包做差异对比用：大小不同 = 内容多半不同）
        var withSizes = a.Contains("--sizes");
        foreach (var e in pack.VisibleEntries)
            Console.WriteLine(withSizes ? $"{e.Path}\t{e.StoredSize}" : e.Path);
        return 0;
    }

    Console.WriteLine($"[i] {Path.GetFileName(pack.FilePath)}：{pack.Version} · {pack.FileType} · 标志 0x{(uint)pack.Flags:X4}");
    Console.WriteLine($"[i] 文件 {pack.Entries.Count} 个，依赖 {pack.Dependencies.Count} 个，数据区起点 {pack.DataStart}");
    Console.WriteLine($"[i] 读取耗时 {sw.Elapsed.TotalMilliseconds:F0} ms");
    var compressed = pack.Entries.Count(e => e.IsCompressed);
    Console.WriteLine($"[i] 其中压缩条目 {compressed} 个");
    foreach (var e in pack.Entries.Take(8)) Console.WriteLine("      " + e);
    if (pack.Entries.Count > 8) Console.WriteLine($"      …还有 {pack.Entries.Count - 8} 个");
    return 0;
}

/// <summary>原生抽取一个文件（自动解压）：native-extract &lt;pack&gt; &lt;包内路径&gt; &lt;输出文件&gt;</summary>
async Task<int> NativeExtract(string[] a)
{
    await Task.CompletedTask;
    if (a.Length < 4) { Usage(); return 1; }
    using var pack = WarbandStudio.Packfile.PackArchive.Open(Path.GetFullPath(a[1]));
    var entry = pack.Find(a[2]) ?? throw new FileNotFoundException("包里没有：" + a[2]);
    var bytes = pack.ReadDecoded(entry);
    var outPath = Path.GetFullPath(a[3]);
    Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
    File.WriteAllBytes(outPath, bytes);
    Console.WriteLine($"[✓] {entry.Path}（存储 {entry.StoredSize}B{(entry.IsCompressed ? "，压缩" : "")}）" +
                      $" → {outPath}（解开后 {bytes.Length}B）");
    return 0;
}

/// <summary>原生解码一张表并导出 TSV（与 rpfm_cli 的导出对拍用）：native-tsv &lt;pack&gt; &lt;包内路径&gt; &lt;输出.tsv&gt;</summary>
async Task<int> NativeTsv(string[] a)
{
    await Task.CompletedTask;
    if (a.Length < 4) { Usage(); return 1; }

    var schemaPath = WarbandStudio.Rpfm.RpfmCli.LocateSchema()
                     ?? throw new FileNotFoundException("找不到 schema_wh3.ron。");
    var swSchema = System.Diagnostics.Stopwatch.StartNew();
    var schema = WarbandStudio.Packfile.Schema.Load(schemaPath);
    swSchema.Stop();

    var inner = a[2].Replace('\\', '/');
    using var pack = WarbandStudio.Packfile.PackArchive.Open(Path.GetFullPath(a[1]));
    var entry = pack.Find(inner) ?? throw new FileNotFoundException("包里没有：" + inner);
    var bytes = pack.ReadDecoded(entry);

    var parts = inner.Split('/');
    var tableName = parts.Length > 1 ? parts[1] : throw new InvalidDataException("看不出来这是哪张表：" + inner);

    var sw = System.Diagnostics.Stopwatch.StartNew();
    var table = WarbandStudio.Packfile.DbTable.Decode(bytes, tableName, schema);
    sw.Stop();

    var outPath = Path.GetFullPath(a[3]);
    Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
    File.WriteAllText(outPath, table.ToTsv(inner), new System.Text.UTF8Encoding(false));

    Console.WriteLine($"[i] schema 载入 {swSchema.Elapsed.TotalMilliseconds:F0} ms（{schema.Definitions.Count} 张表）");
    Console.WriteLine($"[✓] {tableName} v{table.Version}：{table.Rows.Count} 行 × {table.Columns.Count} 列，" +
                      $"解码 {sw.Elapsed.TotalMilliseconds:F1} ms → {outPath}");
    Console.WriteLine("[i] 列名：" + string.Join(", ", table.Columns.Select(c => c.Name).Take(10)));
    return 0;
}

/// <summary>批量：把包里 db/ 下所有表原生解码成 TSV：native-tsv-all &lt;pack&gt; &lt;输出目录&gt; [过滤词]</summary>
async Task<int> LocDump(string[] a)
{
    await Task.CompletedTask;
    if (a.Length < 3) { Usage(); return 1; }
    using var pack = WarbandStudio.Packfile.PackArchive.Open(Path.GetFullPath(a[1]));
    var inner = a[2];
    var needle = a.Length > 3 ? a[3] : null;
    var entry = pack.Find(inner) ?? pack.VisibleEntries.FirstOrDefault(e =>
        e.Path.EndsWith(inner, StringComparison.OrdinalIgnoreCase));
    if (entry is null) { Console.Error.WriteLine("包里没有：" + inner); return 3; }
    var bytes = pack.ReadDecoded(entry);
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var map = WarbandStudio.Packfile.LocFile.Read(bytes);
    sw.Stop();
    if (map is null) { Console.Error.WriteLine("不是 loc 文件（头部不对）"); return 4; }
    Console.WriteLine($"[\u2713] {entry.Path}：{map.Count} 条（{bytes.Length} 字节，解 {sw.ElapsedMilliseconds} ms）");
    var shown = 0;
    foreach (var kv in map)
    {
        if (needle is not null && !kv.Key.Contains(needle, StringComparison.OrdinalIgnoreCase)) continue;
        Console.WriteLine($"    {kv.Key} = {kv.Value}");
        if (++shown >= 40) { Console.WriteLine("    …"); break; }
    }
    return 0;
}

async Task<int> NativeTsvAll(string[] a)
{
    await Task.CompletedTask;
    if (a.Length < 3) { Usage(); return 1; }

    var schemaPath = WarbandStudio.Rpfm.RpfmCli.LocateSchema() ?? throw new FileNotFoundException("找不到 schema。");
    var swSchema = System.Diagnostics.Stopwatch.StartNew();
    var schema = WarbandStudio.Packfile.Schema.Load(schemaPath);
    swSchema.Stop();

    var outRoot = Path.GetFullPath(a[2]);
    var filter = a.Length > 3 ? a[3] : null;
    using var pack = WarbandStudio.Packfile.PackArchive.Open(Path.GetFullPath(a[1]));

    var targets = pack.VisibleEntries
        .Where(e => e.Path.StartsWith("db/", StringComparison.OrdinalIgnoreCase))
        // 单表多文件也认（MOD 常把一张表拆成 <表名>/<自定义名>，文件名不一定叫 data__）
        .Where(e => filter is null || e.Path.Contains(filter, StringComparison.OrdinalIgnoreCase))
        .ToList();

    var sw = System.Diagnostics.Stopwatch.StartNew();
    var ok = 0;
    var failures = new List<string>();
    foreach (var e in targets)
    {
        var parts = e.Path.Split('/');
        var tableName = parts.Length > 1 ? parts[1] : "?";
        try
        {
            var bytes = pack.ReadDecoded(e);
            var table = WarbandStudio.Packfile.DbTable.Decode(bytes, tableName, schema);
            var outPath = Path.Combine(outRoot, e.Path.Replace('/', Path.DirectorySeparatorChar) + ".tsv");
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            File.WriteAllText(outPath, table.ToTsv(e.Path), new System.Text.UTF8Encoding(false));
            ok++;
        }
        catch (Exception ex)
        {
            failures.Add($"{e.Path}  ←  {ex.GetType().Name}: {ex.Message}");
        }
    }
    sw.Stop();

    Console.WriteLine($"[i] schema 载入 {swSchema.Elapsed.TotalMilliseconds:F0} ms");
    Console.WriteLine($"[✓] 解出 {ok}/{targets.Count} 张表，用时 {sw.Elapsed.TotalSeconds:F1}s" +
                      $"（均 {(ok > 0 ? sw.Elapsed.TotalMilliseconds / ok : 0):F1} ms/表）");
    foreach (var f in failures.Take(12)) Console.WriteLine("      ✗ " + f);
    if (failures.Count > 12) Console.WriteLine($"      …共 {failures.Count} 张失败");
    return failures.Count == 0 ? 0 : 6;
}

/// <summary>导出（含画布拖动）：export-edit &lt;pack&gt; &lt;原版db.pack&gt; &lt;目标&gt; &lt;组key&gt; &lt;x&gt; &lt;y&gt;</summary>
async Task<int> ExportEdit(string[] a)
{
    await Task.CompletedTask;
    if (a.Length < 7) { Usage(); return 1; }
    var schema = WarbandStudio.Packfile.Schema.Load(WarbandStudio.Rpfm.RpfmCli.LocateSchema()!);
    var edits = new WarbandStudio.Pack.WarbandEdits();
    edits.InfoEdits[a[4]] = (int.Parse(a[5]), int.Parse(a[6]), null);
    var rep = WarbandStudio.Pack.WarbandExporter.Export(
        Path.GetFullPath(a[1]), Path.GetFullPath(a[2]), Path.GetFullPath(a[3]), schema, edits);
    Console.WriteLine($"[✓] 导出（含 1 处拖动）：{rep.DestPath}");
    foreach (var e in rep.AddedEntries) Console.WriteLine("      新增 " + e);
    return 0;
}

/// <summary>引用体检：refcheck &lt;pack&gt; [原版db.pack]</summary>
async Task<int> RefCheck(string[] a)
{
    await Task.CompletedTask;
    if (a.Length < 2) { Usage(); return 1; }
    var schema = WarbandStudio.Packfile.Schema.Load(WarbandStudio.Rpfm.RpfmCli.LocateSchema()!);
    var notes = new List<string>();
    using var pack = WarbandStudio.Packfile.PackArchive.Open(Path.GetFullPath(a[1]));
    WarbandStudio.Packfile.PackArchive? van = a.Length > 2 ? WarbandStudio.Packfile.PackArchive.Open(Path.GetFullPath(a[2])) : null;

    string[] tables = ["unit_upgrade_groups_tables", "unit_to_unit_group_junctions_tables",
        "unit_upgrade_group_ui_infos_tables", "unit_upgrade_group_ui_categories_tables",
        "unit_upgrade_group_ui_links_tables", "unit_upgrade_to_unit_groups_tables",
        "unit_upgrade_to_tech_requirements_tables", "resource_costs_tables",
        "resource_cost_pooled_resource_junctions_tables", "campaign_features_tables",
        "ui_features_to_cultures_tables", "units_to_groupings_military_permissions_tables",
        "units_to_exclusive_faction_permissions_tables", "main_units_tables", "land_units_tables"];

    WarbandStudio.Packfile.DbTable? Resolve(string name)
    {
        // schema 的 is_reference 里表名不带 _tables 后缀（如 main_units），补上再找
        var candidates = name.EndsWith("_tables", StringComparison.OrdinalIgnoreCase)
            ? new[] { name }
            : new[] { name + "_tables", name };
        foreach (var n in candidates)
        {
            try
            {
                // 体检要的是"本包 + 原版"的并集（只取本包会把原版兵全判成没来源）
                var t = van is null ? null : WarbandStudio.Packfile.TableFiles.ReadMerged(van, n, schema, notes);
                t = WarbandStudio.Packfile.TableFiles.Merge(t, WarbandStudio.Packfile.TableFiles.ReadMerged(pack, n, schema, notes));
                if (t is not null) return t;
            }
            catch (Exception ex)
            {
                notes.Add($"{n} 解析不了，按缺来源处理：{ex.Message}");
                return null;
            }
        }
        return null;
    }

    var rep = WarbandStudio.Packfile.ReferenceChecker.Check(tables, schema, Resolve, notes);
    Console.WriteLine($"[✓] 引用体检：查了 {rep.Checked} 处引用，解析不了 {rep.Issues.Count} 处" +
                      (rep.MissingTargets.Count > 0 ? $"，目标表缺来源 {rep.MissingTargets.Distinct().Count()} 张" : ""));
    foreach (var i in rep.Issues.Take(10)) Console.WriteLine($"      ✗ {i.Table}.{i.Column} = {i.Value} → {i.TargetTable}({i.TargetColumn})");
    if (rep.Issues.Count > 10) Console.WriteLine($"      …还有 {rep.Issues.Count - 10} 处");
    foreach (var m in rep.MissingTargets.Distinct().Take(6)) Console.WriteLine("      目标表缺来源：" + m);
    foreach (var n in notes.Take(5)) Console.WriteLine("      · " + n);
    return rep.Issues.Count == 0 ? 0 : 9;
}

/// <summary>导出（精英解锁）：export-unlock &lt;pack&gt; &lt;原版db.pack&gt; &lt;目标pack&gt;</summary>
async Task<int> ExportUnlock(string[] a)
{
    await Task.CompletedTask;
    if (a.Length < 4) { Usage(); return 1; }
    var schema = WarbandStudio.Packfile.Schema.Load(WarbandStudio.Rpfm.RpfmCli.LocateSchema()!);
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var rep = WarbandStudio.Pack.WarbandExporter.Export(
        Path.GetFullPath(a[1]), Path.GetFullPath(a[2]), Path.GetFullPath(a[3]), schema);
    sw.Stop();
    Console.WriteLine($"[✓] 导出完成：{rep.DestPath}（{sw.ElapsedMilliseconds} ms）");
    Console.WriteLine($"[i] 树里 {rep.UnitsInTree} 个兵：解锁 {rep.Unlocked}，本来就解锁 {rep.AlreadyUnlocked}，原版表里没有 {rep.MissingInVanilla}");
    foreach (var e in rep.AddedEntries) Console.WriteLine("      新增 " + e);
    foreach (var n in rep.Notes.Take(6)) Console.WriteLine("      · " + n);
    return 0;
}

/// <summary>
/// 对比同一张表三种读法的行数：table-rows &lt;pack&gt; &lt;表名&gt; &lt;组合键列（逗号分隔）&gt; [原版.pack]
/// 用来盯住"键不唯一的表被按键合并静默吃行"：组合键读必须 ≥ 按键合并读。
/// </summary>
async Task<int> TableRows(string[] a)
{
    await Task.CompletedTask;
    if (a.Length < 4) { Usage(); return 1; }
    var schema = WarbandStudio.Packfile.Schema.Load(RpfmCli.LocateSchema()!);
    var notes = new List<string>();
    using var pack = WarbandStudio.Packfile.PackArchive.Open(Path.GetFullPath(a[1]));
    var table = a[2];
    var keyCols = a[3].Split(',', StringSplitOptions.RemoveEmptyEntries);
    var merged = WarbandStudio.Packfile.TableFiles.ReadMerged(pack, table, schema, notes);
    var concat = WarbandStudio.Packfile.TableFiles.ReadConcat(pack, table, schema, notes);
    var comp = WarbandStudio.Packfile.TableFiles.ReadComposite(pack, table, keyCols, schema, notes);
    Console.WriteLine($"[i] {table}（本包）: 按键合并 {merged?.Rows.Count ?? 0} 行 / 拼接读 {concat?.Rows.Count ?? 0} 行 / " +
                      $"组合键({string.Join('+', keyCols)}) {comp?.Rows.Count ?? 0} 行");
    // 传了第 5 个参数（原版包）就再对比一次"原版打底 + 本包覆盖"的两种合并方式 ——
    // 吃行的坑在**跨包 Merge** 这一步：按键 Merge 会用本包的行顶掉原版同一个 key 的其它行。
    if (a.Length >= 5 && File.Exists(a[4]))
    {
        using var basePack = WarbandStudio.Packfile.PackArchive.Open(Path.GetFullPath(a[4]));
        var bMerged = WarbandStudio.Packfile.TableFiles.ReadMerged(basePack, table, schema, notes);
        var bComp = WarbandStudio.Packfile.TableFiles.ReadComposite(basePack, table, keyCols, schema, notes);
        var m2 = WarbandStudio.Packfile.TableFiles.Merge(bMerged, merged);
        var c2 = WarbandStudio.Packfile.TableFiles.MergeComposite(bComp, comp, keyCols);
        Console.WriteLine($"[i] {table}（原版打底 + 本包）: 按键合并 {m2?.Rows.Count ?? 0} 行 / " +
                          $"组合键({string.Join('+', keyCols)}) {c2?.Rows.Count ?? 0} 行");
    }
    foreach (var n in notes.Take(3)) Console.WriteLine("      · " + n);
    return 0;
}

/// <summary>把解码后的原始字节与重编码结果各存一个文件（对照用）：native-encode-file &lt;pack&gt; &lt;包内路径&gt; &lt;输出前缀&gt;</summary>
async Task<int> NativeEncodeFile(string[] a)
{
    await Task.CompletedTask;
    if (a.Length < 4) { Usage(); return 1; }
    var schema = WarbandStudio.Packfile.Schema.Load(WarbandStudio.Rpfm.RpfmCli.LocateSchema()!);
    using var pack = WarbandStudio.Packfile.PackArchive.Open(Path.GetFullPath(a[1]));
    var rel = a[2].Replace('\\', '/');
    var entry = pack.Find(rel) ?? throw new FileNotFoundException(rel);
    var original = pack.ReadDecoded(entry);
    var table = WarbandStudio.Packfile.DbTable.Decode(original, WarbandStudio.Pack.PackSession.TableNameOf(rel), schema);
    var again = WarbandStudio.Packfile.DbEncoder.Encode(table);
    File.WriteAllBytes(Path.GetFullPath(a[3] + ".orig.bin"), original);
    File.WriteAllBytes(Path.GetFullPath(a[3] + ".mine.bin"), again);
    Console.WriteLine($"[i] {rel}：原 {original.Length}B / 重编 {again.Length}B（差 {again.Length - original.Length}）；guid='{table.Guid}' v{table.Version} 行 {table.Rows.Count}");
    return 0;
}

/// <summary>离线往返验收：解表 → 再编码 → 与原始字节比。native-roundtrip &lt;pack&gt; [过滤词]</summary>
async Task<int> NativeRoundTrip(string[] a)
{
    await Task.CompletedTask;
    if (a.Length < 2) { Usage(); return 1; }
    var schema = WarbandStudio.Packfile.Schema.Load(WarbandStudio.Rpfm.RpfmCli.LocateSchema()!);
    var filter = a.Length > 2 ? a[2] : null;
    using var pack = WarbandStudio.Packfile.PackArchive.Open(Path.GetFullPath(a[1]));

    var targets = pack.VisibleEntries
        .Where(e => e.Path.StartsWith("db/", StringComparison.OrdinalIgnoreCase))
        .Where(e => e.Path.EndsWith("/data__", StringComparison.OrdinalIgnoreCase))
        .Where(e => filter is null || e.Path.Contains(filter, StringComparison.OrdinalIgnoreCase))
        .ToList();

    var sw = System.Diagnostics.Stopwatch.StartNew();
    var ok = 0;
    var skipped = 0;
    var bad = new List<string>();
    var schemaGaps = new List<string>();
    foreach (var e in targets)
    {
        try
        {
            var original = pack.ReadDecoded(e);
            var table = WarbandStudio.Packfile.DbTable.Decode(original, WarbandStudio.Pack.PackSession.TableNameOf(e.Path), schema);
            var again = WarbandStudio.Packfile.DbEncoder.Encode(table);
            if (original.AsSpan().SequenceEqual(again)) ok++;
            else
            {
                var at = 0;
                while (at < Math.Min(original.Length, again.Length) && original[at] == again[at]) at++;
                bad.Add($"{e.Path}（原 {original.Length}B / 重编 {again.Length}B，首差 @{at}）");
            }
        }
        catch (Exception ex) when (ex is InvalidDataException &&
                                   (ex.Message.Contains("schema 里没有表") || ex.Message.Contains("没有版本")))
        {
            // schema 里没定义这张表、或没定义这个**版本**（游戏更新后会出现新版本表，
            // 例如 2026-10 更新带来的 initiative_sets_tables v9）—— rpfm_cli 同样认不了，
            // 按既有约定跳过、不算失败，但把清单打出来，免得"悄悄少查了几张"。
            skipped++;
            schemaGaps.Add(e.Path);
        }
        catch (Exception ex) { bad.Add($"{e.Path} ← {ex.GetType().Name}: {ex.Message}"); }
    }
    sw.Stop();
    Console.WriteLine($"[✓] 往返字节一致 {ok}/{targets.Count - skipped}（另 {skipped} 张 schema 无定义，跳过），用时 {sw.Elapsed.TotalSeconds:F1}s");
    foreach (var b in bad.Take(10)) Console.WriteLine("      ✗ " + b);
    if (bad.Count > 10) Console.WriteLine($"      …共 {bad.Count} 张不一致");
    foreach (var g in schemaGaps.Take(8))
        Console.WriteLine($"      · schema 认不了（跳过，不算失败）：{g}");
    return bad.Count == 0 ? 0 : 7;
}

/// <summary>写包验收：native-write-pack &lt;src.pack&gt; &lt;dest.pack&gt; [要重编码的表路径]</summary>
async Task<int> NativeWritePack(string[] a)
{
    await Task.CompletedTask;
    if (a.Length < 3) { Usage(); return 1; }
    var src = Path.GetFullPath(a[1]);
    var dest = Path.GetFullPath(a[2]);
    var reencode = a.Length > 3 ? a[3] : null;

    using var pack = WarbandStudio.Packfile.PackArchive.Open(src);
    var replacements = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
    if (reencode is not null)
    {
        var schema = WarbandStudio.Packfile.Schema.Load(WarbandStudio.Rpfm.RpfmCli.LocateSchema()!);
        var bytes = pack.ReadDecoded(reencode);
        var table = WarbandStudio.Packfile.DbTable.Decode(bytes, WarbandStudio.Pack.PackSession.TableNameOf(reencode), schema);
        replacements[reencode] = WarbandStudio.Packfile.DbEncoder.Encode(table);
        Console.WriteLine($"[i] 重编码 {reencode}：{table.Rows.Count} 行 × {table.Columns.Count} 列 → {replacements[reencode].Length}B");
    }

    var sw = System.Diagnostics.Stopwatch.StartNew();
    WarbandStudio.Packfile.PackWriter.WriteFrom(pack, replacements, dest);
    sw.Stop();
    Console.WriteLine($"[✓] 写出 {dest}（{new FileInfo(dest).Length / 1048576.0:F1} MB，{pack.Entries.Count} 条，{sw.ElapsedMilliseconds} ms）");

    // 立刻用原生读回来验一遍：条目数、路径、每条字节都要与源一致（除被替换的）
    using var check = WarbandStudio.Packfile.PackArchive.Open(dest);
    var diff = 0;
    foreach (var e in pack.Entries)
    {
        var c = check.Find(e.Path);
        if (c is null) { diff++; continue; }
        if (replacements.ContainsKey(e.Path)) continue;
        if (!c.IsCompressed.Equals(e.IsCompressed)) { diff++; continue; }
        if (!check.ReadDecoded(c).AsSpan().SequenceEqual(pack.ReadDecoded(e))) diff++;
    }
    Console.WriteLine(diff == 0
        ? "[✓] 读回校验：全部条目字节一致（除被替换的那条）"
        : $"[!] 读回校验发现 {diff} 条不一致");
    return diff == 0 ? 0 : 8;
}

async Task<int> Tree(string[] a)
{
    if (a.Length < 2) { Usage(); return 1; }
    var filter = a.Length > 2 ? a[2] : null;

    var (host, client) = await Start();
    var (key, info) = await client.OpenPackAsync(Path.GetFullPath(a[1]));
    var (_, files) = await client.GetTreeAsync(key);

    Console.WriteLine($"[i] 容器：{info.FileName}  版本 {info.PfhVersion}  压缩 {info.Compress}");
    Console.WriteLine($"[i] 共 {files.Count} 个文件");

    var byTop = files.GroupBy(f => f.Path.Split('/')[0])
                     .OrderByDescending(g => g.Count());
    Console.WriteLine("[i] 顶层目录分布：" + string.Join("  ",
        byTop.Select(g => $"{g.Key}={g.Count()}")));

    var shown = filter is null
        ? files.Where(IsWarbandRelated).ToList()
        : files.Where(f => f.Path.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
    Console.WriteLine(filter is null
        ? $"[i] 战帮相关文件 {shown.Count} 个（加过滤词看别的）"
        : $"[i] 含 \"{filter}\" 的文件 {shown.Count} 个");
    foreach (var f in shown.Take(60))
        Console.WriteLine($"      {f.Path}    [{f.FileType}]");
    if (shown.Count > 60) Console.WriteLine($"      …还有 {shown.Count - 60} 个");

    await client.ClosePackAsync(key);
    await client.DisconnectAsync();
    host.StopIfOwned();
    return 0;
}

async Task<int> Tsv(string[] a)
{
    if (a.Length < 4) { Usage(); return 1; }
    var (host, client) = await Start();
    var (key, _) = await client.OpenPackAsync(Path.GetFullPath(a[1]));
    var dest = Path.GetFullPath(a[3]);
    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

    await client.ExportTsvAsync(key, a[2], dest);
    var lines = File.ReadAllLines(dest);
    Console.WriteLine($"[✓] 导出 {a[2]} → {dest}（{lines.Length} 行，{new FileInfo(dest).Length} 字节）");
    Console.WriteLine("[i] 表头 1：" + (lines.Length > 0 ? lines[0] : ""));
    Console.WriteLine("[i] 表头 2：" + (lines.Length > 1 ? lines[1] : ""));
    if (lines.Length > 2) Console.WriteLine("[i] 首行数据：" + lines[2]);

    await client.ClosePackAsync(key);
    await client.DisconnectAsync();
    host.StopIfOwned();
    return 0;
}

async Task<int> Schema(string[] a)
{
    if (a.Length < 2) { Usage(); return 1; }
    var (host, client) = await Start();
    Console.WriteLine("[i] schema 已加载：" + await client.IsSchemaLoadedAsync());
    var data = await client.DefinitionsByTableNameAsync(a[1]);
    var defs = data.GetProperty("VecDefinition");
    Console.WriteLine($"[✓] 表 {a[1]}：{defs.GetArrayLength()} 个版本");
    foreach (var d in defs.EnumerateArray())
    {
        var fields = d.TryGetProperty("fields", out var f) ? f.GetArrayLength() : -1;
        var names = d.TryGetProperty("fields", out var f2)
            ? string.Join(", ", f2.EnumerateArray().Take(4)
                .Select(x => x.GetProperty("name").GetString()))
            : "";
        Console.WriteLine($"      version={d.GetProperty("version")}  字段 {fields}  前几列：{names}");
    }
    await client.DisconnectAsync();
    host.StopIfOwned();
    return 0;
}

async Task<int> Refs(string[] a)
{
    if (a.Length < 4) { Usage(); return 1; }
    var cols = new Dictionary<string, IEnumerable<string>>();
    foreach (var item in a[3].Split(',', StringSplitOptions.RemoveEmptyEntries))
    {
        var kv = item.Split(':', 2);
        cols[kv[0]] = kv.Length > 1 ? kv[1].Split('|') : ["key"];
    }
    var (host, client) = await Start();
    var (key, _) = await client.OpenPackAsync(Path.GetFullPath(a[1]));
    var hits = await client.SearchReferencesAsync(key, cols, a[2]);
    Console.WriteLine($"[✓] 引用 {a[2]}：{hits.Count} 处");
    foreach (var h in hits.Take(25))
        Console.WriteLine($"      {h.DataSource}  {h.Path}  {h.ColumnName}  " +
                          $"列{h.ColumnNumber} 行{h.RowNumber}");
    if (hits.Count > 25) Console.WriteLine($"      …还有 {hits.Count - 25} 处");
    await client.ClosePackAsync(key);
    await client.DisconnectAsync();
    host.StopIfOwned();
    return 0;
}

async Task<int> Diag(string[] a)
{
    if (a.Length < 2) { Usage(); return 1; }
    var (host, client) = await Start();
    var (key, _) = await client.OpenPackAsync(Path.GetFullPath(a[1]));
    var data = await client.DiagnosticsCheckAsync();
    var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
    Console.WriteLine(json.Length > 3000 ? json[..3000] + "\n…（截断）" : json);
    await client.ClosePackAsync(key);
    await client.DisconnectAsync();
    host.StopIfOwned();
    return 0;
}

async Task<int> SaveAs(string[] a)
{
    if (a.Length < 3) { Usage(); return 1; }
    var src = Path.GetFullPath(a[1]);
    var dest = Path.GetFullPath(a[2]);
    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
    var (host, client) = await Start();
    var (key, _) = await client.OpenPackAsync(src);
    var info = await client.SavePackAsAsync(key, dest);
    Console.WriteLine($"[✓] 另存：{src}（{new FileInfo(src).Length / 1048576.0:F1} MB）" +
                      $" → {dest}（{new FileInfo(dest).Length / 1048576.0:F1} MB）");
    Console.WriteLine($"[i] 容器回来的文件名：{info.FileName}");
    await client.ClosePackAsync(key);
    await client.DisconnectAsync();
    host.StopIfOwned();
    return 0;
}

async Task<int> RoundTrip(string[] a)
{
    if (a.Length < 4) { Usage(); return 1; }
    var work = Path.GetFullPath(a[3]);
    Directory.CreateDirectory(work);
    var inner = a[2];
    var tsvA = Path.Combine(work, "a.tsv");
    var tsvB = Path.Combine(work, "b.tsv");
    var outPack = Path.Combine(work, "roundtrip.pack");

    var (host, client) = await Start();

    // 1) 导出原表
    var (key, _) = await client.OpenPackAsync(Path.GetFullPath(a[1]));
    await client.ExportTsvAsync(key, inner, tsvA);
    Console.WriteLine($"[1] 导出原表 → {tsvA}（{new FileInfo(tsvA).Length} 字节）");

    // 2) 原样导回去（同一份内容），另存成新 pack —— 走一遍完整的编码路径
    await client.ImportTsvAsync(key, inner, tsvA);
    Console.WriteLine("[2] TSV 导回原 pack（同一份内容）");
    await client.SavePackAsAsync(key, outPack);
    Console.WriteLine($"[3] 另存 → {outPack}（{new FileInfo(outPack).Length / 1048576.0:F1} MB）");

    // 3) 重新打开新 pack，再导一次同一张表，逐字节对拍
    await client.ClosePackAsync(key);
    var (key2, _) = await client.OpenPackAsync(outPack);
    await client.ExportTsvAsync(key2, inner, tsvB);
    var same = File.ReadAllBytes(tsvA).SequenceEqual(File.ReadAllBytes(tsvB));
    Console.WriteLine(same
        ? "[✓] 往返后 TSV 逐字节一致 —— 编解码无损"
        : $"[!] 往返后 TSV 不一致：{tsvA} vs {tsvB}（见 diff）");

    await client.ClosePackAsync(key2);
    await client.DisconnectAsync();
    host.StopIfOwned();
    return same ? 0 : 3;
}

static bool IsWarbandRelated(RFileInfo f)
{
    string[] keys =
    [
        "unit_upgrade", "unit_to_unit", "units_to_groupings", "campaign_features",
        "ui_features_to_cultures", "resource_cost", "warband", "main_units", "land_units",
    ];
    return keys.Any(k => f.Path.Contains(k, StringComparison.OrdinalIgnoreCase));
}
