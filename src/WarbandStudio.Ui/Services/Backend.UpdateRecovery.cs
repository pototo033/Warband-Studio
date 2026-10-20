using WarbandStudio.Pack;

namespace WarbandStudio.Ui.Services;

public sealed partial class Backend
{
    public RecoverySession CaptureUpdateRecovery()
    {
        lock (StateSync)
        {
            return new RecoverySession
            {
                ActivePack = _pack?.PackPath,
                Packs = _packs.Where(p => p.IsOpen).Select(p => new RecoveryPack
                {
                    OriginalPath = p.PackPath!, DisplayName = p.DisplayName, ProjectDirectory = p.ProjectDirectory,
                    ProjectKey = p.ProjectKey, IsUnitPack = p.IsUnitPack,
                    Edits = _editsByPack.TryGetValue(p.PackPath!,out var e) ? e.Clone() : new(),
                    Undo = _undoByPack.TryGetValue(p.PackPath!,out var u) ? u.Select(e=>e.Clone()).ToList() : [],
                    Redo = _redoByPack.TryGetValue(p.PackPath!,out var r) ? r.Select(e=>e.Clone()).ToList() : [],
                }).ToList(),
            };
        }
    }

    public PackSession RestoreUpdatePack(RecoveryPack saved, string path, bool recoveredCopy)
    {
        lock (StateSync)
        {
            var pack = OpenPackAsync(path, displayName: saved.DisplayName + (recoveredCopy ? "（更新恢复副本）" : ""), asUnitPack: saved.IsUnitPack).GetAwaiter().GetResult();
            pack.ProjectDirectory = recoveredCopy ? null : saved.ProjectDirectory;
            pack.ProjectKey = saved.ProjectKey;
            _editsByPack[path] = saved.Edits.Clone();
            _undoByPack[path] = saved.Undo.Select(e=>e.Clone()).ToList();
            _redoByPack[path] = saved.Redo.Select(e=>e.Clone()).ToList();
            pack.SetPendingTables(_editsByPack[path].TableReplacements);
            RefreshSourceScope();
            if (!pack.IsUnitPack) RestagePendingArt();
            return pack;
        }
    }
}
