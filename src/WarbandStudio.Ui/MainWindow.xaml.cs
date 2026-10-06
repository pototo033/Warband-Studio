using System.Text.Json;
using System.Windows;
using WarbandStudio.Ui.Services;
using WarbandStudio.Ui.ViewModels;
using WarbandStudio.Ui.Views;

namespace WarbandStudio.Ui;

/// <summary>
/// 主窗口：三栏壳 + 启动流程（起后端 → 首跑问游戏目录 → schema 自举）。
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private System.Windows.Point? _dragFrom;   // 兵种库拖拽起点（判断是不是"拖"而不是"点"）

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        _vm.GameDirRequested += AskGameDirAsync;
        _vm.PackOpened += PushCanvasData;
        _vm.UnitPicked += (unit, url) => CanvasHost.SendAddUnit(unit, url);
        _vm.FilterRequested += (race, faction) => CanvasHost.SendFilter(race, faction);
        // 关画布：未导出的编辑怎么办（是=导出 / 否=放弃 / 取消=不关）
        _vm.AskSave = (title, summary) =>
        {
            var r = MessageBox.Show(
                $"「{title}」还有未导出的编辑：{summary}" + Environment.NewLine + Environment.NewLine +
                "「是」= 现在导出（会让你选保存位置）" + Environment.NewLine +
                "「否」= 放弃这些编辑并关闭画布" + Environment.NewLine +
                "「取消」= 什么都不做，保持打开",
                "WarbandStudio", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            return r == MessageBoxResult.Yes ? 0 : r == MessageBoxResult.No ? 1 : 2;
        };
        // 切画布页签 → 切到那个包的画布（重推它的数据）
        _vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ViewModels.MainViewModel.ActiveCenterTab)) _vm.OnCenterTabChanged(); };
        _vm.PackOpened += async () => await _vm.LoadFactionsAsync();
        CanvasHost.EditRequested += json => _vm.ApplyCanvasEdit(json);
        // 兵种库 ← 画布：把兵牌拖进兵种库 = 从组里删掉这个兵
        ListLibrary.AllowDrop = true;
        ListLibrary.Drop += OnLibraryDrop;
        Loaded += async (_, _) =>
        {
            await _vm.InitializeAsync();      // 里面会打开原版战帮升级 → 触发 PackOpened → 推画布数据
            await RunStartupArgsAsync();
            // 第一次打开：自动放一遍教程（放过就记上；工具栏/全局选项里能再看）
            if (!_vm.TourDone) { await Task.Delay(700); StartTour(); }
            // **启动时检查更新**（只提示、不自动替换；延迟几秒、后台跑，失败静默）
            await Task.Delay(5000);
            _ = _vm.CheckUpdatesAsync();
        };
        // 成本工坊两个下拉展开时各记一行（含选项条数）：以后"点了没反应"看日志就能判 ——
        // 没有这一行 = 点击没到达下拉；有这一行但 0 条 = 清单是空的（v0.145 用户报的就是这种）。
        // **顺手清掉"和当前文字对不上的旧选中项"**：打字之后旧选中项还挂着，这时点回同一项
        // 不会触发 SelectionChanged → 看着就是"点了没反应"（用户实测）。
        CmbCostId.DropDownOpened += (_, _) =>
        {
            // 打字之后旧选中项还挂着：点回同一项不会触发 SelectionChanged（"点了没反应"）→ 展开时先清掉它。
            // **清完必须把文字补回来**：清选中项会让可编辑下拉把输入框也清空（用户实测"展开就清空我输入的 id"）。
            var text = CmbCostId.Text;
            if (CmbCostId.SelectedItem is Services.Backend.WarbandCost c &&
                !string.Equals(text, c.Id, StringComparison.Ordinal))
            {
                CmbCostId.SelectedItem = null;
                if (!string.Equals(CmbCostId.Text, text, StringComparison.Ordinal))
                {
                    CmbCostId.Text = text;
                    _vm.CostId = text;
                }
            }
            Services.FileLog.Write($"[ui] 成本 id 下拉展开：{CmbCostId.Items.Count} 条");
        };
        CmbCostRes.DropDownOpened += (_, _) =>
        {
            var text = CmbCostRes.Text;
            if (CmbCostRes.SelectedItem is Services.Backend.PooledResource r &&
                !string.Equals(text, r.Name, StringComparison.Ordinal) &&
                !string.Equals(text, r.Key, StringComparison.Ordinal))
            {
                CmbCostRes.SelectedItem = null;
                if (!string.Equals(CmbCostRes.Text, text, StringComparison.Ordinal))
                {
                    CmbCostRes.Text = text;
                    _vm.CostResText = text;
                }
            }
            Services.FileLog.Write($"[ui] 选用资源下拉展开：{CmbCostRes.Items.Count} 条");
        };
    }

    /// <summary>成本工坊：「重命名…」→ 把当前成本 id 改成新名字（保存后旧 id 那条会被删掉）。</summary>
    private void OnRenameCost(object sender, RoutedEventArgs e)
    {
        var oldId = (_vm.CostId ?? "").Trim();
        if (oldId.Length == 0) { _vm.SetStatus("先在「成本 id」里选/填一个要改名的成本"); return; }
        var newId = Views.InputDialog.Ask("重命名成本 id", $"把「{oldId}」改成：", oldId, this);
        if (string.IsNullOrWhiteSpace(newId)) return;
        _vm.RenameCurrentCost(newId.Trim());
    }

    /// <summary>"选择游戏目录"框（由 VM 在需要时触发；启动时和左下「全局选项」都用它）。</summary>
    private Task<string?> AskGameDirAsync(string? current)
    {
        var dlg = new GameDirDialog(current) { Owner = this };
        dlg.ShowDialog();
        return Task.FromResult(dlg.Result);
    }

    private void OnExit(object sender, RoutedEventArgs e) => Close();

    /// <summary>页签上的 × → 关页签（画布页签会先问未导出的编辑）。</summary>
    private async void OnCloseTab(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is ViewModels.CenterTab tab)
            await _vm.CloseTabAsync(tab);
    }

    /// <summary>文件树右键 → 在新画布中打开该包。</summary>
    private async void OnOpenCanvasForNode(object sender, RoutedEventArgs e)
    {
        if (TreeFiles.SelectedItem is ViewModels.TreeItem item) await _vm.OpenCanvasForNodeAsync(item);
    }

    /// <summary>右栏选中种族/派系 → 刷新兵种库。</summary>
    private void OnFactionSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is ViewModels.FactionNode node) _vm.SelectedFaction = node;
    }

    /// <summary>兵种库双击一张牌 → 放到画布（新开一组）。</summary>
    private void OnLibraryDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // 种族兵种库和 mod 兵种库用的是同一套处理（谁触发算谁的）
        if ((sender as System.Windows.Controls.ListBox)?.SelectedItem is ViewModels.UnitCard card) _vm.PickUnit(card);
    }

    /// <summary>「mod 兵种」页：选中包（= 全部表）或某个 main_units 表文件 → 换下面的兵种库。</summary>
    private void OnModSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is ViewModels.MainViewModel.ModItem m) _vm.PickModNode(m);
    }

    /// <summary>左下「UI 素材库」右键 → 把这张图加进当前编辑的包（导出时落盘；不动画布）。</summary>
    /// <summary>
    /// 可编辑 ComboBox 的"箭头区兜底"：点在最右侧 30px（箭头那一块）就把下拉展开。
    /// 主题里的 ComboBox 模板是手写的，箭头点了不弹（实测），这里不依赖模板行为直接开。
    /// </summary>
    private void OnComboArrowDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.ComboBox cb) return;
        if (cb.IsDropDownOpen) return;
        if (e.GetPosition(cb).X < cb.ActualWidth - 30) return;     // 左侧文字区照常打字/选中，不抢
        cb.IsDropDownOpen = true;
        e.Handled = true;
    }

    // 成本工坊：创建（三张表一起写，预览在面板上；结果写进 CostStatus）
    private void OnCreateCost(object sender, RoutedEventArgs e) => _vm.CreateWarbandCost();

    /// <summary>切到「成本工坊」页就现算一遍清单（成本 id / 选用资源两个下拉）——
    /// 派生状态别只靠"打开包那一条路"刷新：切页签、切画布、保存重开后看到的都得是当前包的清单。</summary>
    private void OnRightTabsChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(RightTabs.SelectedItem, CostTab) && _vm.PackOpen) _vm.RefreshPooledResources();
    }

    private void OnRefreshResources(object sender, RoutedEventArgs e) => _vm.RefreshPooledResources();

    // 成本 id 下拉里选了某个已有成本 → 交给 VM 预填（金币/资源/按钮文字），
    // 并把输入框**钉死成成本 id**：可编辑下拉默认会把选中项的 ToString 填进去（记录全文，是乱码），
    // 那段乱码会被 VM 的过滤链当成筛选词、把清单过滤成空 → 用户看到的"点了不填入"（实测）。
    private void OnCostPickChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (sender is not System.Windows.Controls.ComboBox cb) return;
        var pick = (e.AddedItems.Count > 0 ? e.AddedItems[0] : null) as Services.Backend.WarbandCost
                   ?? cb.SelectedItem as Services.Backend.WarbandCost;
        if (pick is null) return;
        if (!string.Equals(cb.Text, pick.Id, StringComparison.Ordinal)) cb.Text = pick.Id;
        _vm.NotePickedCost(pick.Id);      // 过滤时保留这条（否则改字时被 ComboBox 连输入框一起清空）
        _vm.CostId = pick.Id;
    }

    // 资源下拉里选了某一项 → 同样把输入框钉死成**资源名**（可编辑下拉默认填记录 ToString 乱码；
    // VM 的 ResolvedResource() 按 名称/key 解析，所以填 Name（空则退回 key））
    private void OnCostResPickChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (sender is not System.Windows.Controls.ComboBox cb) return;
        var pick = (e.AddedItems.Count > 0 ? e.AddedItems[0] : null) as Services.Backend.PooledResource
                   ?? cb.SelectedItem as Services.Backend.PooledResource;
        if (pick is null) return;
        var text = pick.Name.Length > 0 ? pick.Name : pick.Key;
        if (!string.Equals(cb.Text, text, StringComparison.Ordinal)) cb.Text = text;
        _vm.NotePickedRes(pick.Key);      // 过滤时保留这条（同成本下拉）
        _vm.CostResText = text;
    }

    // 删成本：先问一句（删的是原文件里的行，撤销不了）
    private void OnDeleteCost(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_vm.CostId)) return;
        var yes = System.Windows.MessageBox.Show(
            $"确定删除成本「{_vm.CostId}」吗？\n会删掉 resource_costs_tables 里那一行 + 它的资源关联（改原文件，保存后生效；资源池留着）。",
            "删除成本", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
        if (yes) _vm.DeleteWarbandCost();
    }

    private void OnUiAssetAddToPack(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not ViewModels.UiAssetItem item) return;
        // **不问名字了**：写进包的名字由后端自动定（素材 key + _1/_2… 防重名），
        // 真要改名用文件树右键「重命名」；要给页签用就走「换图」（那一步会按页签 key 命名）。
        _vm.AddUiAssetToPack(item);
    }

    /// <summary>
    /// 分栏拖动完成 → 逼画布（WebView2）重算尺寸。
    /// WebView2 是独立合成面，拖分栏时它不会跟着 Grid 实时重算，看起来就是"只有画布动了、左右栏没变"。
    /// </summary>
    private void OnSplitterDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
        => CanvasHost.NudgeSize();

    /// <summary>文件树右键 →「删除（这个文件）」：待导出新增的撤掉；包内条目会在导出时跳过。</summary>
    private void OnTreeDeleteFile(object sender, RoutedEventArgs e)
    {
        if (TreeFiles.SelectedItem is not ViewModels.TreeItem item || item.IsFolder) return;
        if (System.Windows.MessageBox.Show($"删除这个文件？{item.Path}　（导出时不再写进包里；保存后才会从原包消失）",
                "删除文件", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        _vm.RemovePackFile(item);
    }

    /// <summary>文件树右键 →「重命名…」。</summary>
    /// <summary>使用教程的步骤（"PPT 式"：一步指一个地方 + 说清那块是干嘛的）。</summary>
    private List<Views.TourWindow.Step> TourSteps() =>
    [
        new("", "欢迎使用战帮树工坊",
            "这是一个以 pack 为中心的战帮树编辑工具：打开 .pack → 在画布上摆兵牌 / 连升级 → 保存写回。\n" +
            "下面用几步把各个区域指给你看。随时可以「跳过教程」，之后在左上工具栏「教程」或左下「全局选项 → 再看教程」里重看。\n"),
        new("TreeFiles", "① 左栏：文件树",
            "打开包后这里列出包里的文件（双击表文件 = 打开表视图）。\n" +
            "红色加粗 = 这次保存会动到的文件；「（待导出）」= 还没写进包的新文件。\n" +
            "右键：重命名 / 删除 / 打开所在文件夹（定位到包）。\n"),
        new("CanvasHost", "② 中间：战帮画布",
            "每个包一张画布。左上「选择 / 吸附 / 无连线」。\n" +
            "拖兵牌 = 改坐标（拖动即应用，保存时落表；点选不会误挪）；点两个组「链接」= 建升级；点连线 = 改金额/等级、交换方向、删除；升级线上的箭头表示方向（parent → child）。\n" +
            "右侧页签列：切换 / 新建 / 重命名 / 换图 / 从其他 mod 导入页签。「无连线」会把有升级没画线的对用黄色虚线画出来（只在画布上，不写表）。\n"),
        new("RightTabs", "③ 右上：种族 / 派系 与 成本工坊",
            "第一页按种族 / 派系筛兵（选完画布自动切到对应页签）；\n" +
            "第二页「成本工坊」新建/修改升级成本（金币 + 其他资源），成本写进当前打开的那个包（面板上会写明包名）。\n"),
        new("ListLibrary", "④ 右下：兵种库",
            "实时读数据库：双击一张牌 = 往画布加一个组；也可以直接拖到画布上。\n" +
            "灰显 = 已经在画布上；搜索框里 key 和中文名都能搜。\n"),
        new("CostTab", "⑤ 成本工坊",
            "一次写三张表：resource_costs（金币）、资源池、成本↔资源池（其他资源）。\n" +
            "成本 id 只列打开包里的；「重命名…」= 删旧 + 写新（升级路线会跟着改名）；改完点「保存」写回包。\n"),
        new("LeftTabs", "⑥ 左下：全局选项 / 诊断·引用 / UI 素材库",
            "全局选项：游戏目录、备份文件夹（保存时把原包备份到这里，带时间戳、不覆盖旧的）；\n" +
            "UI 素材库：本地素材，右键「添加到当前包」；诊断·引用：引用体检结果。\n"),
    ];

    /// <summary>全局选项 →「检查更新」（启动时也会自动查一次）。</summary>
    private void OnCheckUpdates(object sender, RoutedEventArgs e) => _ = _vm.CheckUpdatesAsync();

    /// <summary>开始/再看教程（左上工具栏「教程」、左下「全局选项 → 再看教程」都调它）。</summary>
    private void OnStartTour(object sender, RoutedEventArgs e) => StartTour();

    private void StartTour()
    {
        try
        {
            _vm.TourDone = true;          // 放过一遍就记上（不再自动弹；随时能重看）
            new Views.TourWindow(this, TourSteps()).Show();
        }
        catch (Exception ex) { _vm.SetStatus("教程打开失败：" + ex.Message); }
    }

    /// <summary>面板上的小问号：单步说明卡（复用教程窗口）。</summary>
    private void ShowPanelHelp(string key)
    {
        var step = TourSteps().FirstOrDefault(x => x.Title.Contains(key, StringComparison.Ordinal));
        if (step is null) return;
        try { new Views.TourWindow(this, [step], single: true).Show(); }
        catch { }
    }
    private void OnHelpTree(object sender, RoutedEventArgs e) => ShowPanelHelp("文件树");
    private void OnHelpFaction(object sender, RoutedEventArgs e) => ShowPanelHelp("种族");
    private void OnHelpLibrary(object sender, RoutedEventArgs e) => ShowPanelHelp("兵种库");
    private void OnHelpCost(object sender, RoutedEventArgs e) => ShowPanelHelp("成本工坊");

    /// <summary>文件树右键 → 打开所在文件夹：包内条目没有真实目录 → 打开**这个包文件所在目录**并选中它。</summary>
    private void OnTreeOpenFolder(object sender, RoutedEventArgs e)
    {
        var packPath = _vm.CurrentPackPath;
        if (string.IsNullOrWhiteSpace(packPath) || !System.IO.File.Exists(packPath))
        { _vm.SetStatus("先打开一个包"); return; }
        try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + packPath + "\""); }
        catch (Exception ex) { _vm.SetStatus("打开文件夹失败：" + ex.Message); }
    }

    /// <summary>全局选项：选备份文件夹 / 打开它。</summary>
    private void OnChooseBackupDir(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择备份文件夹", InitialDirectory = _vm.BackupDir };
        if (dlg.ShowDialog(this) == true) _vm.BackupDir = dlg.FolderName;
    }
    private void OnOpenBackupDir(object sender, RoutedEventArgs e)
    {
        try
        {
            System.IO.Directory.CreateDirectory(_vm.BackupDir);
            System.Diagnostics.Process.Start("explorer.exe", _vm.BackupDir);
        }
        catch (Exception ex) { _vm.SetStatus("打开备份文件夹失败：" + ex.Message); }
    }

    private void OnTreeRenameFile(object sender, RoutedEventArgs e)
    {
        if (TreeFiles.SelectedItem is not ViewModels.TreeItem item || item.IsFolder) return;
        var cur = item.Path.Contains('/') ? item.Path[(item.Path.LastIndexOf('/') + 1)..] : item.Path;
        var name = Views.InputDialog.Ask("重命名文件", $"新文件名（导出时生效；目录不变）：", cur, this);
        if (string.IsNullOrWhiteSpace(name)) return;
        _vm.RenamePackFile(item, name);
    }

    /// <summary>左下「UI 素材库」右键 → 复制"写进包时的路径"。</summary>
    private void OnUiAssetCopyPath(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not ViewModels.UiAssetItem item) return;
        try
        {
            System.Windows.Clipboard.SetText(item.PackPath);
            _vm.SetStatus("已复制（写进包时的路径）：" + item.PackPath);
        }
        catch { /* 剪贴板被别的程序占着就算了 */ }
    }

    /// <summary>左下「UI 素材库」右键 →「重命名…」：改素材库里的文件后缀（前缀按类型保留）。</summary>
    private void OnUiAssetRename(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not ViewModels.UiAssetItem item) return;
        var prefix = item.Kind == "btn" ? "button_upgrade_" : item.Kind == "bg" ? "background_images_" : "ui_";
        var cur = item.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? item.Name[prefix.Length..].Replace(".png", "")
            : (item.Name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? item.Name[..^4] : item.Name);
        var input = Views.InputDialog.Ask("重命名素材",
            $"新的名字（后缀）：素材库里会叫 {prefix}<后缀>.png（注：这只改素材库里的名字；写进包里的名字在「添加到当前包」时填）", cur, this);
        if (input is null) return;
        _vm.RenameUiAsset(item, input.Trim());
    }

    /// <summary>左下「UI 素材库」→「导入背景图…/导入按钮图…」：选 PNG 复制进本地素材库（按按钮上的 Tag 定类型）。</summary>
    private void OnUiAssetImport(object sender, RoutedEventArgs e)
    {
        var kind = (sender as FrameworkElement)?.Tag as string == "btn" ? "btn" : "bg";
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = kind == "btn" ? "导入按钮图（.png，可多选）" : "导入背景图（.png，可多选）",
            Filter = "PNG 图片 (*.png)|*.png",
            Multiselect = true,
        };
        if (dlg.ShowDialog() != true) return;
        _vm.ImportUiAssets(dlg.FileNames, kind);
    }

    /// <summary>左下「UI 素材库」右键 → 从素材库移除（只删本地那份）。</summary>
    private void OnUiAssetRemove(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not ViewModels.UiAssetItem item) return;
        if (_vm.Confirm is not null && !_vm.Confirm($"从素材库移除「{item.Name}」？\n（只删本地那份，不动任何包）")) return;
        _vm.RemoveUiAsset(item);
    }

    /// <summary>从兵种库把一张牌拖出来（放到画布上 = 新开一组）。</summary>
    private void OnLibraryDragStart(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _dragFrom = e.GetPosition(sender as System.Windows.Controls.ListBox ?? ListLibrary);
    }

    private void OnLibraryMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed || _dragFrom is null) return;
        var lb = sender as System.Windows.Controls.ListBox ?? ListLibrary;
        var now = e.GetPosition(lb);
        if (Math.Abs(now.X - _dragFrom.Value.X) < 6 && Math.Abs(now.Y - _dragFrom.Value.Y) < 6) return;
        if (lb.SelectedItem is not ViewModels.UnitCard card) return;
        _dragFrom = null;
        // Chromium 是标准的 OLE 拖放目标：把兵种 key 当纯文本带过去，页面里 drop 就能收到
        var data = new System.Windows.DataObject(System.Windows.DataFormats.UnicodeText, card.Key);
        System.Windows.DragDrop.DoDragDrop(ListLibrary, data, System.Windows.DragDropEffects.Copy);
    }

    /// <summary>兵牌拖进兵种库 = 把这兵从它的组里删掉（记成待导出，导出时改 junction）。</summary>
    private void OnLibraryDrop(object sender, System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(System.Windows.DataFormats.UnicodeText)) return;
        var text = e.Data.GetData(System.Windows.DataFormats.UnicodeText) as string ?? "";
        try
        {
            if (text.StartsWith('{'))
            {
                using var doc = JsonDocument.Parse(text);
                var m = doc.RootElement;
                var unit = m.TryGetProperty("unit", out var u) ? u.GetString() ?? "" : "";
                var group = m.TryGetProperty("group", out var g) ? g.GetString() ?? "" : "";
                if (unit.Length > 0 && group.Length > 0)
                {
                    _vm.ApplyCanvasEdit($"{{\"type\":\"removeUnit\",\"group\":\"{group}\",\"unit\":\"{unit}\"}}");
                    return;
                }
            }
        }
        catch { /* 不是我们的载荷就按普通文本处理 */ }
        _vm.SetStatus("拖进来的不是兵牌（只有从画布拖来的兵牌会被当成删除）");
    }

    /// <summary>把当前包的战帮数据推给画布（没打开包/缺表就静默跳过）。</summary>
    private void PushCanvasData()
    {
        try
        {
            var json = _vm.BuildCanvasJson();
            FileLog.Write($"画布数据已生成（{json.Length} 字符）");
            CanvasHost.Render(json);
        }
        catch (Exception e)
        {
            FileLog.Write("画布数据构建失败（可能没打开包或缺表）", e);
        }
    }

    /// <summary>启动参数（自动化验收用）：--open-table &lt;包内路径&gt; 打开一张表的视图。</summary>
    private async Task RunStartupArgsAsync()
    {
        var args = Environment.GetCommandLineArgs();
        // 注意边界：以前的写法是 i &lt; args.Length - 1，**最后一个参数会被跳过**（无值参数像 --ui-selftest 就永远不触发）。
        for (var i = 0; i < args.Length; i++)
        {
            var next = i + 1 < args.Length ? args[i + 1] : null;
            if (args[i] == "--open-pack" && next is not null)
            {
                FileLog.Write("启动参数：打开 pack " + next);
                await _vm.OpenPackFromArgsAsync(next);
            }
            if (args[i] == "--canvas-edit" && next is not null)
            {
                FileLog.Write("启动参数：画布编辑 " + next);
                _vm.ApplyCanvasEdit(next);             // 自动化验收用：走的就是页面发消息那条路
            }
            if (args[i] == "--library" && next is not null)
            {
                FileLog.Write("启动参数：读兵种库 " + next);
                await _vm.LoadLibraryAsync(next);
            }
            if (args[i] == "--open-table" && next is not null)
            {
                FileLog.Write("启动参数：打开表 " + next);
                await _vm.OpenTableAsync(next);
            }
            if (args[i] == "--export-to" && next is not null)
            {
                FileLog.Write("启动参数：导出到 " + next);
                await _vm.ExportToAsync(next);
            }
            if (args[i] == "--ui-selftest")
            {
                FileLog.Write("启动参数：下拉自检（成本工坊）");
                await RunDropDownSelfTestAsync();
            }
        }
    }

    /// <summary>自检：成本工坊两个下拉的"展开链路 + 选项清单"（--ui-selftest，无头可跑）。
    /// 盯的是"点箭头能看到选项"这条：清单为空（items=0）时弹层是个空条，看着就是"点了没反应"。</summary>
    private async Task RunDropDownSelfTestAsync()
    {
        CostTab.IsSelected = true;                                    // 选中页签 → 内容才会 realize/load
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
        await Task.Delay(400);

        void Dump(string tag, System.Windows.Controls.ComboBox cb)
        {
            var popup = cb.Template.FindName("PART_Popup", cb) as System.Windows.Controls.Primitives.Popup;
            var toggle = cb.Template.FindName("Toggle", cb) as System.Windows.Controls.Primitives.ToggleButton;
            FileLog.Write($"[selftest] {tag}: loaded={cb.IsLoaded} items={cb.Items.Count} open={cb.IsDropDownOpen} " +
                          $"popup={(popup is null ? "无" : popup.IsOpen.ToString())} " +
                          $"popupChildVisible={(popup?.Child?.IsVisible.ToString() ?? "无")} " +
                          $"placementTarget={(popup?.PlacementTarget is null ? "null" : "ok")} " +
                          $"toggleChecked={(toggle is null ? "无" : toggle.IsChecked?.ToString() ?? "null")}");
            for (var i = 0; i < Math.Min(3, cb.Items.Count); i++)
                FileLog.Write($"[selftest]   [{i}] {cb.Items[i]}");
        }

        Dump("初始 CmbCostId", CmbCostId);
        Dump("初始 CmbCostRes", CmbCostRes);
        FileLog.Write("[selftest] 工坊「当前包」提示：" + _vm.CostPackLine);

        CmbCostId.IsDropDownOpen = true;                              // ① 直接设（等同"属性层能开吗"）
        await Task.Delay(400);
        Dump("① CmbCostId 设 open=true", CmbCostId);
        CmbCostId.IsDropDownOpen = false;
        await Task.Delay(250);

        if (CmbCostId.Template.FindName("Toggle", CmbCostId) is System.Windows.Controls.Primitives.ToggleButton tb)
        {
            // ② 拨模板里的 ToggleButton：这就是真实点击箭头的核心路径（OnToggle → IsChecked → IsDropDownOpen）
            var peer = new System.Windows.Automation.Peers.ToggleButtonAutomationPeer(tb);
            ((System.Windows.Automation.Provider.IToggleProvider)peer.GetPattern(
                System.Windows.Automation.Peers.PatternInterface.Toggle)).Toggle();
            await Task.Delay(400);
            Dump("② 拨 Toggle 后 CmbCostId", CmbCostId);
            CmbCostId.IsDropDownOpen = false;
            await Task.Delay(250);
        }
        else FileLog.Write("[selftest] 模板里找不到 Toggle 按钮");

        CmbCostRes.IsDropDownOpen = true;                             // ③ 资源下拉对照
        await Task.Delay(400);
        Dump("③ CmbCostRes 设 open=true", CmbCostRes);
        CmbCostRes.IsDropDownOpen = false;
        await Task.Delay(250);

        // ④/⑤ 点下拉里的某一项（挑**带资源**的那条，顺带验证图标渲染）：
        //    走 Selector 层的选择路径（等同真实点击），看 Text / VM.CostId / 预填有没有跟上。
        CmbCostId.IsDropDownOpen = true;
        await Task.Delay(600);
        var pickIdx = 0;
        for (var i = 0; i < CmbCostId.Items.Count; i++)
            if (CmbCostId.Items[i] is Services.Backend.WarbandCost wc && wc.PoolId.Length > 0) { pickIdx = i; break; }
        if (CmbCostId.ItemContainerGenerator.ContainerFromIndex(pickIdx) is System.Windows.Controls.ComboBoxItem ci)
        {
            var img0 = FindImage(ci);
            FileLog.Write(img0 is null
                ? "[selftest] ⑤ 条目里找不到 Image（模板没渲染图标）"
                : $"[selftest] ⑤ 选中前图标：source={(img0.Source is null ? "null" : "有")} " +
                  $"visible={img0.IsVisible} size={img0.ActualWidth:0}x{img0.ActualHeight:0}");
            ci.IsSelected = true;        // 等同"点了这一项"
            await Task.Delay(500);
            FileLog.Write($"[selftest] ④ 点第 {pickIdx} 项后：text={CmbCostId.Text} " +
                          $"selected={(CmbCostId.SelectedItem as Services.Backend.WarbandCost)?.Id ?? "(空)"} " +
                          $"vmCostId={_vm.CostId} 预填金币={_vm.CostGold} 预填资源={_vm.CostResText} " +
                          $"items={CmbCostId.Items.Count} 按钮={_vm.CostActionText}");
            var img1 = FindImage(ci);
            FileLog.Write(img1 is null
                ? "[selftest] ⑤ 选中后条目里找不到 Image"
                : $"[selftest] ⑤ 选中后图标：source={(img1.Source is null ? "null" : "有")} " +
                  $"visible={img1.IsVisible} size={img1.ActualWidth:0}x{img1.ActualHeight:0}");
        }
        else FileLog.Write($"[selftest] ④ 拿不到第 {pickIdx} 项的容器（下拉没打开？）");
        CmbCostId.IsDropDownOpen = false;
        await Task.Delay(250);

        // ⑪ 打字后展开下拉：输入的文字不能被清掉（清旧选中项时要把文字补回来）
        CmbCostId.Text = "my_new_cost_id";
        await Task.Delay(120);
        CmbCostId.IsDropDownOpen = true;      // 触发 DropDownOpened（清旧选中项那一步）
        await Task.Delay(400);
        FileLog.Write($"[selftest] ⑪ 打字后展开下拉：text={CmbCostId.Text} vmCostId={_vm.CostId}（都应还是 my_new_cost_id）");
        CmbCostId.IsDropDownOpen = false;
        await Task.Delay(200);

        static System.Windows.Controls.Image? FindImage(System.Windows.DependencyObject root)
        {
            for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (child is System.Windows.Controls.Image im) return im;
                if (FindImage(child) is { } hit) return hit;
            }
            return null;
        }

        // ⑥ 创建一条"带资源"的成本（只在内存里，不保存）→ 工坊清单与画布成本数据里要**立刻**有它（含资源与图标）
        _vm.CostId = "studio_selftest_cost";
        _vm.CostGold = "-77";
        _vm.CostResText = "skaven_food";
        _vm.CostResAmount = "-2";
        _vm.CreateWarbandCost();
        await Task.Delay(400);
        var mine = _vm.CostListShown.Where(c => c.Id.StartsWith("studio_selftest", StringComparison.Ordinal)).ToList();
        FileLog.Write($"[selftest] ⑥ 创建后工坊清单 {_vm.CostListShown.Count} 条；自建的那条：" +
                      (mine.Count == 0 ? "(清单里没有 ✗)" : string.Join(" | ", mine.Select(c =>
                          $"{c.Id} 金={c.Gold} 资源={c.Resource} 数量={c.ResourceAmount} 图标={(c.ResourceIcon.Length > 0 ? "有" : "无")}"))));
        var cj = _vm.BuildCanvasJson();
        var at = cj.IndexOf("studio_selftest_cost", StringComparison.Ordinal);
        FileLog.Write("[selftest] ⑥ 画布成本数据：" +
                      (at < 0 ? "(没找到 ✗)" : cj.Substring(at, Math.Min(220, cj.Length - at))));

        // ⑦ 只改数量（资源不动）→ 必须能写回去（用户实测"保存后再次加载修改，数量改了没生效"）
        _vm.CostId = "studio_selftest_cost";        // 选中刚建的那条（带出它现在的资源/数量/池）
        await Task.Delay(200);
        _vm.CostResAmount = "-9";
        _vm.CreateWarbandCost();
        await Task.Delay(250);
        var q = _vm.CostListAll.FirstOrDefault(c => c.Id.Equals("studio_selftest_cost", StringComparison.OrdinalIgnoreCase));
        FileLog.Write($"[selftest] ⑦ 只改数量后：{(q is null ? "(清单里没有 ✗)" : $"数量={q.ResourceAmount} 资源={q.Resource} 池={q.PoolId}")}");

        // ⑧ 重命名 → 旧 id 消失、新 id 顶上（保存后包里也一样：旧行连同资源关联一起删）
        _vm.RenameCurrentCost("studio_selftest_renamed");
        await Task.Delay(250);
        var rn = _vm.CostListAll.FirstOrDefault(c => c.Id.Equals("studio_selftest_renamed", StringComparison.OrdinalIgnoreCase));
        var oldLeft = _vm.CostListAll.Any(c => c.Id.Equals("studio_selftest_cost", StringComparison.OrdinalIgnoreCase));
        FileLog.Write($"[selftest] ⑧ 重命名后：新 id {(rn is null ? "没有 ✗" : $"在（金={rn.Gold} 资源={rn.Resource} 数量={rn.ResourceAmount}）")}；" +
                      $"旧 id {(oldLeft ? "还在 ✗" : "已消失")}");

        // ⑧b 只改**大小写**的改名（用户实测：小写改大写后又被变回小写）
        _vm.RenameCurrentCost("Studio_SelfTest_RENAMED");
        await Task.Delay(250);
        var rn2 = _vm.CostListAll.FirstOrDefault(c => c.Id.Equals("Studio_SelfTest_RENAMED", StringComparison.OrdinalIgnoreCase));
        FileLog.Write($"[selftest] ⑧b 只改大小写后：清单里的 id = {(rn2 is null ? "(没有 ✗)" : rn2.Id)}（应为 Studio_SelfTest_RENAMED）");

        // ⑨ 重命名**包里已有的**成本（用户的真实场景）：旧行连同它的资源关联一起删、资源关联搬到新 id
        var packCost = _vm.CostListAll.FirstOrDefault(c => c.Id.Equals("beastmen_dummy", StringComparison.OrdinalIgnoreCase));
        if (packCost is not null)
        {
            _vm.CostId = packCost.Id;
            await Task.Delay(150);
            _vm.RenameCurrentCost("studio_selftest_packren");
            await Task.Delay(250);
            var pr = _vm.CostListAll.FirstOrDefault(c => c.Id.Equals("studio_selftest_packren", StringComparison.OrdinalIgnoreCase));
            var oldStill = _vm.CostListAll.Any(c => c.Id.Equals("beastmen_dummy", StringComparison.OrdinalIgnoreCase));
            FileLog.Write($"[selftest] ⑨ 包里成本改名后：新 id {(pr is null ? "没有 ✗" : $"在（金={pr.Gold} 资源={pr.Resource} 数量={pr.ResourceAmount} 池={pr.PoolId}）")}；" +
                          $"旧 id {(oldStill ? "还在 ✗" : "已消失")}");
        }

        // ⑩ 画布成本数据：改名删掉的旧 id 不能还挂在"选择升级的成本"下拉里（用户实测）
        {
            var cj2 = _vm.BuildCanvasJson();
            FileLog.Write($"[selftest] ⑩ 画布成本数据里：旧 id beastmen_dummy {(cj2.Contains("\"k\":\"beastmen_dummy\"") ? "还在 ✗" : "已去掉 ✓")}；" +
                          $"新 id studio_selftest_packren {(cj2.Contains("studio_selftest_packren") ? "在 ✓" : "没有 ✗")}");
        }


        FileLog.Write($"[selftest] 结论：CmbCostId 清单 {CmbCostId.Items.Count} 条 / CmbCostRes 清单 {CmbCostRes.Items.Count} 条" +
                      "（都 > 0 = 点箭头能看到选项；= 0 就是弹了个空条）");
    }

    /// <summary>双击文件树里的 DB 表 → 中间栏开表视图（原生解码）。</summary>
    private async void OnTreeDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (TreeFiles.SelectedItem is TreeItem item && !item.IsFolder)
            await _vm.OpenTableAsync(item.Path);
    }
}
