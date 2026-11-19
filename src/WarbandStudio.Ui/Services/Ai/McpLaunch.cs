using System.IO;
using System.IO.Pipes;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using WarbandStudio.Ui.ViewModels;

namespace WarbandStudio.Ui.Services.Ai;

internal static class McpLaunch
{
    internal static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var connect = Array.IndexOf(args, "--connect");
            if (connect >= 0)
            {
                if (connect + 1 >= args.Length || !args[connect + 1].StartsWith("warband-studio-", StringComparison.Ordinal))
                    throw new ArgumentException("--connect requires a WarbandStudio pipe name.");
                await BridgeAsync(args[connect + 1]);
                return 0;
            }
            var saved = AppSettings.Load();
            var settings = new AppSettings
            {
                GameDir = saved.GameDir, BackupDir = saved.BackupDir, GroupKeyPrefix = saved.GroupKeyPrefix,
                FirstRunDone = true, TourDone = true, PersistChanges = false, AutoSave = false
            };
            var game = Array.IndexOf(args, "--game-dir");
            if (game >= 0)
            {
                if (game + 1 >= args.Length) throw new ArgumentException("--game-dir requires a path.");
                settings.GameDir = args[game + 1];
            }
            var vm = new MainViewModel(settings);
            await using var session = new AiSession(vm, ownsBackend: true);
            using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            await using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
            await new McpConnection(session).RunAsync(input, output);
            return 0;
        }
        catch (Exception ex) { await Console.Error.WriteLineAsync("WarbandStudio MCP: " + ex.Message); return 1; }
    }

    private static async Task BridgeAsync(string name)
    {
        await using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000);
        using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        await using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        var pending = new ConcurrentDictionary<string, byte>();
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inputDone = false;
        using var stop = new CancellationTokenSource();
        async Task SendAsync()
        {
            while (await input.ReadLineAsync(stop.Token) is { } line)
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("id", out var id)) pending.TryAdd(id.GetRawText(), 0);
                await writer.WriteLineAsync(line);
            }
            inputDone = true;
            if (pending.IsEmpty) drained.TrySetResult();
        }
        async Task ReceiveAsync()
        {
            while (await reader.ReadLineAsync(stop.Token) is { } response)
            {
                using var doc = JsonDocument.Parse(response);
                await output.WriteLineAsync(response);
                await output.FlushAsync();
                if (doc.RootElement.TryGetProperty("id", out var id)) pending.TryRemove(id.GetRawText(), out _);
                if (inputDone && pending.IsEmpty) drained.TrySetResult();
            }
            if (!inputDone || !pending.IsEmpty) throw new IOException("Editor connection closed.");
        }
        var send = SendAsync();
        var receive = ReceiveAsync();
        try
        {
            var finished = await Task.WhenAny(send, receive);
            await finished;
            await Task.WhenAny(drained.Task, receive);
            if (receive.IsFaulted) await receive;
            await drained.Task;
        }
        finally
        {
            stop.Cancel();
            try { await Task.WhenAll(send, receive); }
            catch (OperationCanceledException) { }
        }
    }
}
