using WarbandStudio.Ui.Services;

namespace WarbandStudio.Ui.ViewModels;

public sealed partial class MainViewModel
{
    internal Backend AutomationBackend => _backend;
    internal void BeginAutomationEdit() => ++_autoSaveGen;

    internal async Task ConfigureAutomationGameAsync(string path)
    {
        GameDir = path;
        _settings.GameDir = path;
        _settings.Save();
        await _backend.ApplyGameAsync();
    }

    internal void RefreshAutomation()
    {
        foreach (var tab in CenterTabs.ToArray())
        {
            var path = tab is CanvasTab canvas ? canvas.PackPath : (tab as TableTab)?.PackPath;
            if (path is not null && !_backend.Packs.Any(p => p.PackPath == path)) CenterTabs.Remove(tab);
        }
        if (_backend.Pack is { IsUnitPack: false } pack)
        {
            var canvas = EnsureCanvasTab(pack);
            var shown = ActiveCenterTab is CanvasTab shownCanvas ? shownCanvas.PackPath : (ActiveCenterTab as TableTab)?.PackPath;
            if (!string.Equals(shown, pack.PackPath, StringComparison.OrdinalIgnoreCase)) ActiveCenterTab = canvas;
        }
        else if (CenterTabs.Count == 0) ActiveCenterTab = null!;
        RebuildTree();
        PackOpened?.Invoke();
        RefreshOpenTables();
        RefreshPooledResources();
        LoadUiAssets();
        RefreshMods();
        RefreshLibrarySoon();
        Raise(nameof(PackOpen));
        PackText = _backend.Pack is { } current ? "Pack：" + current.DisplayName : "Pack：未打开";
        foreach (var command in new[] { SavePackCommand, SavePackAsCommand, ExportDiagnosticBundleCommand, ClosePackCommand })
            command.RaiseCanExecute();
    }
}
