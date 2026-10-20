using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WarbandStudio.Ui.Services;

namespace WarbandStudio.Ui.Tests;

public sealed class UpdaterTests
{
    private static readonly string[] Required = ["WarbandStudio.exe", "WarbandStudio.dll", "WarbandStudio.deps.json", "WarbandStudio.runtimeconfig.json",
        "WarbandStudio.Core.dll", "WarbandStudio.Pack.dll", "WarbandStudio.Packfile.dll", "WarbandStudio.Rpfm.dll", "WarbandStudio.Editors.dll",
        "Web/canvas.html", "schemas/schema_wh3.ron", "update_repo.txt"];
    private readonly string _root;
    public UpdaterTests()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "WarbandStudio.slnx"))) root = root.Parent;
        _root = Path.Combine(root!.FullName, ".tmp", "updater-v1.7.1", "cases", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "install"));
    }
    [Theory]
    [InlineData("1.8", "1.7", "1.8", false, false)]
    [InlineData("1.8", "1.7", "1.8", true, true)]
    [InlineData("1.8", "1.7", "v1.8", true, true)]
    [InlineData("1.7.0", "1.7", "", true, false)]
    [InlineData("1.7.1", "1.7", "", false, true)]
    [InlineData("1.6.9", "1.7", "", true, false)]
    [InlineData("1.8-rc1", "1.7", "", true, false)]
    public void ManualCheckOverridesSkipButNeverOffersSameOrOlderVersion(string latest, string current, string skipped, bool manual, bool expected)
        => Assert.Equal(expected, UpdatePackage.ShouldOffer(latest, current, skipped, manual));

    [Fact]
    public void ReleaseSelectsOnlyApplicationAssetFromConfiguredRepository()
    {
        var metadata = JsonSerializer.Serialize(new { tag_name = "v1.8", assets = new[] { new { name = "WarbandStudio_v1.8_app.zip", browser_download_url = "https://github.com/owner/repo/releases/download/v1.8/WarbandStudio_v1.8_app.zip", size = 100, digest = "sha256:abc" } } });
        Assert.Equal("1.8", UpdatePackage.ParseRelease(metadata, "owner/repo").Version);
        Assert.Throws<InvalidDataException>(() => UpdatePackage.ParseRelease(metadata.Replace("github.com/owner", "example.com/owner"), "owner/repo"));
        Assert.Throws<InvalidDataException>(() => UpdatePackage.ParseRelease(metadata.Replace("_app.zip", "_full.zip"), "owner/repo"));
    }

    private static byte[] Zip(string? extra = null, bool omitSchema = false)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        {
            foreach (var name in Required.Where(n => !omitSchema || n != "schemas/schema_wh3.ron").Concat(extra is null ? [] : [extra]))
            { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write("new:" + name); }
        }
        return buffer.ToArray();
    }
    private sealed class ResponseHandler(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
    }
    private async Task<UpdatePlan> Prepare(byte[] zip, string? digest = null, long? size = null, CancellationToken ct = default)
    {
        using var http = new HttpClient(new ResponseHandler(zip));
        return await UpdatePackage.PrepareAsync(new("1.8", new("https://github.com/owner/repo/releases/download/v1.8/WarbandStudio_v1.8_app.zip"), size ?? zip.Length,
            digest ?? "sha256:" + Convert.ToHexString(SHA256.HashData(zip))), Path.Combine(_root, "cache"), Path.Combine(_root, "install"), http, ct: ct);
    }
    [Fact]
    public async Task DownloadAndStageLeaveInstalledApplicationAndUserPacksUntouched()
    {
        var exe = Path.Combine(_root, "install", "WarbandStudio.exe"); File.WriteAllText(exe, "old");
        var packs = Path.Combine(_root, "install", "packs"); Directory.CreateDirectory(packs); File.WriteAllText(Path.Combine(packs, "user.pack"), "user-data");
        var plan = await Prepare(Zip());
        Assert.Equal("old", File.ReadAllText(exe)); Assert.Equal("user-data", File.ReadAllText(Path.Combine(packs, "user.pack")));
        Assert.Equal(Required.Length, plan.Files.Length);
        Assert.All(plan.Files, f => Assert.Equal(f.Sha256, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(UpdatePackage.Under(plan.PayloadDirectory, f.Path))))));
    }
    [Theory]
    [InlineData("../outside.dll")]
    [InlineData("Web/../../outside.dll")]
    [InlineData("packs/user.pack")]
    [InlineData("Web/canvas.html")]
    [InlineData("Web/canvas.html:stream")]
    public async Task UnsafeOrDuplicateZipEntriesCannotReachInstallation(string extra)
    {
        var sentinel = Path.Combine(_root, "install", "WarbandStudio.exe"); File.WriteAllText(sentinel, "old");
        await Assert.ThrowsAsync<InvalidDataException>(() => Prepare(Zip(extra)));
        Assert.Equal("old", File.ReadAllText(sentinel)); Assert.False(File.Exists(Path.Combine(_root, "outside.dll")));
    }
    [Fact]
    public async Task MissingSchemaHashMismatchAndTruncatedDownloadCannotInstall()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => Prepare(Zip(omitSchema:true)));
        var zip = Zip();
        // 各失败阶段使用独立目录，不能因前一次失败留下的 zip 影响断言。
        using var http = new HttpClient(new ResponseHandler(zip));
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdatePackage.PrepareAsync(new("1.8", new("https://example.invalid/file.zip"), zip.Length, "sha256:0000"), Path.Combine(_root,"bad-hash"), Path.Combine(_root,"install"), http));
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdatePackage.PrepareAsync(new("1.8", new("https://example.invalid/file.zip"), zip.Length + 1, null), Path.Combine(_root,"bad-size"), Path.Combine(_root,"install"), http));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => UpdatePackage.PrepareAsync(new("1.8", new("https://example.invalid/file.zip"), zip.Length, null), Path.Combine(_root,"cancelled"), Path.Combine(_root,"install"), http, ct:new CancellationToken(true)));
    }
    private async Task<int> RunInstaller(UpdatePlan plan)
    {
        var cache = Path.Combine(_root, "cache");
        var path = UpdatePackage.WriteInstaller(plan with { ParentPid = -1, Restart = false }, cache);
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(cache,"apply-update.ps1"), "-PlanPath", path }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        finally { if (!process.HasExited) process.Kill(); }
        File.WriteAllText(Path.Combine(_root, "helper-output.log"), await stdout + await stderr);
        return process.ExitCode;
    }
    [Fact]
    public async Task RealWindowsHelperInstallsOnlyPayloadAndCleansDownloadAfterSuccess()
    {
        var plan = await Prepare(Zip());
        File.WriteAllText(Path.Combine(plan.InstallDirectory, "WarbandStudio.exe"), "old-exe");
        Directory.CreateDirectory(Path.Combine(plan.InstallDirectory, "packs"));
        var userPack = Path.Combine(plan.InstallDirectory, "packs", "user.pack"); File.WriteAllText(userPack, "user-data");
        var recovery = Path.Combine(_root,"recovery","pending-session.json");
        Directory.CreateDirectory(Path.GetDirectoryName(recovery)!); File.WriteAllText(recovery,"unsaved edits");
        Assert.Equal(0, await RunInstaller(plan));
        Assert.Equal("new:WarbandStudio.exe", File.ReadAllText(Path.Combine(plan.InstallDirectory, "WarbandStudio.exe")));
        Assert.Equal("user-data", File.ReadAllText(userPack));
        Assert.Equal("unsaved edits",File.ReadAllText(recovery));
        Assert.False(Directory.Exists(plan.PayloadDirectory)); Assert.False(Directory.Exists(plan.BackupDirectory));
        Assert.True(JsonDocument.Parse(File.ReadAllText(plan.ResultPath)).RootElement.GetProperty("Success").GetBoolean());
    }
    [Fact]
    public async Task LockedFileMidInstallRestoresEarlierFilesAndRetainsFailureLog()
    {
        var plan = await Prepare(Zip());
        foreach (var file in plan.Files)
        {
            var path = UpdatePackage.Under(plan.InstallDirectory, file.Path); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "old:" + file.Path);
        }
        var locked = Path.Combine(plan.InstallDirectory, "WarbandStudio.dll");
        using (File.Open(locked, FileMode.Open, FileAccess.Read, FileShare.Read)) Assert.Equal(1, await RunInstaller(plan));
        Assert.Equal("old:WarbandStudio.exe", File.ReadAllText(Path.Combine(plan.InstallDirectory, "WarbandStudio.exe")));
        Assert.Equal("old:WarbandStudio.dll", File.ReadAllText(locked));
        Assert.True(Directory.Exists(plan.BackupDirectory));
        using var result = JsonDocument.Parse(File.ReadAllText(plan.ResultPath));
        Assert.False(result.RootElement.GetProperty("Success").GetBoolean());
        Assert.True(File.Exists(result.RootElement.GetProperty("LogPath").GetString()));
    }
    [Fact]
    public async Task ChangedStagedFileIsRejectedBeforeTouchingAnyInstalledFile()
    {
        var plan = await Prepare(Zip());
        var exe = Path.Combine(plan.InstallDirectory, "WarbandStudio.exe"); File.WriteAllText(exe, "old");
        File.WriteAllText(UpdatePackage.Under(plan.PayloadDirectory, "WarbandStudio.dll"), "damaged");
        Assert.Equal(1, await RunInstaller(plan)); Assert.Equal("old", File.ReadAllText(exe));
    }
}
