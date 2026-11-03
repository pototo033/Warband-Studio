using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Data;
using WarbandStudio.Pack;

namespace WarbandStudio.Ui.Views;

/// <summary>保存前显式选择冲突规则；关闭或取消都不会提交半份选择。</summary>
public sealed class UpgradeRulesWindow : Window
{
    public Dictionary<string,string> Choices { get; } = new();
    public UpgradeRulesWindow(IReadOnlyList<UpgradeRuleConflict> conflicts)
    {
        Title = "选择重复兵的升级规则"; Width = 850; Height = 640;
        MinWidth = 620; MinHeight = 420; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(30,34,38)); Foreground = Brushes.WhiteSmoke;
        var root = new DockPanel { Margin = new Thickness(18) }; Content = root;
        var info = new TextBlock { Text = "同一实际单位的原生升级无法靠页签区分重叠规则。请为每项选择适用规则；取消会保留未保存编辑。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,14) };
        DockPanel.SetDock(info, Dock.Top); root.Children.Add(info);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0,14,0,0) };
        DockPanel.SetDock(buttons,Dock.Bottom); root.Children.Add(buttons);
        var cancel = new Button { Content = "取消保存", IsCancel = true, Padding = new Thickness(18,7,18,7), Margin = new Thickness(8,0,0,0) };
        var apply = new Button { Content = "应用选择并继续保存", Padding = new Thickness(18,7,18,7) };
        buttons.Children.Add(apply); buttons.Children.Add(cancel);
        var body = new StackPanel(); root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var selectors = new List<(UpgradeRuleConflict Conflict, ComboBox Selector)>();
        foreach (var conflict in conflicts)
        {
            var panel = new StackPanel { Margin = new Thickness(0,0,12,18) };
            panel.Children.Add(new TextBlock { Text = conflict.Description, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,6) });
            var text=new FrameworkElementFactory(typeof(TextBlock));
            text.SetBinding(TextBlock.TextProperty,new Binding(nameof(UpgradeRuleOption.Label)));
            text.SetValue(TextBlock.TextWrappingProperty,TextWrapping.Wrap);text.SetValue(FrameworkElement.MaxWidthProperty,720d);
            var selector = new ComboBox { ItemsSource = conflict.Options, ItemTemplate=new DataTemplate{VisualTree=text}, SelectedIndex = -1, MinHeight = 34, MaxDropDownHeight = 320 };
            panel.Children.Add(selector); selectors.Add((conflict,selector)); body.Children.Add(panel);
        }
        apply.Click += (_,_) =>
        {
            if (selectors.Any(x => x.Selector.SelectedItem is not UpgradeRuleOption))
            { MessageBox.Show(this,"请为所有冲突选择适用规则，或取消保存。",Title,MessageBoxButton.OK,MessageBoxImage.Information); return; }
            foreach (var (conflict,selector) in selectors) Choices[conflict.Id] = ((UpgradeRuleOption)selector.SelectedItem).Id;
            DialogResult = true;
        };
    }
}
