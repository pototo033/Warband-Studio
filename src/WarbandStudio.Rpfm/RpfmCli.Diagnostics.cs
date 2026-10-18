using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WarbandStudio.Rpfm;

public sealed partial class RpfmCli
{
    private readonly SemaphoreSlim _diagnosticGate = new(1, 1);
    public string DiagnosticCacheDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WarbandStudio", "cache", "rpfm");

    public static string ParseDiagnosticsJson(string stdout)
    {
        var start = stdout.IndexOf('{');
        if (start < 0) throw new RpfmException("诊断未完成：RPFM 未返回 JSON 报告。");
        try
        {
            using var doc = JsonDocument.Parse(stdout[start..]);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("results", out var results)
                || results.ValueKind != JsonValueKind.Array) throw new JsonException("缺少 results 数组");
            return doc.RootElement.GetRawText();
        }
        catch (JsonException ex) { throw new RpfmException("诊断未完成：RPFM 报告格式错误：" + ex.Message); }
    }

    private string DiagnosticFingerprint()
    {
        if (string.IsNullOrWhiteSpace(GamePath) || !Directory.Exists(Path.Combine(GamePath, "data")))
            throw new RpfmException("诊断未完成：请先设置有效的游戏目录。");
        var stamp = new StringBuilder("rpfm-managed-cache-v1\n");
        void FileStamp(string path)
        {
            var file = new FileInfo(Path.GetFullPath(path));
            stamp.Append(file.FullName).Append('|').Append(file.Exists ? file.Length : -1).Append('|')
                .Append(file.Exists ? file.LastWriteTimeUtc.Ticks : 0).AppendLine();
        }
        FileStamp(ExePath);
        stamp.Append(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(SchemaPath))));
        var data = Path.GetFullPath(Path.Combine(GamePath!, "data"));
        var manifest = Path.Combine(data, "manifest.txt");
        if (File.Exists(manifest))
        {
            stamp.Append(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(manifest))));
            foreach (var name in File.ReadLines(manifest).Select(l => l.Split('\t')[0]).Where(n => n.EndsWith(".pack", StringComparison.OrdinalIgnoreCase))
                         .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            {
                var path = Path.GetFullPath(Path.Combine(data, name));
                if (!path.StartsWith(data + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new RpfmException("游戏清单中的 Pack 路径无效。");
                FileStamp(path);
            }
        }
        else foreach (var file in Directory.EnumerateFiles(data, "*.pack").OrderBy(p => p, StringComparer.OrdinalIgnoreCase)) FileStamp(file);
        var raw = Path.Combine(GamePath!, "assembly_kit", "raw_data", "db");
        stamp.Append(raw).Append(Directory.Exists(raw));
        if (Directory.Exists(raw)) foreach (var file in Directory.EnumerateFiles(raw, "*.xml").OrderBy(p => p, StringComparer.OrdinalIgnoreCase)) FileStamp(file);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stamp.ToString()))).ToLowerInvariant();
    }

    private static bool CompleteCache(string path) => new[] { "pak1", "pak2", "pak3" }
        .All(ext => File.Exists(Path.ChangeExtension(path, ext)) && new FileInfo(Path.ChangeExtension(path, ext)).Length > 0);

    private async Task<string> EnsureCacheCoreAsync(bool force, CancellationToken ct)
    {
        var fingerprint = DiagnosticFingerprint();
        var scope = Path.Combine(Path.GetFullPath(DiagnosticCacheDirectory), fingerprint);
        Directory.CreateDirectory(scope);
        if (!force)
            foreach (var generation in Directory.EnumerateDirectories(scope, "generation_*").OrderByDescending(p => p, StringComparer.Ordinal))
            {
                var cache = Path.Combine(generation, "dependencies_wh3.pak2");
                if (File.Exists(Path.Combine(generation, "ready")) && CompleteCache(cache))
                {
                    DependenciesCachePath = cache;
                    Log?.Invoke("RPFM 诊断缓存命中（CLI/schema/游戏/AK 来源一致）。");
                    return cache;
                }
            }
        var output = Path.Combine(scope, "generation_" + DateTime.UtcNow.Ticks + "_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        var target = Path.Combine(output, "dependencies_wh3.pak2");
        Log?.Invoke("RPFM：使用当前内置 CLI 生成独立诊断缓存，不复用外部 RPFM 缓存。");
        var result = await GenerateDependenciesAsync(target, ct);
        if (!result.Ok || !CompleteCache(target)) throw new RpfmException("诊断未完成：依赖缓存生成失败。" + result.All);
        if (DiagnosticFingerprint() != fingerprint) throw new RpfmException("诊断未完成：生成期间游戏或 schema 来源发生变化，请重试。");
        await File.WriteAllTextAsync(Path.Combine(output, "ready"), fingerprint, ct);
        DependenciesCachePath = target;
        return target;
    }

    public async Task<string> EnsureDiagnosticCacheAsync(bool force = false, CancellationToken ct = default)
    {
        await _diagnosticGate.WaitAsync(ct);
        try { return await EnsureCacheCoreAsync(force, ct); }
        finally { _diagnosticGate.Release(); }
    }

    public async Task<CliResult> DiagnoseAsync(IEnumerable<string> packPaths, CancellationToken ct = default)
    {
        var packs = packPaths.Select(Path.GetFullPath).ToArray();
        if (packs.Length == 0 || packs.Any(p => !File.Exists(p))) throw new RpfmException("诊断未完成：待诊断 Pack 不存在。");
        await _diagnosticGate.WaitAsync(ct);
        try
        {
            await EnsureCacheCoreAsync(false, ct);
            var result = await DiagnoseFilesAsync(packs, ct);
            if (!result.Ok && result.All.Contains("io error", StringComparison.OrdinalIgnoreCase))
            {
                Log?.Invoke("RPFM 读取诊断缓存失败，重建本工具缓存后重试一次。");
                await EnsureCacheCoreAsync(true, ct);
                result = await DiagnoseFilesAsync(packs, ct);
            }
            if (!result.Ok) throw new RpfmException("诊断未完成：" + result.All);
            var json = ParseDiagnosticsJson(result.StdOut);
            return result with { StdOut = json };
        }
        finally { _diagnosticGate.Release(); }
    }
}
