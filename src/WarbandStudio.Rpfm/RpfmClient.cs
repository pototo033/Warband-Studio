using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace WarbandStudio.Rpfm;

/// <summary>
/// rpfm_server（5.0.6）的 WebSocket 客户端：连接、封套、请求-响应配对。
///
/// 用法：<c>await using var c = new RpfmClient(); await c.ConnectAsync();</c>
/// 具体命令在 <see cref="RpfmClientCommands"/> 里（partial 的另一个文件）。
///
/// 注意实现细节：官方示例的接收循环用的是一次性缓冲，表数据 JSON 动辄几 MB，
/// 会截断 —— 这里按 EndOfMessage 拼帧。
/// </summary>
public sealed partial class RpfmClient : IAsyncDisposable
{
    private readonly ClientWebSocket _ws = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private int _nextId;
    private CancellationTokenSource? _rxCts;
    private Task? _rxLoop;

    /// <summary>连接后服务端主动推的会话号（id=0 的 SessionConnected）。</summary>
    public int? SessionId { get; private set; }

    public bool IsConnected => _ws.State == WebSocketState.Open;

    /// <summary>事件日志（收发原文摘要），Probe 和后续的诊断面板都用它。</summary>
    public event Action<string>? Trace;

    public async Task ConnectAsync(string url = RpfmProtocol.DefaultUrl, CancellationToken ct = default)
    {
        await _ws.ConnectAsync(new Uri(url), ct);
        _rxCts = new CancellationTokenSource();
        _rxLoop = Task.Run(() => ReceiveLoopAsync(_rxCts.Token), CancellationToken.None);
        Trace?.Invoke("connected " + url);
        // 服务端握手后马上会推 SessionConnected；等它一下，别让调用方读到空会话号
        for (var i = 0; i < 50 && SessionId is null; i++) await Task.Delay(20, ct);
    }

    /// <summary>
    /// 把一条命令发出去并等它的响应。命令对象用 <see cref="RpfmProtocol"/> 的三个
    /// 构造函数拼（Unit / Newtype / Tuple），或者直接传 <see cref="JsonElement"/>。
    /// </summary>
    public async Task<JsonElement> SendAsync(object command, CancellationToken ct = default)
    {
        if (!IsConnected) throw new InvalidOperationException("rpfm_server 还没连上。");

        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var envelope = new Dictionary<string, object?> { ["id"] = id, ["data"] = command };
        var json = JsonSerializer.Serialize(envelope, RpfmProtocol.Json);
        Trace?.Invoke($"→ #{id} {Truncate(json)}");
        await _ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct);

        await using var reg = ct.Register(() => tcs.TrySetCanceled(ct)).ConfigureAwait(false);
        return await tcs.Task.ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var acc = new MemoryStream();
        try
        {
            while (_ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                acc.SetLength(0);
                WebSocketReceiveResult r;
                do
                {
                    r = await _ws.ReceiveAsync(buffer, ct);
                    if (r.MessageType == WebSocketMessageType.Close)
                    {
                        Trace?.Invoke("server closed the socket");
                        return;
                    }
                    acc.Write(buffer, 0, r.Count);
                } while (!r.EndOfMessage);

                HandleMessage(acc.ToArray());
            }
        }
        catch (OperationCanceledException) { /* 正常退出 */ }
        catch (WebSocketException e) { Trace?.Invoke("socket error: " + e.Message); }
        finally
        {
            // 连接断了：把所有等着的请求全部失败掉，别让调用方挂死
            foreach (var kv in _pending)
                kv.Value.TrySetException(new RpfmException("与 rpfm_server 的连接已断开。"));
            _pending.Clear();
        }
    }

    private void HandleMessage(byte[] utf8)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(utf8); }
        catch (JsonException e) { Trace?.Invoke("bad json: " + e.Message); return; }

        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("data", out var data)) return;

            // 连接后服务端主动推的会话号（id=0，不参与配对）
            if (root.TryGetProperty("id", out var idEl) && idEl.GetInt32() == 0
                && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("SessionConnected", out var sid))
            {
                SessionId = sid.GetInt32();
                Trace?.Invoke("session " + SessionId);
                return;
            }

            if (!root.TryGetProperty("id", out idEl)) return;
            if (!_pending.TryRemove(idEl.GetInt32(), out var tcs)) return;   // 别人的/过期的

            if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("Error", out var err))
                tcs.TrySetException(new RpfmException(err.GetString() ?? "未知错误"));
            else
                tcs.TrySetResult(data.Clone());   // Clone：doc 一会儿就 Dispose 了
        }
    }

    /// <summary>正常道别（服务端就不用在 5 分钟超时里干等），然后关 socket。</summary>
    public async Task DisconnectAsync()
    {
        try
        {
            if (IsConnected) await SendAsync(RpfmProtocol.Unit("ClientDisconnecting"));
        }
        catch { /* 走人就别管回包了 */ }
        try
        {
            _rxCts?.Cancel();
            if (_ws.State == WebSocketState.Open)
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        }
        catch { }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _ws.Dispose();
        _rxCts?.Dispose();
    }

    private static string Truncate(string s, int max = 220) =>
        s.Length <= max ? s : s[..max] + "…(" + s.Length + ")";
}
