using System.IO;
using WarbandStudio.Pack;
using WarbandStudio.Packfile;
using WarbandStudio.Ui.Services;

namespace WarbandStudio.Ui.ViewModels;

public sealed partial class MainViewModel
{
    private bool _updatePreparing;
    private string? _updateRecoveryManifest;
    public event Action<bool>? UpdateSessionLockChanged;

    private async Task<bool> PrepareUpdateRecoveryAsync()
    {
        if (IsBusy) { Status = "请等待当前操作完成后再更新。"; return false; }
        if (FlushTableEdits is not null) await FlushTableEdits();
        ++_autoSaveGen;
        _updatePreparing = true;
        IsBusy = true;
        UpdateSessionLockChanged?.Invoke(true);
        try
        {
            Status = "正在保护打开的 Pack、未保存编辑及撤销记录…";
            var state = _backend.CaptureUpdateRecovery();
            state.ProjectDirectory = _projectDir;
            static RecoveryDocument? Document(CenterTab? tab) => tab switch
            {
                TableTab t => new(t.PackPath,t.InnerPath),
                CanvasTab { PackPath: { } path } => new(path,null),
                _ => null,
            };
            state.Documents = CenterTabs.Select(Document).OfType<RecoveryDocument>().ToList();
            state.ActiveDocument = Document(ActiveCenterTab);
            _updateRecoveryManifest = await Task.Run(() => UpdateRecovery.Save(UpdateRecovery.Root,AppContext.BaseDirectory,state));
            FileLog.Write("更新保护快照已写入：" + _updateRecoveryManifest);
            UpdateStatus = "未保存编辑已保护，重启后将自动恢复。";
            return true; // Keep the UI locked until shutdown or explicit cancellation.
        }
        catch { CancelUpdateRecovery(); throw; }
    }

    private void CancelUpdateRecovery()
    {
        try
        {
            if (_updateRecoveryManifest is not null)
                UpdateRecovery.Acknowledge(UpdateRecovery.Root,AppContext.BaseDirectory,_updateRecoveryManifest);
        }
        catch (Exception ex) { FileLog.Write("取消更新保护标记失败；快照仍保留",ex); }
        finally
        {
            _updateRecoveryManifest = null;
            if (_updatePreparing)
            {
                _updatePreparing = false; IsBusy = false;
                UpdateSessionLockChanged?.Invoke(false);
            }
        }
    }

    public async Task RestoreUpdateRecoveryAsync(string? root = null, string? installation = null)
    {
        root ??= UpdateRecovery.Root; installation ??= AppContext.BaseDirectory;
        if (_backend.Packs.Count != 0) return; // Never replace a live editing session.
        UpdateRecovery.Loaded? loaded;
        try { loaded = await Task.Run(() => UpdateRecovery.LoadPending(root,installation)); }
        catch (Exception ex)
        {
            Status = "更新编辑恢复未完成，保护快照仍保留：" + ex.Message;
            FileLog.Write("更新恢复失败",ex); return;
        }
        if (loaded is null) return;
        IsBusy = true;
        _updatePreparing = true;
        UpdateSessionLockChanged?.Invoke(true);
        ++_autoSaveGen;
        try
        {
            var paths = loaded.Session.Packs.Select(p =>
            {
                var path = UpdateRecovery.ResolvePack(loaded,p,out var copy);
                using (PackArchive.Open(path)) { }
                return (Saved:p,Path:path,Copy:copy);
            }).ToList();
            if (paths.Select(p=>p.Saved.OriginalPath).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=paths.Count)
                throw new InvalidDataException("恢复清单含重复 Pack。");
            var map = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in paths)
            {
                _backend.RestoreUpdatePack(item.Saved,item.Path,item.Copy);
                map[item.Saved.OriginalPath] = item.Path;
            }
            if (loaded.Session.ProjectDirectory is { Length:>0 } project && ProjectStore.IsProject(project))
            {
                _projectDir = project; Project = ProjectStore.Load(project);
                _backend.ProjectKey = Project.ProjectKey;
            }
            foreach (var pack in _backend.Packs.Where(p=>!p.IsUnitPack)) EnsureCanvasTab(pack);
            foreach (var document in loaded.Session.Documents.Where(d=>d.InnerPath is not null))
                if (map.TryGetValue(document.PackPath,out var path)) await OpenTableAsync(document.InnerPath!,path);
            var active = loaded.Session.ActiveDocument;
            var activePath = active?.PackPath ?? loaded.Session.ActivePack;
            if (activePath is not null && map.TryGetValue(activePath,out var selected))
            {
                var tab = CenterTabs.FirstOrDefault(t => active?.InnerPath is { } inner
                    ? t is TableTab table && table.PackPath.Equals(selected,StringComparison.OrdinalIgnoreCase) && table.InnerPath==inner
                    : t is CanvasTab canvas && string.Equals(canvas.PackPath,selected,StringComparison.OrdinalIgnoreCase));
                if (tab is not null) ActiveCenterTab = tab;
                var pack = _backend.Packs.First(p=>string.Equals(p.PackPath,selected,StringComparison.OrdinalIgnoreCase));
                if (!pack.IsUnitPack) _backend.SwitchTo(pack);
            }
            RebuildTree(); LoadUiAssets(); RefreshPooledResources(); RefreshMods();
            Raise(nameof(PackOpen)); PackOpened?.Invoke();
            UpdateRecovery.Acknowledge(root,installation,loaded.Manifest);
            Status = $"更新恢复完成：{paths.Count} 个 Pack，{_backend.Packs.Sum(p=>_backend.EditCountOf(p.PackPath))} 处未保存编辑";
            if (paths.Any(p=>p.Copy)) Status += "；原文件已变动或缺失，相关编辑在恢复副本中，请另存到合适位置";
            FileLog.Write(Status + "；保护快照：" + loaded.Manifest);
        }
        catch (Exception ex)
        {
            await ResetProjectSessionsAsync();
            _projectDir = ""; Project = null;
            Status = "更新恢复未完成，快照已保留，重新启动可重试：" + ex.Message;
            FileLog.Write("更新恢复失败",ex);
        }
        finally { _updatePreparing = false; IsBusy = false; UpdateSessionLockChanged?.Invoke(false); }
    }
}
