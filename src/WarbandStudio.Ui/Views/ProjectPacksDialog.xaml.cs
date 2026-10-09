using System.Windows;
using WarbandStudio.Ui.ViewModels;

namespace WarbandStudio.Ui.Views;

/// <summary>
/// 「工程内的包…」对话框（v1.5.7）：列出工程"生成 Pack 目录"里的所有包，
/// **勾上 = 已识别**（打开工程时会自动一起打开）。确定后新勾上的立刻打开。
/// </summary>
public partial class ProjectPacksDialog : Window
{
    private readonly MainViewModel _vm;
    private readonly List<ProjectPackRow> _rows;

    public ProjectPacksDialog(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        _rows = vm.ProjectPacksList();
        Lst.ItemsSource = _rows;
        if (_rows.Count == 0)
            Lst.ItemsSource = new[] { new ProjectPackRow("", "", "（工程里还没有包 —— 先用「导入 WUU 模板」或「导入 Pack」放一个进来）", "", false) };
    }

    private void OnCheckAll(object sender, RoutedEventArgs e)
    {
        foreach (var r in _rows) r.Checked = true;
        Lst.Items.Refresh();
    }

    private void OnCheckNone(object sender, RoutedEventArgs e)
    {
        foreach (var r in _rows) r.Checked = false;
        Lst.Items.Refresh();
    }

    private async void OnOk(object sender, RoutedEventArgs e)
    {
        var rels = _rows.Where(r => r.Checked && r.Rel.Length > 0).Select(r => r.Rel).ToList();
        Close();
        await _vm.ApplyProjectPacksAsync(rels);
    }
}
