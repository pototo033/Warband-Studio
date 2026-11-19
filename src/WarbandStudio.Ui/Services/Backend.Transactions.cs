using System.Text.Json;
using WarbandStudio.Pack;

namespace WarbandStudio.Ui.Services;

public sealed partial class Backend
{
    private int _editDepth;

    public T Edit<T>(Func<T> action)
    {
        lock (StateSync)
        {
            if (_editDepth > 0) return action();
            _editDepth++;
            var key = Key;
            var before = Edits.Clone();
            var previousUndo = _undoByPack.TryGetValue(key, out var undo) ? undo.ToList() : [];
            var previousRedo = _redoByPack.TryGetValue(key, out var redo) ? redo.ToList() : [];
            var previousClipboard = _pageCopy;
            var revision = _rev;
            try
            {
                PushUndo();
                var result = action();
                var options = new JsonSerializerOptions { IncludeFields = true };
                if (JsonSerializer.Serialize(before, options) == JsonSerializer.Serialize(Edits, options))
                {
                    _undoByPack[key] = previousUndo;
                    _redoByPack[key] = previousRedo;
                }
                else if (_rev == revision) InvalidateCanvas();
                return result;
            }
            catch
            {
                if (key.Length == 0) _fallbackEdits = before;
                else _editsByPack[key] = before;
                _undoByPack[key] = previousUndo;
                _redoByPack[key] = previousRedo;
                _pageCopy = previousClipboard;
                InvalidateCanvas();
                throw;
            }
            finally { _editDepth--; }
        }
    }
}
