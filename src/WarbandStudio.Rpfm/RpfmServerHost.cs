using System.Diagnostics;
using System.Text.Json;

namespace WarbandStudio.Rpfm;

/// <summary>
/// rpfm_server 进程的生命周期：探测 →（必要时）拉起 → 等就绪。
///
/// 踩过的坑：本机可能已经有一个 rpfm_server 在跑（比如 RPFM GUI 自带的后端），
/// 直接再起一个会 bind 失败（os error 10048）。所以先探测 45127 上的 /version，
/// 有活的就复用，绝不硬起。
/// </summary>
public sealed class RpfmServerHost
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private Process? _owned;   // 只有我们拉起来的进程才由我们负责关

    public int Port { get; }
    public string? Version { get; private set; }
    public int? Pid { get; private set; }

    public RpfmServerHost(int port = RpfmProtocol.DefaultPort) => Port = port;

    /// <summary>探测已在跑的 server；活着返回版本号，没活返回 null。</summary>
    public async Task<string?> ProbeAsync(CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.GetStringAsync($"http://127.0.0.1:{Port}/version", ct);
            using var doc = JsonDocument.Parse(resp);
            Version = doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
            Pid = doc.RootElement.TryGetProperty("pid", out var p) ? p.GetInt32() : null;
            return Version;
        }
        catch
        {
            Version = null;
            Pid = null;
            return null;
        }
    }

    /// <summary>
    /// 确保有一个可连的 server：先探测，没有就用 <paramref name="serverExe"/> 拉起，
    /// 最多等 <paramref name="waitSeconds"/> 秒。返回版本号。
    /// </summary>
    public async Task<string> EnsureRunningAsync(string serverExe, int waitSeconds = 20,
                                                 CancellationToken ct = default)
    {
        if (await ProbeAsync(ct) is { } alive) return alive;

        if (!File.Exists(serverExe))
            throw new FileNotFoundException(
                "找不到 rpfm_server.exe，无法启动 rpfm 后端。", serverExe);

        var psi = new ProcessStartInfo(serverExe)
        {
            WorkingDirectory = Path.GetDirectoryName(serverExe)!,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        _owned = Process.Start(psi);

        var deadline = DateTime.UtcNow.AddSeconds(waitSeconds);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(400, ct);
            if (await ProbeAsync(ct) is { } v) return v;
            if (_owned is { HasExited: true }) break;
        }
        throw new RpfmException("rpfm_server 启动后一直没在 45127 上响应。");
    }

    /// <summary>只关我们自己拉起来的那个进程（复用别人的 server 时不动它）。</summary>
    public void StopIfOwned()
    {
        try
        {
            if (_owned is { HasExited: false }) _owned.Kill(entireProcessTree: true);
        }
        catch { /* 关不掉就算了 */ }
        _owned = null;
    }

    // 找 rpfm_server.exe 的活交给 WarbandStudio.Core.RpfmFinder（候选列表只此一份），
    // 调用方算好路径直接丢给 EnsureRunningAsync 即可。
}
