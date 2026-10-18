using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace WarbandStudio.Ui.ViewModels;

public sealed partial class MainViewModel
{
    private readonly ConcurrentDictionary<string, long> _readRequests = new();
    private async Task<T> ReadCurrentAsync<T>(Func<T> read, [CallerMemberName] string slot = "")
    {
        var request = _readRequests.AddOrUpdate(slot, 1, (_, prior) => prior + 1);
        var context = _backend.CaptureReadContext();
        var result = await Task.Run(() => _backend.Read(context, read));
        if (!_backend.IsCurrent(context) || _readRequests[slot] != request)
            throw new OperationCanceledException("已有新的读取请求，丢弃旧结果。");
        return result;
    }
    private string _updateStatus = "";
    public string UpdateStatus { get => _updateStatus; private set => Set(ref _updateStatus, value); }
}
