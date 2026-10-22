using System.Windows;
using System.Windows.Controls;

namespace WarbandStudio.Ui.Views;

public sealed class SaveFailureDialog : Window
{
    public SaveFailureDialog(string detail)
    {
        Title = "保存 / 导出失败"; Width = 850; Height = 450; MinWidth = 550; MinHeight = 250;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "AeBrush.Surface1");
        SetResourceReference(ForegroundProperty, "AeBrush.TextPrimary");
        var panel = new DockPanel { Margin = new Thickness(16) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var export = new Button { Content = "导出编辑与日志…", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0) };
        export.Click += (_, _) => DialogResult = true;
        var close = new Button { Content = "关闭", IsCancel = true, Padding = new Thickness(12, 5, 12, 5) };
        buttons.Children.Add(export); buttons.Children.Add(close); DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
        var text = new TextBox { Text = detail, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            VerticalContentAlignment = VerticalAlignment.Top, HorizontalContentAlignment = HorizontalAlignment.Left, FontSize = 13,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        panel.Children.Add(text); Content = panel;
    }
}
