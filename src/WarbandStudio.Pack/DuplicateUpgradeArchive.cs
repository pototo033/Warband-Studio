using System.Security.Cryptography;
using System.Text.Json;
using WarbandStudio.Packfile;

namespace WarbandStudio.Pack;

/// <summary>兼容层是保存时的派生物；重开只还原内存视图，不把内部成本、桥接路线当成作者编辑。</summary>
public static class DuplicateUpgradeArchive
{
    public const string ManifestPath = "warband-studio/duplicate-upgrades.v1.json";
    public sealed record FileState(byte[]? Original, string CompiledHash, byte[]? Compiled = null);
    public sealed class Manifest
    {
        public int Version { get; set; } = 1;
        public string Namespace { get; set; } = "";
        public string? ProjectKey { get; set; }
        public Dictionary<string, FileState> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Choices { get; set; } = new();
    }
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    public static string Id(string value) => Hash(System.Text.Encoding.UTF8.GetBytes(value))[..16].ToLowerInvariant();

    public static Manifest? Read(PackArchive pack)
    {
        if (pack.Find(ManifestPath) is not { } entry) return null;
        Manifest? m;
        try { m = JsonSerializer.Deserialize<Manifest>(pack.ReadDecoded(entry)); }
        catch (JsonException) { return null; }
        if (m is null) return null;
        if (m.Version != 1 || m.Files is null || m.Choices is null || m.Namespace is null || m.Files.Count > 10000 ||
            m.Namespace.Length>0 && !System.Text.RegularExpressions.Regex.IsMatch(m.Namespace,"^[0-9a-f]{16}$"))
            return null;
        try { m.Files = new(m.Files, StringComparer.OrdinalIgnoreCase); }
        catch (ArgumentException) { return null; }
        foreach (var (file, state) in m.Files.ToArray())
        {
            if (state is null || string.IsNullOrEmpty(state.CompiledHash)) return null;
            if (state.Compiled is not null && !Hash(state.Compiled).Equals(state.CompiledHash, StringComparison.OrdinalIgnoreCase))
                m.Files[file] = state with { Compiled = null };
        }
        return m;
    }

    public static PackArchive OpenView(string path, IReadOnlyDictionary<string, byte[]>? pending = null, IEnumerable<string>? removed = null,
        IList<string>? recoveryNotes = null)
    {
        using var raw = PackArchive.Open(path);
        var replacements = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var drops = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var metadata=Read(raw);
        var noteStart = recoveryNotes?.Count ?? 0;
        Schema? recoverySchema = null;
        var keepCurrent = false;
        if (metadata is { } manifest)
        {
            foreach (var (file, state) in manifest.Files)
            {
                // 外部编辑过编译产物时不能用旧快照覆盖；保留原包并明确指出需要恢复的文件。
                var entry = raw.Find(file);
                var current = entry is null ? null : raw.ReadDecoded(entry);
                // 外部删除即为当前设计，不从旧快照复活该文件。
                if (current is null) { drops.Add(file); continue; }
                if (Hash(current) != state.CompiledHash)
                {
                    if (current is not null && file.StartsWith("db/", StringComparison.OrdinalIgnoreCase) && state.Compiled is not null)
                    {
                        try
                        {
                            recoverySchema ??= Schema.Load(WarbandStudio.Rpfm.RpfmCli.LocateSchema()
                                ?? throw new InvalidDataException("没有可用schema。"));
                            var logical = DuplicateUpgradeDbRecovery.Rebase(file, state, current, recoverySchema);
                            if (logical is null) drops.Add(file); else replacements[file] = logical;
                            recoveryNotes?.Add("已按当前表保留外部增删和改值：" + file);
                            continue;
                        }
                        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or NotSupportedException)
                        { /* 无法反向解释时仍允许打开，整体以当前 Pack 为准。 */ }
                    }
                    if (file.Equals("ui/campaign ui/warband_upgrades.twui.xml", StringComparison.OrdinalIgnoreCase) &&
                        state.Original is not null && current is not null &&
                        TabFactionVisibility.TryRebaseCompiled(state.Original, current, state.CompiledHash, out var rebased))
                    {
                        replacements[file] = rebased;
                        recoveryNotes?.Add("已保留外部修改的页签派系/种族显示范围，并恢复兼容逻辑视图；原包未写入：" + file);
                        continue;
                    }
                    if (file.Equals(DuplicateUpgradeCompiler.UiPath, StringComparison.OrdinalIgnoreCase) &&
                        state.Original is not null && state.Compiled is not null &&
                        DuplicateUpgradeUiRecovery.TryRebase(state.Original, state.Compiled, current, out var currentUi))
                    {
                        replacements[file] = currentUi;
                        continue;
                    }
                    keepCurrent = true;
                    while (recoveryNotes is not null && recoveryNotes.Count > noteStart) recoveryNotes.RemoveAt(recoveryNotes.Count - 1);
                    recoveryNotes?.Add("已按当前 Pack 原样读取，不套用旧快照：" + file);
                    break;
                }
                if (state.Original is null) drops.Add(file); else replacements[file] = state.Original;
            }
            drops.Add(ManifestPath);
        }
        // 不允许一半还原旧来源、一半保留新内部引用；来源不足时整包保持当前内容。
        if (keepCurrent) { replacements.Clear(); drops.Clear(); }
        foreach (var (file, bytes) in pending ?? new Dictionary<string, byte[]>()) { replacements[file] = bytes; drops.Remove(file); }
        drops.UnionWith(removed ?? []);
        return PackArchive.OpenView(path, replacements, drops);
    }

    public static void Capture(PackArchive authored, Dictionary<string, byte[]> derived, IReadOnlyDictionary<string, string> choices,string ns,string? projectKey)
    {
        var manifest = new Manifest { Choices = new(choices),Namespace=ns,ProjectKey=projectKey };
        foreach (var (path, bytes) in derived)
        {
            var before = authored.Find(path) is { } entry ? authored.ReadDecoded(entry) : null;
            if (before is not null && before.AsSpan().SequenceEqual(bytes)) continue;
            // 内部生成数据的说明随当前包携带，只用于解释差分，不作为打开/替换工程的限制。
            manifest.Files[path] = new(before, Hash(bytes), path.StartsWith("db/", StringComparison.OrdinalIgnoreCase)
                || path.Equals(DuplicateUpgradeCompiler.UiPath, StringComparison.OrdinalIgnoreCase) ? bytes : null);
        }
        if (manifest.Files.Count > 0) derived[ManifestPath] = JsonSerializer.SerializeToUtf8Bytes(manifest);
    }
}
