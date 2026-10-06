using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WarbandStudio.Ui.Views;

/// <summary>
/// 极简输入框（自绘，**不用 Microsoft.VisualBasic.InputBox** —— 那个需要 WinForms，
/// 跨平台构建里一调就抛 "Method requires System.Windows.Forms."）。
/// 只用来问一句短文本：素材后缀 / 页签新 key 之类。
/// </summary>
public static class InputDialog
{
    public static string? Ask(string title, string prompt, string initial = "", Window? owner = null)
    {
        var win = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = owner ?? Application.Current?.MainWindow,
            ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x1E, 0x21)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE4, 0xE7, 0xE9)),
        };
        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        var box = new TextBox
        {
            Text = initial, Padding = new Thickness(4, 3, 4, 3),
            Background = new SolidColorBrush(Color.FromRgb(0x15, 0x17, 0x19)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE4, 0xE7, 0xE9)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x34, 0x3A, 0x40)),
            CaretBrush = new SolidColorBrush(Color.FromRgb(0xE4, 0xE7, 0xE9)),
        };
        box.SelectAll();
        panel.Children.Add(box);
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var ok = new Button { Content = "确定", Padding = new Thickness(14, 3, 14, 3), Margin = new Thickness(0, 0, 6, 0), IsDefault = true };
        var cancel = new Button { Content = "取消", Padding = new Thickness(14, 3, 14, 3), IsCancel = true };
        string? result = null;
        ok.Click += (_, _) => { result = box.Text; win.DialogResult = true; };
        cancel.Click += (_, _) => { win.DialogResult = false; };
        row.Children.Add(ok);
        row.Children.Add(cancel);
        panel.Children.Add(row);
        win.Content = panel;
        box.Focus();
        return win.ShowDialog() == true ? result : null;
    }
}
