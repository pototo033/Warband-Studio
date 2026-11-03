using WarbandStudio.Pack;

namespace WarbandStudio.Ui.Services;

public sealed partial class Backend
{
    public void SetDuplicateUpgradeChoices(string packPath, IReadOnlyDictionary<string,string> choices)
    {
        lock (StateSync)
        {
            if (!_packs.Any(p => p.IsOpen && string.Equals(p.PackPath,packPath,StringComparison.OrdinalIgnoreCase)))
                throw new OperationCanceledException("规则所属的包已关闭，未应用选择。");
            var current = _editsByPack.TryGetValue(packPath,out var value) ? value : new WarbandEdits();
            var next = current.Clone();
            foreach(var (id,choice) in choices) next.DuplicateUpgradeChoices[id] = choice;
            if (choices.All(p => current.DuplicateUpgradeChoices.GetValueOrDefault(p.Key) == p.Value)) return;
            if (!_undoByPack.TryGetValue(packPath,out var undo)) _undoByPack[packPath] = undo = [];
            undo.Add(current.Clone()); _redoByPack.Remove(packPath); _editsByPack[packPath] = next;
            InvalidateCanvas();
            Log?.Invoke($"重复兵升级规则：{choices.Count} 项用户选择，待保存；支持撤销重做。");
        }
    }
}
