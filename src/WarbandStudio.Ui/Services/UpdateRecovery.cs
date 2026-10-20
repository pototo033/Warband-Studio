using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WarbandStudio.Pack;

namespace WarbandStudio.Ui.Services;

public sealed class RecoveryPack
{
    public string OriginalPath { get; set; } = "";
    public string Baseline { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? ProjectDirectory { get; set; }
    public string? ProjectKey { get; set; }
    public bool IsUnitPack { get; set; }
    public WarbandEdits Edits { get; set; } = new();
    public List<WarbandEdits> Undo { get; set; } = [];
    public List<WarbandEdits> Redo { get; set; } = [];
}

public sealed record RecoveryDocument(string PackPath, string? InnerPath);
public sealed class RecoverySession
{
    public int Format { get; set; } = 1;
    public string Installation { get; set; } = "";
    public int OwnerPid { get; set; } = Environment.ProcessId;
    public long OwnerStart { get; set; } = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
    public string? ProjectDirectory { get; set; }
    public string? ActivePack { get; set; }
    public RecoveryDocument? ActiveDocument { get; set; }
    public List<RecoveryDocument> Documents { get; set; } = [];
    public List<RecoveryPack> Packs { get; set; } = [];
    public Dictionary<string, string> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Independent of update/download cleanup. Only an acknowledged pending pointer is removed.</summary>
public static class UpdateRecovery
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WarbandStudio", "recovery");
    public static readonly JsonSerializerOptions Json = new()
    {
        IncludeFields = true,
        PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate,
    };
    private sealed record Pointer(string Manifest, string Hash, int ClaimPid = 0, long ClaimStart = 0);
    public sealed record Loaded(string Manifest, RecoverySession Session);
    private static string Identity(string installation) => Path.GetFullPath(installation).TrimEnd('\\', '/').ToUpperInvariant();
    private static string Pending(string root, string installation) => UpdatePackage.Under(root,
        "pending-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Identity(installation)))) + ".json");
    public static string Hash(string path) { using var s = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(s)); }

    private static void DurableWrite(string path, byte[] bytes)
    {
        using var s = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        s.Write(bytes); s.Flush(true);
    }

    public static string Save(string root, string installation, RecoverySession session)
    {
        Directory.CreateDirectory(root);
        var pending = Pending(root, installation);
        if (File.Exists(pending)) throw new IOException("仍有未恢复的更新快照，请先恢复后再更新：" + pending);
        session.Installation = Identity(installation);
        var generation = UpdatePackage.Under(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(generation);
        string Copy(string source, string relative)
        {
            var target = UpdatePackage.Under(generation, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using (var input = File.OpenRead(source))
            using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { input.CopyTo(output); output.Flush(true); }
            var hash = Hash(target);
            if (Hash(source) != hash) throw new IOException("快照生成期间来源文件发生变化：" + source);
            session.Files.Add(relative, hash);
            return target;
        }
        var assets = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        string? Asset(string? source)
        {
            if (string.IsNullOrEmpty(source)) return source;
            if (!File.Exists(source))
            {
                if (Path.IsPathRooted(source)) throw new FileNotFoundException("更新保护找不到待保存素材，已停止更新。", source);
                return source; // Internal pack path.
            }
            var full = Path.GetFullPath(source);
            if (!assets.TryGetValue(full, out var saved))
                assets[full] = saved = Copy(full, "assets/" + assets.Count.ToString("D6") + Path.GetExtension(full));
            return saved;
        }
        for (var i = 0; i < session.Packs.Count; i++)
        {
            var pack = session.Packs[i];
            pack.Baseline = "packs/" + i.ToString("D6") + ".pack";
            Copy(pack.OriginalPath, pack.Baseline);
            foreach (var edits in new[] { pack.Edits }.Concat(pack.Undo).Concat(pack.Redo))
            {
                for (var n = 0; n < edits.FileReplacements.Count; n++)
                { var x = edits.FileReplacements[n]; edits.FileReplacements[n] = (x.Target, Asset(x.Source)!); }
                for (var n = 0; n < edits.NewTabs.Count; n++)
                { var x = edits.NewTabs[n]; edits.NewTabs[n] = (x.Key, x.Donor, Asset(x.BgSource), Asset(x.BtnSource)); }
            }
        }
        var manifest = UpdatePackage.Under(generation, "manifest.json");
        DurableWrite(manifest, JsonSerializer.SerializeToUtf8Bytes(session, Json));
        // Round-trip before publishing the pointer; unsupported state cannot trigger an exit.
        _ = ReadManifest(root, manifest, Hash(manifest), installation);
        var temp = pending + "." + Guid.NewGuid().ToString("N");
        DurableWrite(temp, JsonSerializer.SerializeToUtf8Bytes(new Pointer(Path.GetRelativePath(root, manifest), Hash(manifest))));
        File.Move(temp, pending); // Atomic publication, never overwrite another instance's pending session.
        return manifest;
    }

    private static Loaded ReadManifest(string root, string manifest, string hash, string installation)
    {
        manifest = UpdatePackage.Under(root, Path.GetRelativePath(root, manifest));
        if (Hash(manifest) != hash) throw new InvalidDataException("更新恢复清单校验失败，快照已保留：" + manifest);
        var state = JsonSerializer.Deserialize<RecoverySession>(File.ReadAllBytes(manifest), Json)
            ?? throw new InvalidDataException("更新恢复清单为空。");
        if (state.Format != 1 || state.Installation != Identity(installation)) throw new InvalidDataException("恢复快照版本或安装目录不匹配。");
        var generation = Path.GetDirectoryName(manifest)!;
        foreach (var (relative, expected) in state.Files)
            if (Hash(UpdatePackage.Under(generation, relative)) != expected) throw new InvalidDataException("恢复文件校验失败：" + relative);
        foreach (var pack in state.Packs)
            if (!state.Files.ContainsKey(pack.Baseline)) throw new InvalidDataException("恢复快照缺少 Pack 底本。");
        return new(manifest, state);
    }

    public static Loaded? LoadPending(string root, string installation)
    {
        var pending = Pending(root, installation);
        if (!File.Exists(pending)) return null;
        var lockPath = UpdatePackage.Under(root,Path.GetRelativePath(root,pending + ".lock"));
        using var gate = new FileStream(lockPath,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        if (!File.Exists(pending)) return null;
        var pointer = JsonSerializer.Deserialize<Pointer>(File.ReadAllBytes(pending)) ?? throw new InvalidDataException("恢复指针为空。");
        if (Alive(pointer.ClaimPid,pointer.ClaimStart)) return null;
        var result = ReadManifest(root, UpdatePackage.Under(root, pointer.Manifest), pointer.Hash, installation);
        if (Alive(result.Session.OwnerPid,result.Session.OwnerStart)) return null;
        using var current = Process.GetCurrentProcess();
        var claimed = pointer with { ClaimPid = current.Id, ClaimStart = current.StartTime.ToUniversalTime().Ticks };
        var temp = pending + "." + Guid.NewGuid().ToString("N");
        DurableWrite(temp,JsonSerializer.SerializeToUtf8Bytes(claimed));
        File.Move(temp,pending,overwrite:true);
        return result;
    }

    private static bool Alive(int pid,long start)
    {
        if (pid <= 0) return false;
        try { using var p = Process.GetProcessById(pid); return !p.HasExited && p.StartTime.ToUniversalTime().Ticks == start; }
        catch (ArgumentException) { return false; }
    }

    public static string ResolvePack(Loaded loaded, RecoveryPack pack, out bool recoveredCopy)
    {
        var expected = loaded.Session.Files[pack.Baseline];
        recoveredCopy = !File.Exists(pack.OriginalPath) || Hash(pack.OriginalPath) != expected;
        if (!recoveredCopy) return pack.OriginalPath;
        var generation = Path.GetDirectoryName(loaded.Manifest)!;
        var baseline = UpdatePackage.Under(generation,pack.Baseline);
        var working = UpdatePackage.Under(generation,"working/" + Guid.NewGuid().ToString("N") + "/" + Path.GetFileName(pack.OriginalPath));
        Directory.CreateDirectory(Path.GetDirectoryName(working)!);
        File.Copy(baseline,working); // Editable copy; never let a later save mutate the recovery baseline.
        return working;
    }

    public static void Acknowledge(string root, string installation, string manifest)
    {
        var pending = Pending(root, installation);
        if (!File.Exists(pending)) return;
        var pointer = JsonSerializer.Deserialize<Pointer>(File.ReadAllBytes(pending))!;
        if (Path.GetFullPath(UpdatePackage.Under(root,pointer.Manifest)).Equals(Path.GetFullPath(manifest),StringComparison.OrdinalIgnoreCase))
            File.Delete(pending); // Keep base packs, assets and history for recovery/review.
    }
}
