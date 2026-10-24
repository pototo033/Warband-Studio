using System.Reflection;
using WarbandStudio.Ui.Services;
using WarbandStudio.Ui.ViewModels;
using WarbandStudio.Ui.Views;
using System.Windows.Controls;
using System.Windows.Threading;

namespace WarbandStudio.Ui.Tests;

public sealed partial class BackendSafetyTests
{
    [Fact]
    public Task ResourceSearchSurvivesOpeningAndPopupResetsPreviousBottomScroll() => OnCostPickerSta(async () =>
    {
        var vm = new MainViewModel(_settings, ValidateFixturePack);
        await using var backend = (Backend)typeof(MainViewModel).GetField("_backend", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
        await backend.OpenPackAsync(MakePack("resource-search-scroll.pack"));
        await vm.RefreshPooledResourcesAsync(); vm.CostId = "C";
        // 多个候选制造真实滚动范围；复用资源记录保持图标和显示结构一致。
        var proto = vm.PooledResAll.First();
        for (var i = 0; i < 80; i++) vm.PooledResAll.Add(proto with { Key = $"test_resource_{i:00}", Name = $"test_resource_{i:00}" });
        var picker = new CostResourcePicker { DataContext = vm, Width = 260 };
        var host = new System.Windows.Window { Content = picker, Width = 300, Height = 120, Left = -10000, Top = -10000, ShowInTaskbar = false, ShowActivated = false };
        host.Show();
        try
        {
        picker.Measure(new System.Windows.Size(260, 30)); picker.Arrange(new System.Windows.Rect(0, 0, 260, 30));
        var input = (TextBox)picker.FindName("ResourceInput");
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        input.Text = "resource_07";
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        picker.IsDropDownOpen = true;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal("test_resource_07", Assert.Single(vm.PooledResShown).Key);
        input.Text = "no_match_resource";
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Empty(vm.PooledResShown);
        input.Text = "";
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var choices = (ListBox)picker.FindName("Choices");
        var scroll = (ScrollViewer)typeof(CostResourcePicker).GetMethod("FindScroll", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [choices])!;
        scroll.ScrollToEnd(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.True(scroll.VerticalOffset > 0);
        picker.IsDropDownOpen = false; picker.IsDropDownOpen = true;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(0, scroll.VerticalOffset);
        Assert.Equal(vm.PooledResAll.Count, picker.Items.Count);
        choices.SelectedItem = vm.PooledResAll.Single(r => r.Key == "test_resource_79");
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        picker.IsDropDownOpen = true;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(vm.PooledResAll.Count, picker.Items.Count);
        Assert.Equal(0, scroll.VerticalOffset);
        picker.IsDropDownOpen = false;
        }
        finally { picker.IsDropDownOpen = false; host.Close(); }
    });

    [Fact]
    public Task ResourcePickerInsideRightTabsDoesNotReloadDraftOnBubbledSelection() => OnCostPickerSta(async () =>
    {
        var vm = new MainViewModel(_settings, ValidateFixturePack);
        await using var backend = (Backend)typeof(MainViewModel).GetField("_backend", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
        await backend.OpenPackAsync(MakePack("routed-cost-picker.pack"));
        backend.Edit(() => backend.RemoveWarbandCostResource("C", "chiv_pool"));
        await vm.RefreshPooledResourcesAsync(); vm.CostId = "C";
        var picker = new CostResourcePicker { DataContext = vm };
        var costTab = new TabItem { Content = picker };
        var tabs = new TabControl { Items = { costTab }, SelectedItem = costTab };
        // 只装配真实控件树和生产事件处理器，不启动主窗口的 WebView/更新检查。
        var shell = (MainWindow)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        typeof(MainWindow).GetField("_vm", fields)!.SetValue(shell, vm);
        typeof(MainWindow).GetField("RightTabs", fields)!.SetValue(shell, tabs);
        typeof(MainWindow).GetField("CostTab", fields)!.SetValue(shell, costTab);
        var handler = (SelectionChangedEventHandler)typeof(MainWindow).GetMethod("OnRightTabsChanged", fields)!
            .CreateDelegate(typeof(SelectionChangedEventHandler), shell);
        tabs.SelectionChanged += handler;
        var bubbled = 0;
        tabs.AddHandler(System.Windows.Controls.Primitives.Selector.SelectionChangedEvent,
            new SelectionChangedEventHandler((_, e) => { if (!ReferenceEquals(e.OriginalSource, tabs)) bubbled++; }), true);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        picker.IsDropDownOpen = true;
        ((ListBox)picker.FindName("Choices")).SelectedItem = vm.PooledResAll.Single(r => r.Key == "chivalry");
        vm.CostResAmount = "-27";
        await Task.Delay(100);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.True(bubbled > 0);
        Assert.Equal("chivalry", picker.Text); Assert.Equal("-27", vm.CostResAmount);
        picker.IsDropDownOpen = true;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(vm.PooledResAll.Count, picker.Items.Count);
        picker.IsDropDownOpen = false;
        vm.CreateWarbandCost(); await vm.RefreshPooledResourcesAsync();
        Assert.Equal("chivalry", Assert.Single(vm.CostResourceLinks).Resource);
        Assert.Equal(-27, vm.CostResourceLinks.Single().Amount);
        tabs.SelectionChanged -= handler;
    });

    [Fact]
    public Task ResourceButtonsAppendReplaceRemoveAndConsecutiveEditsPreserveOtherLinks() => OnCostPickerSta(async () =>
    {
        var vm = new MainViewModel(_settings, ValidateFixturePack);
        await using var backend = (Backend)typeof(MainViewModel).GetField("_backend", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
        var path = MakePack("cost-actions.pack", ("db/pooled_resources_tables/extra", Table("pooled_resources_tables", new Dictionary<string, object>() { ["key"] = "extra" })));
        await backend.OpenPackAsync(path);
        await vm.RefreshPooledResourcesAsync(); vm.CostId = "C";
        vm.SelectedCostResourceLink = vm.CostResourceLinks.Single(r => r.Resource == "chivalry");
        vm.PickCostResource(vm.PooledResAll.Single(r => r.Key == "extra")); vm.CostResAmount = "-7";
        vm.AddCostResourceCommand.Execute(null);
        Assert.Equal(3, vm.CostResourceLinks.Count);
        Assert.Equal(-4, vm.CostResourceLinks.Single(r => r.Resource == "chivalry").Amount);
        // 不等待异步刷新：新增后立刻改值、删除、重新追加。
        vm.CostResAmount = "-8"; vm.CreateWarbandCost();
        Assert.Equal(-8, vm.SelectedCostResourceLink!.Amount);
        vm.RemoveCostResourceCommand.Execute(null);
        vm.PickCostResource(vm.PooledResAll.Single(r => r.Key == "extra")); vm.CostResAmount = "-9";
        vm.AddCostResourceCommand.Execute(null);
        Assert.Equal(3, vm.CostResourceLinks.Count);
        Assert.Equal(-9, vm.SelectedCostResourceLink!.Amount);
        vm.PickCostResource(vm.PooledResAll.Single(r => r.Key == "food"));
        vm.AddCostResourceCommand.Execute(null);
        Assert.Contains("已有配置", vm.CostStatus);
        vm.ReplaceCostResourceCommand.Execute(null);
        Assert.Contains("已有配置", vm.CostStatus);
        Assert.Equal(-3, vm.CostResourceLinks.Single(r => r.Resource == "food").Amount);
        vm.SelectedCostResourceLink = vm.CostResourceLinks.Single(r => r.Resource == "extra");
        vm.RemoveCostResourceCommand.Execute(null);
        await vm.RefreshPooledResourcesAsync();
        vm.SelectedCostResourceLink = vm.CostResourceLinks.Single(r => r.Resource == "chivalry");
        vm.PickCostResource(vm.PooledResAll.Single(r => r.Key == "extra")); vm.CostResAmount = "-11";
        vm.ReplaceCostResourceCommand.Execute(null);
        Assert.Equal(2, vm.CostResourceLinks.Count);
        Assert.Equal("percentage", vm.SelectedCostResourceLink!.Context);
        Assert.Equal("secondary", vm.SelectedCostResourceLink.UiPooled);
        Assert.True(backend.Undo());
        Assert.Contains(backend.CostList().Single(c => c.Id == "C").Resources, r => r.Resource == "chivalry");
        Assert.True(backend.Redo());
        await backend.SaveInPlaceAsync(); await backend.OpenPackAsync(path);
        var links = backend.CostList().Single(c => c.Id == "C").Resources;
        Assert.Equal(2, links.Count); Assert.Equal(-3, links.Single(r => r.Resource == "food").Amount);
        Assert.Equal(-11, links.Single(r => r.Resource == "extra").Amount);
    });

    private static Task OnCostPickerSta(Func<Task> action)
    {
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>
        {
            var dispatcher=Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async ()=>
            {
                try { await action();done.SetResult(); }
                catch(Exception ex) { done.SetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        }) { IsBackground=true };
        thread.SetApartmentState(ApartmentState.STA);thread.Start();
        return done.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public Task ActualWpfPickerUpdatesVisibleTextAndCanReaddRemovedResource()=>OnCostPickerSta(async ()=>
    {
        var vm=new MainViewModel(_settings,ValidateFixturePack);
        await using var backend=(Backend)typeof(MainViewModel).GetField("_backend",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(vm)!;
        await backend.OpenPackAsync(MakePack("actual-resource-picker.pack"));
        backend.Edit(()=>backend.RemoveWarbandCostResource("C","chiv_pool"));
        await vm.RefreshPooledResourcesAsync();vm.CostId="C";
        var picker=new CostResourcePicker{DataContext=vm};
        var choices=(ListBox)picker.FindName("Choices");var input=(TextBox)picker.FindName("ResourceInput");
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        void Select(string key)
        {
            picker.IsDropDownOpen=true;
            choices.SelectedItem=vm.PooledResAll.Single(r=>r.Key==key);
        }
        Select("chivalry");await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal("chivalry",picker.Text);Assert.Equal("chivalry",vm.CostResText);
        Assert.False(picker.IsDropDownOpen);Assert.Null(choices.SelectedItem);
        input.Text="food";await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal("food",vm.CostResText);
        input.Text="";await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); // 清除搜索后才选择其他资源。
        Select("chivalry");await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal("chivalry",picker.Text); // 同一选项可以重新选。
        vm.CostResAmount="-17";vm.CreateWarbandCost();await vm.RefreshPooledResourcesAsync();
        Assert.Equal(-17,Assert.Single(backend.CostList().Single(c=>c.Id=="C").Resources).Amount);
        vm.RemoveCostResourceCommand.Execute(null);await vm.RefreshPooledResourcesAsync();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Empty(backend.CostList().Single(c=>c.Id=="C").Resources);
        Assert.Equal("",picker.Text);Assert.Null(vm.SelectedCostResourceLink);
        Select("chivalry");await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal("chivalry",picker.Text);
        vm.CostResAmount="-9";vm.CreateWarbandCost();await vm.RefreshPooledResourcesAsync();
        Assert.Equal(-9,Assert.Single(backend.CostList().Single(c=>c.Id=="C").Resources).Amount);
        backend.Edit(()=>backend.UpdateWarbandCost("C",0,"food",-3,true,addResource:true));
        await vm.RefreshPooledResourcesAsync();
        vm.SelectedCostResourceLink=vm.CostResourceLinks.Single(r=>r.Resource=="chivalry");
        vm.RemoveCostResourceCommand.Execute(null);await vm.RefreshPooledResourcesAsync();
        Select("chivalry");vm.CostResAmount="-13";vm.CreateWarbandCost();await vm.RefreshPooledResourcesAsync();
        var links=backend.CostList().Single(c=>c.Id=="C").Resources;
        Assert.Equal(2,links.Count);Assert.Equal(-3,links.Single(r=>r.Resource=="food").Amount);
        Assert.Equal(-13,links.Single(r=>r.Resource=="chivalry").Amount);
        picker.IsDropDownOpen=false;
        var path=backend.Pack!.PackPath!;
        await backend.SaveInPlaceAsync();await backend.OpenPackAsync(path);
        links=backend.CostList().Single(c=>c.Id=="C").Resources;
        Assert.Equal(2,links.Count);Assert.Equal(-3,links.Single(r=>r.Resource=="food").Amount);
        Assert.Equal(-13,links.Single(r=>r.Resource=="chivalry").Amount);
    });
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResourcePickerCanReplaceExistingAndNewCostThenUndoSaveReopen(bool newlyCreated)
    {
        var vm=new MainViewModel(_settings,ValidateFixturePack);
        var backend=(Backend)typeof(MainViewModel).GetField("_backend",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(vm)!;
        await using var lifetime=backend;
        var path=MakePack("resource-picker.pack");
        await backend.OpenPackAsync(path);
        backend.Edit(()=>backend.RemoveWarbandCostResource("C","chiv_pool"));
        var id=newlyCreated ? "new_picker_cost" : "C";
        if(newlyCreated) backend.Edit(()=>backend.CreateWarbandCost(id,0,"food",-2));
        await vm.RefreshPooledResourcesAsync();vm.CostId=id;
        Assert.Equal("food",vm.CostResText);
        vm.CostResText="FOOD"; // 模拟可编辑下拉的文字回写，触发筛选。
        Assert.DoesNotContain(vm.PooledResShown,r=>r.Key=="chivalry");
        vm.ShowAllCostResourceChoices();
        Assert.Contains(vm.PooledResShown,r=>r.Key=="chivalry");
        Assert.Equal("FOOD",vm.CostResText);
        vm.NotePickedRes("chivalry");vm.CostResText="chivalry";vm.CostResAmount="-17";
        vm.CreateWarbandCost();
        var link=Assert.Single(backend.CostList().Single(c=>c.Id==id).Resources);
        Assert.Equal("chivalry",link.Resource);Assert.Equal(-17,link.Amount);
        Assert.True(backend.Undo());Assert.Equal("food",Assert.Single(backend.CostList().Single(c=>c.Id==id).Resources).Resource);
        Assert.True(backend.Redo());
        await backend.SaveInPlaceAsync();await backend.OpenPackAsync(path);
        link=Assert.Single(backend.CostList().Single(c=>c.Id==id).Resources);
        Assert.Equal("chivalry",link.Resource);Assert.Equal(-17,link.Amount);
        await vm.RefreshPooledResourcesAsync();
        vm.CostResText="food";vm.CostResAmount="-3";vm.CreateWarbandCost();
        Assert.Equal("food",Assert.Single(backend.CostList().Single(c=>c.Id==id).Resources).Resource);
        await vm.RefreshPooledResourcesAsync();
        vm.CostResText="";vm.CreateWarbandCost();Assert.Empty(backend.CostList().Single(c=>c.Id==id).Resources);
    }

    [Fact]
    public async Task UnresolvedResourceTextCannotSilentlyRemoveAnExistingAssociation()
    {
        var vm=new MainViewModel(_settings,ValidateFixturePack);
        var backend=(Backend)typeof(MainViewModel).GetField("_backend",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(vm)!;
        await using var lifetime=backend;await backend.OpenPackAsync(_pack);
        await vm.RefreshPooledResourcesAsync();vm.CostId="C";
        vm.CostResText="unknown_resource_partial";vm.CreateWarbandCost();
        Assert.Contains("有效",vm.CostStatus);Assert.True(backend.Edits.IsEmpty);
        Assert.Equal(2,backend.CostList().Single(c=>c.Id=="C").Resources.Count);
    }
}
