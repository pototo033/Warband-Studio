using System.Collections.ObjectModel;
using System.Data;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using WarbandStudio.Pack;
using WarbandStudio.Ui.Services;

namespace WarbandStudio.Ui.ViewModels;

// 工程打开、识别清单与历史版本独立维护，复用未保存确认流程。
public sealed partial class MainViewModel
{
    // ── 工程（v1.5.0）──────────────────────────────────────────

    private async Task ResetProjectSessionsAsync()
    {
        ++_autoSaveGen;
        foreach (var session in _backend.Packs.ToArray()) await _backend.CloseSessionAsync(session);
        _activeCenterTab = null!;
        CenterTabs.Clear();
        Raise(nameof(ActiveCenterTab));
        Raise(nameof(IsCanvasActive));
        _selectedFaction = null;
        Raise(nameof(SelectedFaction));
        Factions.Clear();
        Library.Clear();
        _libAll = [];
        _modLibAll = [];
        ModLibrary.Clear();
        ResetCostForm();
        AfterPackClosed();
    }

    /// <summary>切工程/关工程/还原前统一处理未导出编辑：三选（是=**保存所有有编辑的包** / 否=放弃 / 取消=中止）。
    /// 返回 false = 用户取消或保存失败（调用方应中止操作）。</summary>
    private async Task<bool> ConfirmUnsavedBeforeAsync(string what)
    {
        if (FlushTableEdits is not null) await FlushTableEdits();
        var (packs, edits) = UnsavedEdits();
        if (packs == 0) return true;
        var choice = AskSave?.Invoke(what, $"{edits} 处未导出（{packs} 个包）") ?? 1;
        if (choice == 2) { Status = "已取消"; return false; }
        if (choice == 0) return await SaveAllEditsAsync();
        foreach (var s in _backend.Packs.ToList()) _backend.DiscardEditsOf(s.PackPath);
        return true;
    }

    /// <summary>新建工程（"新建工程"对话框的结果）：工程目录 + 生成 Pack 目录 + 项目 key；
    /// 建好骨架（project.json + old/ + Pack 目录），**不自动导入任何包**。</summary>
    public async Task<bool> NewProjectAsync(string dir, string? packDir = null, string? projectKey = null)
    {
        try
        {
            dir = Path.GetFullPath(dir);
            if (!await ConfirmUnsavedBeforeAsync("新建工程")) return false;
            var info = ProjectStore.Ensure(dir, prefillKey: projectKey ?? _settings.GroupKeyPrefix, packDir: packDir);
            if (!string.IsNullOrWhiteSpace(projectKey)) info.ProjectKey = projectKey!.Trim();
            if (!string.IsNullOrWhiteSpace(packDir)) info.PackDir = packDir!.Trim();
            ProjectStore.Save(dir, info);
            await ResetProjectSessionsAsync();
            _projectDir = dir;
            Project = info;
            _backend.ProjectKey = info.ProjectKey;
            ProjectStore.TouchRecent(_settings.RecentProjects, dir);
            _settings.Save();
            PruneRecentProjects();
            var packs = info.Packs.Select(rel => ProjectStore.FullPath(dir, rel)).Where(File.Exists).ToList();
            if (packs.Count > 0 && !await OpenPackPathAsync(packs[0], open: true, asUnitPack: false)) return false;
            Status = packs.Count > 0
                ? $"工程「{info.Name}」已打开：{Path.GetFileName(packs[0])}"
                : $"工程「{info.Name}」已建好（Pack 目录 {info.PackDir}）—— 用「导入 WUU 模板」或「导入 Pack」放一个包进来";
            FileLog.Write($"工程：新建 {dir}（Pack 目录 {ProjectStore.PackDirOf(dir, info)}，项目 key「{info.ProjectKey}」）");
            return true;
        }
        catch (Exception ex) { Status = "新建工程失败：" + ex.Message; FileLog.Write("新建工程失败", ex); return false; }
    }

    /// <summary>打开一个已有工程（打开它的 lastPack；里面没包就登记着，等导入）。</summary>
    public Task<bool> OpenProjectAsync(string dir) => OpenProjectAtAsync(dir);

    /// <summary>「打开上次工程」：直接开最近工程列表里的第一个（没有就提示从哪开始）。</summary>
    public async Task<bool> OpenLastProjectAsync()
    {
        PruneRecentProjects();
        var last = RecentProjects.FirstOrDefault();
        if (last is null)
        {
            Status = "还没有最近工程 —— 用「新建工程」，或在菜单「工程」里选「从 Pack 打开工程」";
            return false;
        }
        return await OpenRecentProjectAsync(last);
    }

    private async Task<bool> OpenProjectAtAsync(string dir)
    {
        try
        {
            dir = Path.GetFullPath(dir);
            if (!ProjectStore.IsProject(dir))
            {
                Status = "这个文件夹还不是工程（没有 project.json）——用「新建工程」，或菜单「从 Pack 打开工程」";
                return false;
            }
            var info = ProjectStore.Ensure(dir, prefillKey: _settings.GroupKeyPrefix);
            foreach (var relative in info.Packs)
            {
                var candidate = ProjectStore.FullPath(dir, relative);
                if (File.Exists(candidate)) { using var validation = WarbandStudio.Packfile.PackArchive.Open(candidate); }
            }
            if (!await ConfirmUnsavedBeforeAsync("打开工程")) return false;
            await ResetProjectSessionsAsync();
            _projectDir = dir;
            Project = info;
            _backend.ProjectKey = info.ProjectKey;
            ProjectStore.TouchRecent(_settings.RecentProjects, dir);
            _settings.Save();
            PruneRecentProjects();
            // **按识别清单打开所有包**（工程目录里没被识别的包不自动开）；LastPack 放最后开 = 激活的画布
            var all = ProjectStore.PacksIn(dir);
            var toOpen = info.Packs
                .Select(rel => ProjectStore.FullPath(dir, rel))
                .Where(f => f.Length > 0 && File.Exists(f))
                .ToList();
            var missing = info.Packs.Count - toOpen.Count;
            var last = ProjectStore.FullPath(dir, info.LastPack);
            if (toOpen.Any(f => f.Equals(last, StringComparison.OrdinalIgnoreCase)))
            {
                toOpen.RemoveAll(f => f.Equals(last, StringComparison.OrdinalIgnoreCase));
                toOpen.Add(last);
            }
            var opened = 0;
            foreach (var f in toOpen)
            {
                if (!await OpenPackPathAsync(f, open: true, asUnitPack: false)) return false;
                opened++;
            }
            Status = toOpen.Count switch
            {
                0 => $"工程「{info.Name}」已打开 —— 用「导入 WUU 模板」或「导入 Pack」放一个包进来" +
                     (all.Count > 0 ? "（目录里有包但都没被识别：工程根右键「工程内的包…」勾一下）" : ""),
                _ => $"工程「{info.Name}」已打开，同时开 {toOpen.Count} 个包：{string.Join("、", toOpen.Select(Path.GetFileName))}" +
                     (all.Count > toOpen.Count ? $"（另有 {all.Count - toOpen.Count} 个未识别，不自动开）" : "") +
                     (missing > 0 ? $"（{missing} 个清单里的包不在了，已跳过）" : ""),
            };
            FileLog.Write($"工程：打开 {dir}（项目 key「{info.ProjectKey}」，识别 {info.Packs.Count} / 目录 {all.Count} 个包，本次开 {toOpen.Count} 个）");
            return true;
        }
        catch (Exception ex) { Status = "打开工程失败：" + ex.Message; FileLog.Write("打开工程失败", ex); return false; }
    }

    /// <summary>「从 Pack 打开工程」：把**源 pack 复制**到工程的"生成 Pack 目录"再编辑（**源文件不动**），
    /// 然后建工程并打开复制件。工程目录 / Pack 目录 / 项目 key 来自"新建工程"对话框。</summary>
    public async Task<bool> OpenProjectFromPackAsync(string sourcePack, string projectDir,
                                                     string? packDir = null, string? projectKey = null)
    {
        try
        {
            var src = Path.GetFullPath(sourcePack);
            if (!File.Exists(src)) { Status = "找不到源 pack：" + src; return false; }
            using (WarbandStudio.Packfile.PackArchive.Open(src)) { }
            projectDir = Path.GetFullPath(projectDir);
            if (!await ConfirmUnsavedBeforeAsync("打开工程")) return false;
            var info = ProjectStore.Ensure(projectDir, prefillKey: projectKey ?? _settings.GroupKeyPrefix, packDir: packDir);
            if (!string.IsNullOrWhiteSpace(projectKey)) info.ProjectKey = projectKey!.Trim();
            if (!string.IsNullOrWhiteSpace(packDir)) info.PackDir = packDir!.Trim();
            var targetDir = ProjectStore.PackDirOf(projectDir, info);
            Directory.CreateDirectory(targetDir);
            var dest = Path.Combine(targetDir, Path.GetFileName(src));
            if (!Path.GetFullPath(dest).Equals(src, StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(dest))
                    FileLog.Write($"工程：目标已有 {Path.GetFileName(dest)} → 直接打开现有那份（**不覆盖**；源文件也不动）");
                else AtomicFile.Copy(src, dest);
            }
            using (WarbandStudio.Packfile.PackArchive.Open(dest)) { }
            info.LastPack = dest;
            ProjectStore.AddPack(info, ProjectStore.RelPath(projectDir, dest));   // 识别
            ProjectStore.Save(projectDir, info);
            using (WarbandStudio.Packfile.PackArchive.Open(dest)) { }
            await ResetProjectSessionsAsync();
            _projectDir = projectDir;
            Project = info;
            _backend.ProjectKey = info.ProjectKey;
            ProjectStore.TouchRecent(_settings.RecentProjects, projectDir);
            _settings.Save();
            PruneRecentProjects();
            if (!await OpenPackPathAsync(dest, open: true, asUnitPack: false)) return false;
            Status = $"已复制进工程并打开：{Path.GetFileName(dest)}（源文件不动，编辑都在工程里）";
            FileLog.Write($"工程：从 Pack 建立 {projectDir}（复制 {src} → {dest}，项目 key「{info.ProjectKey}」）");
            return true;
        }
        catch (Exception ex) { Status = "从 Pack 打开工程失败：" + ex.Message; FileLog.Write("从 Pack 打开工程失败", ex); return false; }
    }

    /// <summary>打开"最近工程"里的一个（欢迎面板/菜单的快捷入口）。</summary>
    public async Task<bool> OpenRecentProjectAsync(string dir)
    {
        if (!Directory.Exists(dir) || !ProjectStore.IsProject(dir))
        {
            PruneRecentProjects();
            Status = "这个工程目录已经不在了（已从最近列表移除）";
            return false;
        }
        return await OpenProjectAsync(dir);
    }

    /// <summary>关闭工程：它下面打开的包全关掉（有未导出编辑先问）。</summary>
    public async Task CloseProjectAsync()
    {
        if (_project is null) return;
        if (!await ConfirmUnsavedBeforeAsync("关闭工程")) return;
        await ResetProjectSessionsAsync();
        Project = null;
        _projectDir = "";
        _backend.ProjectKey = "";
        RebuildTree();
        FileLog.Write("工程：已关闭");
        Status = "工程已关闭";
    }

    /// <summary>当前包的历史版本列表（工程 old/ 里的备份，新到旧）。没工程/没包时为空。</summary>
    public List<HistoryRow> HistoryOfCurrentPack()
    {
        var list = new List<HistoryRow>();
        var pack = _backend.Pack?.PackPath;
        if (pack is null || _project is null || _projectDir.Length == 0) return list;
        foreach (var f in ProjectStore.HistoryOf(_projectDir, pack))
            list.Add(new HistoryRow(f.Name, f.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
                                    $"{f.Length / 1024.0 / 1024.0:N1} MB", f.FullName));
        return list;
    }

    /// <summary>当前包所在工程的历史版本目录（对话框的「打开文件夹」用；没工程返回空）。</summary>
    public string HistoryDirOfCurrentPack()
    {
        var pack = _backend.Pack?.PackPath;
        if (pack is null || _project is null || _projectDir.Length == 0) return "";
        return ProjectStore.HistoryDirOf(_projectDir);
    }

    /// <summary>还原到某个历史版本：有未导出编辑先问；Backend 负责"先把当前包也备份一份再拷回"，
    /// 然后走正常打开链路把包重开（画布/树/清单一起刷新）。</summary>
    public async Task<bool> RestoreHistoryAsync(string backupFile)
    {
        var pack = _backend.Pack?.PackPath;
        if (pack is null) { Status = "先打开一个包"; return false; }
        if (!await ConfirmUnsavedBeforeAsync("还原历史版本")) return false;
        try
        {
            var msg = await _backend.RestoreFromBackupAsync(pack, backupFile);
            if (!await OpenPackPathAsync(pack, open: true, asUnitPack: false)) return false;   // 重开 + 画布/树/清单全刷
            Status = msg;
            return true;
        }
        catch (Exception ex) { Status = "还原失败：" + ex.Message; FileLog.Write("还原失败", ex); return false; }
    }

    /// <summary>有未导出编辑的包数 / 编辑总处数（关窗口提醒用）。</summary>
    public (int Packs, int Edits) UnsavedEdits()
    {
        var dirty = _backend.Packs.Where(s => _backend.EditCountOf(s.PackPath) > 0).ToList();
        return (dirty.Count, dirty.Sum(s => _backend.EditCountOf(s.PackPath)));
    }

    /// <summary>保存**指定包**（写回原包；工程模式下同时留一份 old/ 历史版本）。失败返回 false。</summary>
    public async Task<bool> SavePackByPathAsync(string packPath)
    {
        var session = _backend.Packs.FirstOrDefault(x =>
            string.Equals(x.PackPath, packPath, StringComparison.OrdinalIgnoreCase));
        if (session is null) return false;
        try
        {
            Status = await Task.Run(() => _backend.SaveInPlaceAsync(session));
            return true;
        }
        catch (Exception ex) { Status = "保存失败：" + ex.Message; FileLog.Write("保存失败", ex); return false; }
    }

    /// <summary>把所有有编辑的包都写回原包（关窗口/关工程"保存后继续"用）。返回是否全部成功。</summary>
    public async Task<bool> SaveAllEditsAsync()
    {
        var dirty = _backend.Packs.Where(s => _backend.EditCountOf(s.PackPath) > 0).ToList();
        foreach (var s in dirty)
            if (!await SavePackByPathAsync(s.PackPath)) return false;
        RebuildTree();
        return true;
    }

    /// <summary>把外部 pack 拷进当前工程并打开（"往工程里加一个包"的正路）。</summary>
    public async Task<bool> ImportPackToProjectAsync(string externalPack)
    {
        if (_project is null || _projectDir.Length == 0) { Status = "先新建/打开一个工程"; return false; }
        try
        {
            var srcFull = Path.GetFullPath(externalPack);
            using (WarbandStudio.Packfile.PackArchive.Open(srcFull)) { }
            var targetDir = ProjectStore.PackDirOf(_projectDir, _project);
            Directory.CreateDirectory(targetDir);
            var dest = Path.Combine(targetDir, Path.GetFileName(srcFull));
            if (srcFull.Equals(Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
            {
                // 已经在"生成 Pack 目录"里 → 不复制，直接**识别 + 打开**（给"手动拷进工程的包"用的入口）
            }
            else
            {
                if (File.Exists(dest)) { Status = $"工程里已经有 {Path.GetFileName(dest)}（要么给导入的包改个名，要么先删旧的那份）"; return false; }
                AtomicFile.Copy(srcFull, dest);
            }
            ProjectStore.AddPack(_project, ProjectStore.RelPath(_projectDir, dest));   // 识别：下次打开工程一起开
            ProjectStore.Save(_projectDir, _project);
            if (!await OpenPackPathAsync(dest, open: true, asUnitPack: false)) return false;
            FileLog.Write($"工程：导入包 {Path.GetFileName(dest)}（已识别；下次打开工程会一起开）");
            return true;
        }
        catch (Exception ex) { Status = "导入包失败：" + ex.Message; return false; }
    }

    /// <summary>导入内置的 WUU 模板到当前工程并打开（空工程起步用；素材在 exe 旁 bundled/wuu）。</summary>
    public async Task<bool> ImportWuuTemplateAsync()
    {
        if (_project is null || _projectDir.Length == 0) { Status = "先新建/打开一个工程"; return false; }
        var pack = _backend.BuildWuuTemplatePack(_projectDir);
        if (pack is null) { Status = "导入失败：内置 WUU 素材缺失（exe 旁 bundled\\wuu 里应有 .pack）"; return false; }
        ProjectStore.AddPack(_project, ProjectStore.RelPath(_projectDir, pack));   // 识别：下次打开工程一起开
        ProjectStore.Save(_projectDir, _project);
        if (!await OpenPackPathAsync(pack, open: true, asUnitPack: false)) return false;
        FileLog.Write($"工程：导入 WUU 模板 {Path.GetFileName(pack)}（已识别）");
        return true;
    }

    /// <summary>「工程内的包…」对话框的数据：目录里每个包 + 是否已识别（在清单里）+ 大小。</summary>
    public List<ProjectPackRow> ProjectPacksList()
    {
        var list = new List<ProjectPackRow>();
        if (_project is null || _projectDir.Length == 0) return list;
        foreach (var f in ProjectStore.PacksIn(_projectDir))
        {
            var rel = ProjectStore.RelPath(_projectDir, f);
            var fi = new FileInfo(f);
            var rec = _project.Packs.Any(p => p.Equals(rel, StringComparison.OrdinalIgnoreCase));
            list.Add(new ProjectPackRow(f, rel, fi.Name, $"{fi.Length / 1024.0 / 1024.0:N1} MB", rec));
        }
        return list;
    }

    /// <summary>应用「工程内的包…」的勾选（= 识别清单）：保存清单；**新勾上的立刻打开**；
    /// 取消勾选的不主动关（已经开着就开着，下次打开工程起不再自动开）。</summary>
    public async Task ApplyProjectPacksAsync(List<string> rels)
    {
        if (_project is null || _projectDir.Length == 0) return;
        var previous = _project.Packs;
        try
        {
            foreach (var rel in rels)
            {
                var candidate = ProjectStore.FullPath(_projectDir, rel);
                if (File.Exists(candidate)) { using var validation = WarbandStudio.Packfile.PackArchive.Open(candidate); }
            }
            _project.Packs = rels.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            ProjectStore.Save(_projectDir, _project);
        }
        catch (Exception ex) { _project.Packs = previous; Status = "识别清单保存失败：" + ex.Message; return; }
        var opened = _backend.Packs.Select(s => s.PackPath).ToList();
        var n = 0;
        foreach (var rel in _project.Packs)
        {
            var f = ProjectStore.FullPath(_projectDir, rel);
            if (f.Length == 0 || !File.Exists(f)) continue;
            if (opened.Any(p => p.Equals(f, StringComparison.OrdinalIgnoreCase))) continue;
            if (!await OpenPackPathAsync(f, open: true, asUnitPack: false)) return;
            n++;
        }
        FileLog.Write($"工程：识别清单更新为 {_project.Packs.Count} 个包" + (n > 0 ? $"（顺手打开了 {n} 个新勾的）" : ""));
        Status = $"识别清单已更新：{_project.Packs.Count} 个包会在打开工程时自动打开" +
                 (n > 0 ? $"（已打开 {n} 个新勾选的）" : "");
    }

}
