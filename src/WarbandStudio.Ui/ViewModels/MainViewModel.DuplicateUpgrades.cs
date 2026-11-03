using System.Windows;
using WarbandStudio.Pack;
using WarbandStudio.Ui.Views;

namespace WarbandStudio.Ui.ViewModels;

public sealed partial class MainViewModel
{
    private bool ResolveDuplicateUpgradeRules(string packPath, UpgradeRuleConflictException error)
    {
        var dialog = new UpgradeRulesWindow(error.Conflicts);
        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
        if (owner is not null) dialog.Owner = owner;
        if (dialog.ShowDialog() != true) { Status = "已取消保存：重复兵升级规则尚未选择，编辑保留。"; return false; }
        _backend.SetDuplicateUpgradeChoices(packPath,dialog.Choices);
        return true;
    }
}
