using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace WarbandStudio.Ui.Services;

public sealed record UpdateRelease(string Version, Uri Download, long Size, string? Digest);
public sealed record UpdateFile(string Path, string Sha256);
public sealed record UpdatePlan(int ParentPid, string InstallDirectory, string PayloadDirectory,
    string BackupDirectory, string ResultPath, string Version, UpdateFile[] Files, bool Restart = true);

/// <summary>下载和验证阶段完全不改安装目录；退出后的替换由独立助手完成。</summary>
public static class UpdatePackage
{
    private static readonly string[] RequiredFiles = ["WarbandStudio.exe", "WarbandStudio.dll", "WarbandStudio.deps.json",
        "WarbandStudio.runtimeconfig.json", "WarbandStudio.Core.dll", "WarbandStudio.Pack.dll", "WarbandStudio.Packfile.dll",
        "WarbandStudio.Rpfm.dll", "WarbandStudio.Editors.dll", "Web/canvas.html", "schemas/schema_wh3.ron", "update_repo.txt"];

    public static UpdateRelease ParseRelease(string json, string repo)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean() || root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean())
            throw new InvalidDataException("更新来源不是正式版本。");
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var version = tag.TrimStart('v', 'V');
        if (!System.Version.TryParse(version, out _)) throw new InvalidDataException("更新版本号无效。");
        var assetName = $"WarbandStudio_v{version}_app.zip";
        var assets = root.GetProperty("assets").EnumerateArray().Where(a => a.GetProperty("name").GetString() == assetName).ToArray();
        if (assets.Length != 1) throw new InvalidDataException("此版本没有唯一的程序更新包：" + assetName);
        var asset = assets[0];
        var uri = new Uri(asset.GetProperty("browser_download_url").GetString()!);
        var expected = new Uri($"https://github.com/{repo}/releases/download/{Uri.EscapeDataString(tag)}/{assetName}");
        if (uri != expected) throw new InvalidDataException("程序包下载地址与更新仓库不一致。");
        var size = asset.GetProperty("size").GetInt64();
        if (size <= 0 || size > 256L * 1024 * 1024) throw new InvalidDataException("程序更新包大小无效。");
        return new(version, uri, size, asset.TryGetProperty("digest", out var digest) ? digest.GetString() : null);
    }

    public static bool ShouldOffer(string latest, string current, string skipped, bool manual)
    {
        if (!System.Version.TryParse(latest.TrimStart('v', 'V'), out var next) || !System.Version.TryParse(current, out var installed)) return false;
        static System.Version Normalize(System.Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));
        return Normalize(next) > Normalize(installed) && (manual || !latest.Equals(skipped.TrimStart('v', 'V'), StringComparison.OrdinalIgnoreCase));
    }

    public static string Under(string root, string relative)
    {
        root = System.IO.Path.GetFullPath(root).TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar;
        var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新包路径越界：" + relative);
        for (var item = path; item is not null && item.Length >= root.TrimEnd('\\').Length; item = System.IO.Path.GetDirectoryName(item))
            if ((File.Exists(item) || Directory.Exists(item)) && (File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("更新路径包含链接：" + item);
        return path;
    }

    public static async Task<UpdatePlan> PrepareAsync(UpdateRelease release, string cache, string install, HttpClient http,
        IProgress<(long Received, long Total)>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(cache);
        var archivePath = Under(cache, "application.zip");
        using (var response = await http.GetAsync(release.Download, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } length && length != release.Size) throw new InvalidDataException("下载大小与发布附件不一致。");
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            await using var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
            var buffer = new byte[65536]; long received = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                received += read;
                if (received > release.Size) throw new InvalidDataException("下载超过预期大小。");
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                progress?.Report((received, release.Size));
            }
            if (received != release.Size) throw new InvalidDataException("更新包未下载完整。");
        }
        if (!string.IsNullOrEmpty(release.Digest))
        {
            if (!release.Digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("不支持的更新包校验格式。");
            await using var stream = File.OpenRead(archivePath);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
            if (!hash.Equals(release.Digest[7..], StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新包 SHA256 校验失败。");
        }
        var payload = Under(cache, "payload"); Directory.CreateDirectory(payload);
        var files = new List<UpdateFile>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long expanded = 0;
        using (var zip = ZipFile.OpenRead(archivePath))
            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                var name = entry.FullName.Replace('\\', '/');
                if (name.EndsWith('/')) { _ = Under(payload, name); continue; }
                if (name.Split('/').Any(p => p is "" or "." or "..") || name.Contains(':') || name.StartsWith('/') || !seen.Add(name)) throw new InvalidDataException("更新包包含非法或重复路径：" + name);
                var rootFile = !name.Contains('/') && (RequiredFiles.Contains(name, StringComparer.OrdinalIgnoreCase) || name.StartsWith("WarbandStudio.", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
                if (!rootFile && !name.StartsWith("Web/", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("schemas/", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新包包含不属于程序的文件：" + name);
                if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || (expanded += entry.Length) > 512L * 1024 * 1024) throw new InvalidDataException("更新包格式或解压大小无效。");
                var dest = Under(payload, name); _ = Under(install, name);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest)!);
                await using (var input = entry.Open())
                await using (var output = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true)) await input.CopyToAsync(output, ct);
                await using var verify = File.OpenRead(dest);
                files.Add(new(name, Convert.ToHexString(await SHA256.HashDataAsync(verify, ct))));
            }
        if (RequiredFiles.Any(f => !seen.Contains(f))) throw new InvalidDataException("程序更新包缺少必要文件，不能安装。");
        // 权限问题在退出前发现，不能关掉程序才发现目录无法写入。
        var probe = Under(install, ".studio-update-" + Guid.NewGuid().ToString("N"));
        using (File.Create(probe)) { } File.Delete(probe);
        return new(Environment.ProcessId, System.IO.Path.GetFullPath(install), payload, Under(cache, "backup"),
            System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(cache))!, "last-result.json"), release.Version, files.ToArray());
    }

    public static string WriteInstaller(UpdatePlan plan, string cache)
    {
        var planPath = Under(cache, "plan.json");
        File.WriteAllText(planPath, JsonSerializer.Serialize(plan));
        using var script = typeof(UpdatePackage).Assembly.GetManifestResourceStream("WarbandStudio.Ui.Updates.apply-update.ps1")
            ?? throw new InvalidOperationException("内置更新助手缺失。");
        using var reader = new StreamReader(script);
        File.WriteAllText(Under(cache, "apply-update.ps1"), reader.ReadToEnd(), new System.Text.UTF8Encoding(true));
        return planPath;
    }
}
