using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using WarbandStudio.Core;
using WarbandStudio.Ui.Services;

namespace WarbandStudio.Ui.Views;

/// <summary>候选目录的一条（带"能不能用"的判定）。</summary>
public sealed class GameDirOption
{
    public required string Path { get; init; }
    public required bool Valid { get; init; }
    public required string Note { get; init; }
    public string Mark => Valid ? "✓" : "·";
    public Brush MarkBrush => Valid
        ? (Brush)Application.Current.FindResource("Accent")
        : (Brush)Application.Current.FindResource("Text.Secondary");
}

/// <summary>
/// 选游戏目录：列出扫描到的候选让用户点，也可以手动填 / 浏览。
/// **不自动选、不自动应用** —— 用户点了「用这个目录」才算数。跳过也允许。
/// </summary>
public partial class GameDirDialog : Window
{
    /// <summary>用户选定的目录；跳过时为 null。</summary>
    public string? Result { get; private set; }

    public GameDirDialog(string? current)
    {
        InitializeComponent();
        if (!string.IsNullOrWhiteSpace(current)) TxtManual.Text = current;
        Scan();
    }

    /// <summary>扫一遍候选（顺序：Steam 记录过的库 → 常见默认位置）。</summary>
    private void Scan()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var options = new List<GameDirOption>();
        var total = 0;

        foreach (var c in GameLocator.Candidates())
        {
            total++;
            if (total > 40) break;                       // 别把整个磁盘翻一遍
            var norm = c.Replace('/', '\\').TrimEnd('\\');
            if (!seen.Add(norm)) continue;

            var ok = GameFinder.LooksLikeGame(norm);
            options.Add(new GameDirOption
            {
                Path = norm,
                Valid = ok,
                Note = ok ? "data 里有 .pack，可用" : "这里没有 data\\*.pack",
            });
        }

        // 有能用的就只列能用的（其余是噪音）；一个能用的都没有时全列出来，让人看见确实扫过
        var valid = options.Where(o => o.Valid).OrderBy(o => o.Path).ToList();
        var shown = valid.Count > 0 ? valid : options.OrderBy(o => o.Path).ToList();
        LstCandidates.ItemsSource = shown;

        TxtScanNote.Text = options.Count == 0
            ? "没扫到候选 —— 用「浏览…」自己挑吧。"
            : valid.Count > 0
                ? $"扫了 {options.Count} 个位置，{valid.Count} 个可用；没看到你的目录就用「浏览…」。"
                : $"扫了 {options.Count} 个位置，都没有 data\\*.pack —— 用「浏览…」自己挑吧。";
    }

    private void OnListDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (LstCandidates.SelectedItem is GameDirOption o) TxtManual.Text = o.Path;
    }

    private void OnManualChanged(object sender, TextChangedEventArgs e)
    {
        // 手动输入时取消列表选中，避免"到底用哪个"的歧义
        if (LstCandidates.SelectedItem is not null && TxtManual.Text != ((GameDirOption)LstCandidates.SelectedItem).Path)
            LstCandidates.SelectedItem = null;
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "选战锤3 游戏目录（含 data 那一层）",
            InitialDirectory = Directory.Exists(TxtManual.Text) ? TxtManual.Text : "C:\\",
        };
        if (dlg.ShowDialog() == true) TxtManual.Text = dlg.FolderName;
    }

    private void OnSkip(object sender, RoutedEventArgs e)
    {
        Result = null;
        Close();
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var chosen = (LstCandidates.SelectedItem as GameDirOption)?.Path;
        var manual = (TxtManual.Text ?? "").Trim();
        var path = chosen ?? manual;

        if (path.Length == 0)
        {
            MessageBox.Show(this, "还没选目录。点列表里的一条，或用「浏览…」挑一个；不想设就点「跳过」。",
                "还差一步", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!GameFinder.LooksLikeGame(path))
        {
            var r = MessageBox.Show(this,
                $"这个目录里没找到 data\\*.pack：\n{path}\n\n要选的通常是游戏根目录（里面有 data 文件夹）。\n仍然使用它吗？",
                "目录看着不对", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) return;
        }
        Result = path;
        Close();
    }
}
