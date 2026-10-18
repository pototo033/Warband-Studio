using System.Windows;
using System.Windows.Controls;

namespace WarbandStudio.Ui.Services;

internal sealed class UpdateProgressWindow : Window
{
    private readonly ProgressBar _bar = new() { Height = 16, Minimum = 0, Maximum = 100, Margin = new Thickness(0, 12, 0, 12) };
    private readonly TextBlock _detail = new() { Text = "正在下载程序更新包…" };
    private bool _finished;
    public UpdateProgressWindow(string version, CancellationTokenSource cancel)
    {
        Title = "更新 WarbandStudio"; Width = 470; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "AeBrush.Surface1"); SetResourceReference(ForegroundProperty, "AeBrush.TextPrimary");
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock { Text = $"正在准备 v{version}", FontSize = 17, Margin = new Thickness(0, 0, 0, 12) });
        panel.Children.Add(_detail); panel.Children.Add(_bar);
        var button = new Button { Content = "取消更新", HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(14, 5, 14, 5) };
        button.Click += (_, _) => { cancel.Cancel(); Close(); };
        panel.Children.Add(button); Content = panel;
        Closing += (_, _) => { if (!_finished) cancel.Cancel(); };
    }
    public void Report(long received, long total)
    {
        _bar.Value = total > 0 ? received * 100d / total : 0;
        _detail.Text = received >= total ? "下载完成，正在校验程序文件…" : $"{received / 1048576d:F1} / {total / 1048576d:F1} MiB";
    }
    public void Finish() { _finished = true; if (IsVisible) Close(); }
}
