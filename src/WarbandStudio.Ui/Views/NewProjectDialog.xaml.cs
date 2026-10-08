using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace WarbandStudio.Ui.Views;

/// <summary>
/// "新建工程"对话框（v1.5.0）：**工程目录 + 生成 Pack 目录 + 项目 key**（用户 2026-10-08 定的表单）。
/// 「从 Pack 打开工程」也复用它（标题换一下、提示里带源 pack；确定后由调用方负责把源包**复制**进工程）。
/// </summary>
public partial class NewProjectDialog : Window
{
    /// <summary>确定后的结果（取消则不读）。</summary>
    public string ProjectDir { get; private set; } = "";
    public string PackDir { get; private set; } = "";
    public string ProjectKey { get; private set; } = "";

    private bool _packDirTouched;    // 用户手动改过 Pack 目录 → 换工程目录时不再自动跟随
    private bool _syncing;           // 程序在填 Pack 目录（别把"手动改过"置位）
    private const string PackDirName = "packs";   // 与 ProjectStore.DefaultPackDir 保持一致

    /// <param name="title">窗口标题（"新建工程" / "从 Pack 打开工程"）。</param>
    /// <param name="sourcePack">从 Pack 打开时传源包路径（提示里写明"复制、源文件不动"）。</param>
    public NewProjectDialog(string title = "新建工程", string? sourcePack = null)
    {
        InitializeComponent();
        Title = title;
        if (!string.IsNullOrWhiteSpace(sourcePack))
        {
            TxtIntro.Text = "把源 pack **复制**到工程里编辑（源文件不动）；工程目录放项目设置和历史版本（old/）。\n" +
                            "「生成 Pack 所在目录」默认为工程目录的子文件夹。";
            TxtNote.Text = $"源 pack：{sourcePack}";
        }
        else
        {
            TxtNote.Text = "工程建好后，用「导入 WUU 模板」或「导入 Pack」放一个包进来。";
        }
        Loaded += (_, _) => TxtProjectDir.Focus();
    }

    /// <summary>工程目录变化 → Pack 目录跟着变成 &lt;工程&gt;\packs（除非用户手动改过）。</summary>
    private void OnProjectDirChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing || _packDirTouched) return;
        var dir = TxtProjectDir.Text.Trim();
        if (dir.Length == 0) return;
        _syncing = true;
        try
        {
            try { TxtPackDir.Text = Path.Combine(Path.GetFullPath(dir), PackDirName); }
            catch { TxtPackDir.Text = Path.Combine(dir, PackDirName); }
        }
        finally { _syncing = false; }
    }

    private void OnPackDirChanged(object sender, TextChangedEventArgs e)
    {
        if (!_syncing) _packDirTouched = true;
    }

    private void OnBrowseProject(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择工程文件夹", Multiselect = false };
        if (dlg.ShowDialog(this) == true) TxtProjectDir.Text = dlg.FolderName;   // TextChanged 会带着 Pack 目录一起跟
    }

    private void OnBrowsePack(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择生成 Pack 所在目录", Multiselect = false };
        if (dlg.ShowDialog(this) == true)
        {
            TxtPackDir.Text = dlg.FolderName;
            _packDirTouched = true;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var dir = TxtProjectDir.Text.Trim();
        var pack = TxtPackDir.Text.Trim();
        if (dir.Length == 0)
        {
            MessageBox.Show(this, "先选一个工程文件夹。", "新建工程", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (pack.Length == 0)
        {
            MessageBox.Show(this, "「生成 Pack 所在目录」不能为空（默认是工程目录下的 packs 子文件夹）。",
                            "新建工程", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        ProjectDir = dir;
        PackDir = pack;
        ProjectKey = TxtKey.Text.Trim().Length > 0 ? TxtKey.Text.Trim() : "studio";
        DialogResult = true;
    }
}
