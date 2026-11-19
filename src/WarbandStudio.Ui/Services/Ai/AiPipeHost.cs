using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Text;

namespace WarbandStudio.Ui.Services.Ai;

public sealed class AiPipeHost(AiSession session) : IAsyncDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<NamedPipeServerStream, Task> _clients = new();
    private Task? _accept;
    public string PipeName => "warband-studio-" + session.Id;
    public void Start() => _accept ??= AcceptAsync();

    private async Task AcceptAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try { await pipe.WaitForConnectionAsync(_shutdown.Token); }
            catch (OperationCanceledException) { await pipe.DisposeAsync(); break; }
            _clients[pipe] = ServeAsync(pipe);
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe)
    {
        try
        {
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true);
            await new McpConnection(session).RunAsync(reader, writer, _shutdown.Token);
        }
        catch (IOException) { }
        catch (OperationCanceledException) { }
        catch (Exception ex) { FileLog.Write("AI connection failed", ex); }
        finally { _clients.TryRemove(pipe, out _); await pipe.DisposeAsync(); }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        if (_accept is not null) await _accept;
        var clients = _clients.ToArray();
        foreach (var client in clients) await client.Key.DisposeAsync();
        await Task.WhenAll(clients.Select(c => c.Value));
        await session.DisposeAsync();
        _shutdown.Dispose();
    }
}
