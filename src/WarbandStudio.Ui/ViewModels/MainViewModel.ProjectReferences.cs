using System.IO;
using WarbandStudio.Pack;

namespace WarbandStudio.Ui.ViewModels;

public sealed partial class MainViewModel
{
    private int _projectReferencesSuspended;

    private void RememberProjectReference(string path, bool loaded)
    {
        if (_projectReferencesSuspended > 0 || _updatePreparing || _project is null || _projectDir.Length == 0) return;
        var previous = _project.ReferencePacks.ToList();
        var changed = loaded ? ProjectStore.AddReferencePack(_project, _projectDir, path)
            : ProjectStore.RemoveReferencePack(_project, _projectDir, path);
        if (!changed) return;
        try
        {
            ProjectStore.Save(_projectDir, _project);
            Services.FileLog.Write($"工程参考 mod：{(loaded ? "记住" : "取消记忆")} {path}；工程 {_projectDir}");
        }
        catch (Exception ex)
        {
            _project.ReferencePacks = previous;
            Status = "参考 mod 路径记录失败：" + ex.Message;
            Services.FileLog.Write("工程参考 mod 路径记录失败：" + path, ex);
        }
    }

    private async Task<string> RestoreProjectReferencesAsync()
    {
        if (_project is null || _project.ReferencePacks.Count == 0) return "";
        var failures = new List<string>();
        var loaded = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var writable = _project.Packs.Select(path => ProjectStore.FullPath(_projectDir, path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _projectReferencesSuspended++;
        try
        {
            foreach (var saved in _project.ReferencePacks)
            {
                var path = ProjectStore.FullPath(_projectDir, saved);
                if (!seen.Add(path.Length == 0 ? saved : path)) continue;
                try
                {
                    if (!File.Exists(path)) throw new FileNotFoundException("路径不存在");
                    if (writable.Contains(path)) throw new InvalidOperationException("同一路径已登记为可编辑 Pack，不重复加载为参考 mod");
                    await _backend.OpenPackAsync(path, asUnitPack: true);
                    loaded++;
                }
                catch (Exception ex)
                {
                    failures.Add($"{saved}：{ex.Message}");
                    Services.FileLog.Write("自动加载工程参考 mod 失败：" + saved, ex);
                }
            }
        }
        finally { _projectReferencesSuspended--; }
        RefreshMods();
        var result = $"；自动加载 {loaded} 个参考 mod";
        if (failures.Count > 0) result += $"，{failures.Count} 个未加载（路径记录保留）：" + string.Join("；", failures);
        Services.FileLog.Write("工程参考 mod" + result);
        return result;
    }

    public async Task UnloadReferenceModAsync(ModItem mod)
    {
        if (IsBusy) return;
        var session = _backend.Packs.FirstOrDefault(pack => pack.IsUnitPack && string.Equals(pack.PackPath, mod.Key, StringComparison.OrdinalIgnoreCase));
        if (session is null) return;
        IsBusy = true;
        try
        {
            await _backend.CloseSessionAsync(session);
            _modRaceFilters.Remove(mod.Key);
            RefreshMods();
            RefreshPooledResources();
            RefreshLibrarySoon();
            PackOpened?.Invoke();
            Status = $"已卸载参考 mod：{session.DisplayName}" + (HasProject ? "，本工程下次不再自动加载" : "");
        }
        finally { IsBusy = false; }
    }
}
