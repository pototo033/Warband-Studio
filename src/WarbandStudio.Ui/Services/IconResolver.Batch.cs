using WarbandStudio.Packfile;

namespace WarbandStudio.Ui.Services;

public sealed partial class IconResolver
{
    private int _batchDepth;
    private readonly Dictionary<string, PackArchive> _batchArchives = new(StringComparer.OrdinalIgnoreCase);
    public IDisposable BeginBatch()
    {
        lock (_gate) { _batchDepth++; return new Batch(this); }
    }
    private byte[]? ReadGameBytes(string path, string inner)
    {
        lock (_gate)
        {
            if (_batchDepth == 0)
            {
                using var archive = PackArchive.Open(path);
                return archive.Find(inner) is { } entry ? archive.ReadDecoded(entry) : null;
            }
            if (!_batchArchives.TryGetValue(path, out var cached)) _batchArchives[path] = cached = PackArchive.Open(path);
            return cached.Find(inner) is { } hit ? cached.ReadDecoded(hit) : null;
        }
    }
    private sealed class Batch(IconResolver owner) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            lock (owner._gate)
            {
                if (_disposed) return;
                _disposed = true;
                if (--owner._batchDepth != 0) return;
                foreach (var archive in owner._batchArchives.Values) archive.Dispose();
                owner._batchArchives.Clear();
            }
        }
    }
}
