using WarbandStudio.Pack;
using WarbandStudio.Ui.Services;

namespace WarbandStudio.Ui.Tests;

public sealed partial class BackendSafetyTests
{
    [Fact]
    public Task ConflictWindowRequiresAnExplicitChoiceAndCancelDoesNotCommit()=>OnCostPickerSta(() =>
    {
        var conflict=new UpgradeRuleConflict("conflict","同一单位升级的两种成本",[new("r1","原页：100 金币"),new("r2","复制页：500 金币")]);
        var window=new WarbandStudio.Ui.Views.UpgradeRulesWindow([conflict]){Left=-10000,Top=-10000,ShowInTaskbar=false,WindowStartupLocation=System.Windows.WindowStartupLocation.Manual};
        var root=(System.Windows.Controls.DockPanel)window.Content;
        var body=(System.Windows.Controls.StackPanel)root.Children.OfType<System.Windows.Controls.ScrollViewer>().Single().Content;
        var selector=((System.Windows.Controls.StackPanel)body.Children[0]).Children.OfType<System.Windows.Controls.ComboBox>().Single();
        Assert.Equal(-1,selector.SelectedIndex);Assert.Empty(window.Choices);
        window.Dispatcher.BeginInvoke(() =>
        {
            selector.SelectedIndex=1;
            root.Children.OfType<System.Windows.Controls.StackPanel>().Single().Children.OfType<System.Windows.Controls.Button>().First().RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        });
        Assert.True(window.ShowDialog());Assert.Equal("r2",window.Choices["conflict"]);
        var cancelled=new WarbandStudio.Ui.Views.UpgradeRulesWindow([conflict]){Left=-10000,Top=-10000,ShowInTaskbar=false,WindowStartupLocation=System.Windows.WindowStartupLocation.Manual};
        cancelled.Dispatcher.BeginInvoke(cancelled.Close);Assert.False(cancelled.ShowDialog()??false);Assert.Empty(cancelled.Choices);
        return Task.CompletedTask;
    });
    [Fact]
    public async Task DuplicateRuleChoicesUndoRedoRecoverAndStayWithTheirPack()
    {
        var first=_backend.Pack!.PackPath!;
        _backend.SetDuplicateUpgradeChoices(first,new Dictionary<string,string>{{"rule","r1"}});
        Assert.Equal("r1",_backend.Edits.DuplicateUpgradeChoices["rule"]);
        Assert.True(_backend.Undo());Assert.Empty(_backend.Edits.DuplicateUpgradeChoices);
        Assert.True(_backend.Redo());
        _backend.SetDuplicateUpgradeChoices(first,new Dictionary<string,string>{{"rule","r2"}});
        Assert.True(_backend.Undo());Assert.Equal("r1",_backend.Edits.DuplicateUpgradeChoices["rule"]);
        Assert.True(_backend.Redo());Assert.Equal("r2",_backend.Edits.DuplicateUpgradeChoices["rule"]);
        var recovery=_backend.CaptureUpdateRecovery();
        Assert.Equal("r2",recovery.Packs.Single(p=>p.OriginalPath==first).Edits.DuplicateUpgradeChoices["rule"]);
        await _backend.OpenPackAsync(MakePack("second.pack"));
        Assert.Empty(_backend.Edits.DuplicateUpgradeChoices);
        _backend.SetDuplicateUpgradeChoices(first,new Dictionary<string,string>{{"rule","r3"}});
        Assert.Empty(_backend.Edits.DuplicateUpgradeChoices);
        await _backend.OpenPackAsync(first);Assert.Equal("r3",_backend.Edits.DuplicateUpgradeChoices["rule"]);
    }
}
