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
            // 先在副本中迁移缓存，成功后整体恢复；绝不因启动恢复写回原包。
            WarbandEdits Normalize(WarbandEdits source)
            {
                var edits = source.Clone();
                var notes = new List<string>();
                TabFileBindingOperations.NormalizeRecovered(pack.Archive!, edits, GetSchema(), notes);
                foreach (var note in notes) FileLog.Write(note);
                return edits;
            }
            var restored = Normalize(saved.Edits);
            var undo = saved.Undo.Select(Normalize).ToList();
            var redo = saved.Redo.Select(Normalize).ToList();
            _editsByPack[path] = restored;
            _undoByPack[path] = undo;
            _redoByPack[path] = redo;
            FileLog.Write($"更新恢复：仅加载未保存修改，未写入原包；表缓存={restored.TableReplacements.Count}，撤销={undo.Count}，重做={redo.Count}");
            pack.SetPendingTables(_editsByPack[path].TableReplacements);
            RefreshSourceScope();
            if (!pack.IsUnitPack) RestagePendingArt();
            return pack;
        }
    }
}
