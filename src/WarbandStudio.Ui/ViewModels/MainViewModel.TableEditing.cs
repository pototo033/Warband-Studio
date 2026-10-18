using WarbandStudio.Ui.Services;

namespace WarbandStudio.Ui.ViewModels;

public sealed partial class MainViewModel
{
    public Func<Task>? FlushTableEdits { get; set; }
    private async void RefreshOpenTables()
    {
        foreach (var tab in CenterTabs.OfType<TableTab>().Where(t => string.Equals(t.PackPath, _backend.Pack?.PackPath, StringComparison.OrdinalIgnoreCase)))
            await ReloadTableTabAsync(tab);
    }
    public async Task CommitTableCellAsync(TableTab tab, int row, string column, string[] expected, string value)
    {
        IsBusy = true;
        try
        {
            await Task.Run(() => _backend.EditTableCell(tab.PackPath, tab.InnerPath, row, column, expected, value));
            RebuildTree();
            PackOpened?.Invoke();
            RefreshLibrarySoon();
            RefreshPooledResources();
            foreach (var open in CenterTabs.OfType<TableTab>().Where(t => string.Equals(t.PackPath, tab.PackPath, StringComparison.OrdinalIgnoreCase))) await ReloadTableTabAsync(open);
            Status = "已修改单元格：" + column + "；" + _backend.EditSummary;
            AutoSaveSoon();
        }
        catch (Exception ex) { Status = "单元格未修改：" + ex.Message; FileLog.Write("表编辑失败", ex); await ReloadTableTabAsync(tab); }
        finally { IsBusy = false; }
    }
}
