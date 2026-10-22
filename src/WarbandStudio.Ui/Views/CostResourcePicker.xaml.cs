using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WarbandStudio.Ui.Services;
using WarbandStudio.Ui.ViewModels;

namespace WarbandStudio.Ui.Views;

// 输入文本与候选列表各有一个来源，避免可编辑 ComboBox 在 SelectionChanged 后覆盖表单文本。
public partial class CostResourcePicker : UserControl
{
    private bool _choosing;
    public CostResourcePicker() => InitializeComponent();
    public string Text => ResourceInput.Text;
    public ItemCollection Items => Choices.Items;
    public bool IsDropDownOpen { get => Toggle.IsChecked == true; set => Toggle.IsChecked = value; }
    private void OnOpened(object sender, RoutedEventArgs e)
    {
        Choices.SelectedItem = null;
        if (DataContext is MainViewModel vm) vm.ShowAllCostResourceChoices();
        FileLog.Write($"[ui] 选用资源下拉展开：{Items.Count} 条");
    }
    private void OnChoiceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_choosing || !IsDropDownOpen || e.AddedItems.Count == 0 || e.AddedItems[0] is not Backend.PooledResource pick
            || DataContext is not MainViewModel vm) return;
        _choosing = true;
        try
        {
            vm.PickCostResource(pick);
            IsDropDownOpen = false;
            Choices.SelectedItem = null; // 下次可再次选同一项，包括移除关联之后。
        }
        finally { _choosing = false; }
    }
    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down) { IsDropDownOpen = true; Choices.Focus(); e.Handled = true; }
        else if (e.Key == Key.Escape) { IsDropDownOpen = false; e.Handled = true; }
    }
    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { IsDropDownOpen = false; ResourceInput.Focus(); e.Handled = true; }
    }
}
