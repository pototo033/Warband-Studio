using System.Windows;
using WarbandStudio.Ui.ViewModels;

namespace WarbandStudio.Ui.Views;

/// <summary>
/// 「历史版本」对话框（v1.5.2）：列出**当前包**在工程 `old/` 里的备份（每次保存前自动生成，
/// 保留最近 20 份）→ 选中一份「还原」（还原前当前包也会先备份一份，不丢东西）。
/// 没有工程 / 包里还没有备份时，给出下一步提示。
/// </summary>
public partial class HistoryDialog : Window
{
    private readonly MainViewModel _vm;

    public HistoryDialog(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        Loaded += (_, _) => Reload();
    }

    private void Reload()
    {
        var packName = System.IO.Path.GetFileName(_vm.CurrentPackPath);
        var dir = _vm.HistoryDirOfCurrentPack();
        var rows = _vm.HistoryOfCurrentPack();
        Lst.ItemsSource = rows;
        TxtSub.Text = rows.Count == 0
            ? (dir.Length == 0
                ? "这个包不在任何工程里 —— 历史版本（old/）跟随工程；先「新建工程」或菜单「从 Pack 打开工程」。"
                : $"「{packName}」还没有历史版本 —— 每次「保存」前会自动备份一份到这里（保留最近 20 份）。\n文件夹：{dir}")
            : $"当前包：{packName}　共 {rows.Count} 份备份（新的在前）—— 选中一份点「还原到所选…」（双击也行）；" +
              $"还原前会把当前包也备份一份。\n文件夹：{dir}";
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => Reload();

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        var dir = _vm.HistoryDirOfCurrentPack();
        if (dir.Length == 0) { _vm.SetStatus("这个包不在工程里"); return; }
        try
        {
            System.IO.Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{dir}\"")
            { UseShellExecute = true });
        }
        catch (Exception ex) { _vm.SetStatus("打开历史文件夹失败：" + ex.Message); }
    }

    private async void OnRestore(object sender, RoutedEventArgs e)
    {
        if (Lst.SelectedItem is not HistoryRow row) { _vm.SetStatus("先选中一份备份"); return; }
        var ok = MessageBox.Show(this,
            $"把当前包还原到：\n{row.File}（{row.When}）\n\n还原前会把当前包也备份一份（不会丢东西）。继续吗？",
            "还原历史版本", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (ok != MessageBoxResult.OK) return;
        if (await _vm.RestoreHistoryAsync(row.Full)) Close();   // 成功 → 关掉（画布已刷新）
        else Reload();                                          // 失败/取消 → 刷新（可能有新备份）
    }
}
