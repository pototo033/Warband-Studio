using System.Collections.ObjectModel;
using System.Data;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using WarbandStudio.Pack;
using WarbandStudio.Ui.Services;

namespace WarbandStudio.Ui.ViewModels;

/// <summary>历史版本对话框的一行（WPF 绑定要**属性**，不能用 ValueTuple——它的具名元素是字段）。</summary>
public sealed record HistoryRow(string File, string When, string Size, string Full);

/// <summary>
/// 主视图模型：后端生命周期、首跑向导、打开/保存 pack、文件树、诊断、日志。
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly Backend _backend;

    private string _gameDir = "";
    private string _backupDir = "";
    private string _serverText = "引擎：未连接（本工具依赖已安装的 RPFM）";
    private string _schemaText = "schema：未知";
    private string _packText = "Pack：未打开";
    private string _status = "就绪";
    private bool _isBusy;

    public MainViewModel()
    {
        _backend = new Backend(_settings);
        _gameDir = _settings.GameDir;
        _backupDir = string.IsNullOrWhiteSpace(_settings.BackupDir)
            ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WarbandStudio", "backups")
            : _settings.BackupDir;
        _autoSave = _settings.AutoSave;
        // 这里不建画布页签：等第一个包打开时按包名建（避免出现一个空的"战帮画布"页签）
        // **日志回调不阻塞后台线程**：以前用同步 Dispatcher.Invoke，而 UI 线程可能正等着后台线程的图标解析锁
        // （兵种库在后台抽图、画布在 UI 线程抽图，抢同一把锁）→ 死锁。现在文件日志直接写（自带锁），
        // 界面那行用 BeginInvoke 排队，顺序按入队先后。
        _backend.Log += line =>
        {
            FileLog.Write("[rpfm] " + line);
            try
            {
                App.Current.Dispatcher.BeginInvoke(() =>
                {
                    LogLines.Add(line);
                    while (LogLines.Count > 400) LogLines.RemoveAt(0);
                });
            }
            catch { /* 关窗口时 Dispatcher 可能已停 */ }
        };

        OpenPackCommand = new RelayCommand(() => OpenPackAsync(false), () => !IsBusy);
        // 打开处就分两种：加载为**战帮**（当前这种）/ 加载为**兵种**（参考包：图标/中文名/兵种库用，不占画布）
        OpenUnitPackCommand = new RelayCommand(() => OpenPackAsync(true), () => !IsBusy);
        SavePackCommand = new RelayCommand(() => SaveAsync(null), () => PackOpen && !IsBusy);
        SavePackAsCommand = new RelayCommand(SaveAsAsync, () => PackOpen && !IsBusy);
        ClosePackCommand = new RelayCommand(ClosePackAsync, () => PackOpen && !IsBusy);
        CloseProjectCommand = new RelayCommand(CloseProjectAsync, () => HasProject && !IsBusy);
        ChooseGameDirCommand = new RelayCommand(ChooseGameDirAsync, () => !IsBusy);
        ApplyGameCommand = new RelayCommand(ApplyGameDirAsync, () => !IsBusy);
        DiagnosticsCommand = new RelayCommand(RunDiagnosticsAsync, () => !IsBusy);
        LoadUiAssetsCommand = new RelayCommand(() => { LoadUiAssets(); return Task.CompletedTask; }, () => PackOpen);
    }

    // ── 界面状态 ────────────────────────────────────────────────

    public ObservableCollection<TreeItem> TreeRoots { get; } = [];
    public ObservableCollection<string> DiagnosticsLines { get; } = [];
    public ObservableCollection<string> LogLines { get; } = [];

    /// <summary>打开/切换了一个 pack（窗口据此刷新画布数据）。</summary>
    public event Action? PackOpened;

    /// <summary>要选游戏目录时触发（窗口弹选择框，列出扫描到的候选）。参数是当前值（可空）。</summary>
    public event Func<string?, Task<string?>>? GameDirRequested;

    public string GameDir { get => _gameDir; set => Set(ref _gameDir, value); }

    /// <summary>备份文件夹（保存写回原包时原文件备份到这里）；改了立刻存设置。</summary>
    public string BackupDir
    {
        get => _backupDir;
        set { _backupDir = value; _settings.BackupDir = value; _settings.Save(); Raise(nameof(BackupDir)); }
    }

    // ── 工程（v1.5.0：用户选一个文件夹 = 一个工程；项目 key / 历史版本都在工程里）────────────

    private ProjectInfo? _project;
    private string _projectDir = "";

    /// <summary>当前工程（null = 没打开；启动就是空状态）。</summary>
    public ProjectInfo? Project
    {
        get => _project;
        private set
        {
            _project = value;
            Raise(nameof(Project));
            Raise(nameof(HasProject));
            Raise(nameof(ProjectName));
            Raise(nameof(ProjectDir));
            Raise(nameof(ProjectText));
            Raise(nameof(ProjectKey));
            Raise(nameof(IsWelcomeVisible));
        }
    }

    public bool HasProject => _project is not null;
    public string ProjectName => _project?.Name ?? "(未打开)";
    public string ProjectDir => _projectDir;
    public string ProjectText => _project is null ? "工程：未打开" : $"工程：{_project.Name}";

    /// <summary>**项目 key**（新单位组的命名前缀，存工程 project.json）：`<key>_<页签>_<兵种词…>`；
    /// 没工程/留空 = 新组命名退回旧时间戳规则。改了立刻落盘（下一个新建的组就用新 key）。</summary>
    public string ProjectKey
    {
        get => _project?.ProjectKey ?? "";
        set
        {
            if (_project is null || _projectDir.Length == 0) return;
            _project.ProjectKey = value ?? "";
            ProjectStore.Save(_projectDir, _project);
            _backend.ProjectKey = _project.ProjectKey;
            _backend.InvalidateCanvas();                 // 画布「新建分组」的默认名跟着变
            Raise(nameof(ProjectKey));
        }
    }

    /// <summary>欢迎面板（空状态）是否显示：没有画布页签 = 还没开工程/包。</summary>
    public bool IsWelcomeVisible => !IsCanvasActive;

    /// <summary>最近工程（欢迎面板/菜单用；打开时会把"目录已不存在"的剔掉）。</summary>
    public ObservableCollection<string> RecentProjects { get; } = [];

    /// <summary>刷新最近工程列表（剔除失效目录）。</summary>
    private void PruneRecentProjects()
    {
        var list = _settings.RecentProjects.Where(d => Directory.Exists(d) && ProjectStore.IsProject(d)).ToList();
        if (list.Count != _settings.RecentProjects.Count) { _settings.RecentProjects = list; _settings.Save(); }
        RecentProjects.Clear();
        foreach (var d in list) RecentProjects.Add(d);
    }

    /// <summary>教程是否放过（第一次打开自动放一遍；看完/跳过都记上）。</summary>
    public bool TourDone
    {
        get => _settings.TourDone;
        set { _settings.TourDone = value; _settings.Save(); }
    }

    /// <summary>本程序版本（build.ps1 编进来的）+ 检查更新入口（启动时后台查一次；菜单/全局选项里可手动查）。</summary>
    public string AppVersion => "v" + Services.Updater.CurrentVersion;

    public async Task CheckUpdatesAsync() => await Services.Updater.CheckAsync(_settings, s => Status = s);

    /// <summary>当前打开包的路径（文件树右键"打开所在文件夹"用；没打开就是空）。</summary>
    public string CurrentPackPath => _backend.Pack?.PackPath ?? "";
    public string ServerText { get => _serverText; private set => Set(ref _serverText, value); }
    public string SchemaText { get => _schemaText; private set => Set(ref _schemaText, value); }
    public string PackText { get => _packText; private set => Set(ref _packText, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }

    /// <summary>
    /// 成本工坊的"当前包"提示：成本写进**当前包**，切了画布页签就会换包 ——
    /// 用户实测踩过：切到工具自带的参考包后点保存，成本写进了参考包、自己的包没动，看着像"保存后成本消失了"。
    /// </summary>
    public string CostPackLine
    {
        get
        {
            var pack = _backend.Pack;
            if (pack is not { IsOpen: true }) return "当前没有打开包 —— 先在左边打开/切到要改的那个包";
            var name = pack.Info?.FileName ?? (pack.PackPath is { } p ? Path.GetFileName(p) : "(未知包)");
            var warn = name.Contains("WUU战帮升级", StringComparison.OrdinalIgnoreCase)
                ? "⚠ 这是工具自带的参考包；" : "";
            return $"{warn}成本写进当前包：{name}（保存 = 写回它）";
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value)) return;
            foreach (var c in new[] { OpenPackCommand, OpenUnitPackCommand, SavePackCommand, SavePackAsCommand, ClosePackCommand,
                                      ChooseGameDirCommand, ApplyGameCommand, DiagnosticsCommand })
                c.RaiseCanExecute();
        }
    }

    public bool PackOpen => _backend.Pack?.IsOpen == true;

    public RelayCommand OpenPackCommand { get; }
    public RelayCommand OpenUnitPackCommand { get; }
    public RelayCommand SavePackCommand { get; }
    public RelayCommand SavePackAsCommand { get; }
    public RelayCommand ClosePackCommand { get; }
    public RelayCommand CloseProjectCommand { get; }
    public RelayCommand ChooseGameDirCommand { get; }
    public RelayCommand ApplyGameCommand { get; }
    public RelayCommand DiagnosticsCommand { get; }
    public RelayCommand LoadUiAssetsCommand { get; }

    /// <summary>界面上的引擎状态（来自 Backend）。</summary>
    public void RefreshEngineText()
    {
        ServerText = _backend.EngineText;
        SchemaText = _backend.SchemaText;
    }

    // ── 中间栏页签（画布 + 表视图）────────────────────────────

    /// <summary>中间栏页签：第一个永远是战帮画布，打开的表往后排。</summary>
    public ObservableCollection<CenterTab> CenterTabs { get; } = [];

    /// <summary>
    /// 画布页签：**一个包一个**（名字 = 包名 + 战帮；原版那个叫「原版战帮」）。
    /// 打开新包会新增一个画布（不动原来的），切页签 = 切到那个包的画布。
    /// </summary>
    public CanvasTab EnsureCanvasTab(PackSession session)
    {
        var key = session.PackPath;
        var existing = CenterTabs.OfType<CanvasTab>()
            .FirstOrDefault(t => string.Equals(t.PackPath, key, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing;
        var name = session.DisplayName.Contains("原版", StringComparison.Ordinal) || session.DisplayName.EndsWith("db.pack", StringComparison.OrdinalIgnoreCase)
            ? "原版战帮"
            : session.DisplayName + "战帮";
        var tab = new CanvasTab { Header = name, PackPath = key };
        CenterTabs.Add(tab);
        return tab;
    }

    /// <summary>切到某个包的画布（没有就建一个）。左侧文件树右键「在新画布中打开」也走这里。</summary>
    public async Task OpenCanvasForPackAsync(string packPath)
    {
        var session = _backend.Packs.FirstOrDefault(x =>
            string.Equals(x.PackPath, Path.GetFullPath(packPath), StringComparison.OrdinalIgnoreCase));
        if (session is null)
        {
            Status = "这个包还没打开过：先双击打开它，再右键开画布";
            return;
        }
        var tab = EnsureCanvasTab(session);
        _backend.SwitchTo(session);
        ActiveCenterTab = tab;
        PackOpened?.Invoke();
        RefreshPooledResources();      // 当前包变了 → 成本工坊清单跟着换（派生状态随源更新）
        await Task.CompletedTask;
    }

    private CenterTab _activeCenterTab = null!;
    public CenterTab ActiveCenterTab
    {
        get => _activeCenterTab;
        // TabControl.SelectedItem 是双向绑定：ItemsSource 填充时 WPF 会先写回一个 null，
        // 那样画布就被"取消选中"了（表现：点「战帮画布」没反应，得先开一张表才活）。
        // 这里把 null 顶回第一个页签（战帮画布），并强制再通知一次让控件同步回来。
        set
        {
            var v = value ?? (CenterTabs.Count > 0 ? CenterTabs[0] : null!);
            if (Set(ref _activeCenterTab, v)) { Raise(nameof(IsCanvasActive)); Raise(nameof(IsWelcomeVisible)); }
            else if (v is not null) Raise(nameof(ActiveCenterTab));
        }
    }

    /// <summary>当前选中的是不是画布页签（画布 WebView2 靠它决定显示/隐藏）。</summary>
    public bool IsCanvasActive => _activeCenterTab is CanvasTab;

    /// <summary>当前画布对应的包（切页签后重推数据用）。</summary>
    public void OnCenterTabChanged()
    {
        if (_activeCenterTab is TableTab tt) { _ = ReloadTableTabAsync(tt); return; }   // 表视图不再是快照：切过去就重读
        if (_activeCenterTab is not CanvasTab ct) return;
        var session = _backend.Packs.FirstOrDefault(x =>
            string.Equals(x.PackPath, ct.PackPath, StringComparison.OrdinalIgnoreCase));
        if (session is null) return;
        _backend.SwitchTo(session);
        PackOpened?.Invoke();
        LoadUiAssets();          // 左下「UI 素材库」跟着换成这个包的图
        RefreshPooledResources(); // 成本工坊清单也跟着换（切画布 = 换当前包）
    }

    /// <summary>画布数据（JSON）；窗口在打开包之后取一次。</summary>
    public string BuildCanvasJson() => _backend.BuildWarbandJson();      // 看的是"当前包"（切页签时切过来）

    /// <summary>画布上拖动了一张牌：记下所属组的新坐标（导出时生效）。</summary>
    public void ApplyCanvasMove(string group, int x, int y)
    {
        _backend.SetGroupPos(group, x, y);
        Status = $"已移动「{group}」到 ({x}, {y})　待导出；（点「另存为…」写出到 pack）";
    }

    // ————— 右栏：种族 / 派系（上）+ 兵种库（下）——都实时读 DB —————

    public ObservableCollection<FactionNode> Factions { get; } = [];
    public ObservableCollection<UnitCard> Library { get; } = [];

    private FactionNode? _selectedFaction;
    public FactionNode? SelectedFaction
    {
        get => _selectedFaction;
        set
        {
            if (!Set(ref _selectedFaction, value)) return;
            _ = LoadLibraryAsync(value?.Key);
            // 选种族 → 画布切到该种族的页签并显示全部（含专属兵，专属打角标）；选派系 → 只显示该派系的兵
            var race = value?.Race;
            var fac = value is { IsFaction: true } ? value.Key : null;
            if (string.IsNullOrWhiteSpace(race) && value is { IsAll: true }) { FilterBy(null, null); return; }
            FilterBy(race, fac);
        }
    }

    /// <summary>兵种库搜索（key 和中文名都能搜；空 = 全部）。</summary>
    private string _libSearch = "";
    public string LibrarySearch
    {
        get => _libSearch;
        set { _libSearch = value; Raise(nameof(LibrarySearch)); ApplyLibFilter(); }
    }
    /// <summary>种族页兵种库的全量（搜索只是过滤显示，不重新读库）。</summary>
    private List<UnitCard> _libAll = [];
    /// <summary>mod 兵种库的全量。</summary>
    private List<UnitCard> _modLibAll = [];

    /// <summary>
    /// 「mod 兵种」页的节点：根 = 兵种包，子节点 = 该包的 `main_units_tables` **文件**
    /// （选中哪个文件就显示哪个文件的兵 —— 比如 `za_...` 与 `!!!...` 两张适配表各自成表）。
    /// </summary>
    public sealed class ModItem(string key, string name, string? tablePath = null)
    {
        public string Key { get; } = key;              // 包内路径（packPath）
        public string Name { get; } = name;            // 包名 / 文件名
        public string? TablePath { get; } = tablePath; // 非空 = 这是一个表文件节点
        public List<ModItem> Children { get; } = [];
        public bool IsFile => TablePath is { Length: > 0 };
        /// <summary>树里默认展开（这样"包 → 表文件"一眼能看见）。</summary>
        public bool IsExpanded { get; set; } = true;
    }
    public System.Collections.ObjectModel.ObservableCollection<ModItem> Mods { get; } = [];
    public System.Collections.ObjectModel.ObservableCollection<UnitCard> ModLibrary { get; } = [];
    private ModItem? _selectedMod;
    private string _modSearch = "";
    private string _modTitle = "mod 兵种（选一个包）";

    public ModItem? SelectedMod
    {
        get => _selectedMod;
        set { _selectedMod = value; Raise(nameof(SelectedMod)); _ = LoadModLibraryAsync(value); }
    }
    /// <summary>树里选中的（可能是包根，也可能是某张表文件）—— 由 UI 的选中事件调用。</summary>
    public void PickModNode(ModItem? node) => SelectedMod = node;
    /// <summary>mod 兵种库搜索（key 和名字都能搜）。</summary>
    public string ModLibrarySearch
    {
        get => _modSearch;
        set { _modSearch = value; Raise(nameof(ModLibrarySearch)); ApplyModLibFilter(); }
    }
    public string ModLibraryTitle { get => _modTitle; private set { _modTitle = value; Raise(nameof(ModLibraryTitle)); } }

    /// <summary>搜索命中：key 或中文名（大小写不敏感）。</summary>
    private static bool LibMatch(UnitCard c, string q) =>
        q.Length == 0
        || (c.Key ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
        || (c.Name ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;

    private void ApplyLibFilter()
    {
        var q = (_libSearch ?? "").Trim();
        Library.Clear();
        foreach (var c in _libAll) if (LibMatch(c, q)) Library.Add(c);
        Raise(nameof(LibraryTitle));
    }

    private void ApplyModLibFilter()
    {
        var q = (_modSearch ?? "").Trim();
        ModLibrary.Clear();
        foreach (var c in _modLibAll) if (LibMatch(c, q)) ModLibrary.Add(c);
        ModLibraryTitle = (_selectedMod?.Name ?? "mod 兵种（选一个包）") +
                          $"：{(q.Length > 0 ? "搜到 " : "")}{ModLibrary.Count} 个兵";
    }

    /// <summary>包打开/关闭后刷新 mod 清单（右上「mod 兵种」页）。</summary>
    public void RefreshMods(string? selectKey = null)
    {
        try
        {
            Mods.Clear();
            foreach (var (key, name) in _backend.OpenMods())
            {
                var packNode = new ModItem(key, name);
                foreach (var (tPath, tName) in _backend.ModUnitTables(key))
                    packNode.Children.Add(new ModItem(key, tName, tPath));   // 子节点 = 每个 main_units 表文件
                Mods.Add(packNode);
            }
            if (Mods.Count == 0) { _modLibAll = []; ApplyModLibFilter(); return; }
            // 刚加载的那个优先选中；否则保持当前选中（还在列表里就不跳）
            var pack = (selectKey is { Length: > 0 }
                        ? Mods.FirstOrDefault(m => m.Key.Equals(selectKey, StringComparison.OrdinalIgnoreCase))
                        : null)
                       ?? (_selectedMod is not null ? Mods.FirstOrDefault(m => m.Key.Equals(_selectedMod.Key, StringComparison.OrdinalIgnoreCase)) : null)
                       ?? Mods[0];
            // 默认落到**第一张 main_units 表文件**上（而不是包根 = "这个包的全部兵"）——
            // 用户口径：不要一进来就停在"全部"，自动切到具体的表里
            SelectedMod = pack.Children.Count > 0 ? pack.Children[0] : pack;
        }
        catch (Exception ex) { Status = "mod 清单读取失败：" + ex.Message; }
    }

    private int _modGen;
    private async Task LoadModLibraryAsync(ModItem? mod)
    {
        if (mod is null || _backend.Pack?.IsOpen != true) { _modLibAll = []; ApplyModLibFilter(); return; }
        var gen = ++_modGen;
        try
        {
            var json = await Task.Run(() => _backend.BuildModUnitLibraryJson(mod.Key, mod.TablePath));
            if (gen != _modGen) return;
            _modLibAll = [];
            using var doc = JsonDocument.Parse(json);
            var icons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (doc.RootElement.TryGetProperty("icons", out var ic))
                foreach (var p in ic.EnumerateObject()) icons[p.Name] = p.Value.GetString() ?? "";
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (doc.RootElement.TryGetProperty("names", out var nc))
                foreach (var p in nc.EnumerateObject()) names[p.Name] = p.Value.GetString() ?? "";
            var inTree = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (doc.RootElement.TryGetProperty("inTree", out var it) && it.ValueKind == JsonValueKind.Array)
                foreach (var x in it.EnumerateArray()) { var v = x.GetString() ?? ""; if (v.Length > 0) inTree.Add(v); }
            foreach (var u in doc.RootElement.GetProperty("units").EnumerateArray())
            {
                var k = u.GetString() ?? "";
                if (k.Length == 0) continue;
                icons.TryGetValue(k, out var url);
                names.TryGetValue(k, out var nm);
                _modLibAll.Add(new UnitCard(k, url, _backend.LocalIconPath(url), nm, inTree.Contains(k)));
            }
            ApplyModLibFilter();
        }
        catch (Exception ex) { Status = "mod 兵种读不出来：" + ex.Message; _modLibAll = []; ApplyModLibFilter(); }
    }

    public string LibraryTitle => _selectedFaction is null or { IsAll: true }
        ? "兵种库（全部）"
        : $"兵种库：{_selectedFaction.Key}";

    /// <summary>兵种库里双击一个兵 → 交给画布新开一组（参数：兵种 key, 卡图 url）。</summary>
    public event Action<string, string?>? UnitPicked;

    /// <summary>要用户确认时问一句（窗口挂 MessageBox；没人挂就默认"确定"）。</summary>
    public Func<string, bool>? Confirm { get; set; }

    /// <summary>关画布时问"未保存怎么办"：返回 0=现在导出 1=放弃编辑 2=取消。</summary>
    public Func<string, string, int>? AskSave { get; set; }

    /// <summary>
    /// 关闭一个页签：画布页签 = 关掉那个包（**有未导出的编辑会先问**：确定=现在导出，取消=放弃编辑）；表页签直接关。
    /// </summary>
    public async Task CloseTabAsync(CenterTab tab)
    {
        if (tab is CanvasTab ct)
        {
            // 未导出编辑按**这个页签那个包**算（不是"当前包"）：右键关别的包时，当前包有编辑不该误弹
            var pending = _backend.EditCountOf(ct.PackPath) > 0;
            if (pending)
            {
                // 三选一：0=保存（写回原包；工程里会留一份历史版本） 1=放弃编辑并关闭 2=取消（不关）
                var choice = AskSave?.Invoke(ct.Header, _backend.EditSummaryOf(ct.PackPath)) ?? 1;
                if (choice == 2) { Status = "已取消关闭"; return; }
                if (choice == 0)
                {
                    Status = "关闭前先保存…";
                    if (!await SavePackByPathAsync(ct.PackPath)) { Status = "保存失败，画布保持打开（见日志）"; return; }
                }
                else _backend.DiscardEditsOf(ct.PackPath);   // 清**这个页签那个包**的编辑（当前包可能已经被挪走了）
            }
            var session = _backend.Packs.FirstOrDefault(x => string.Equals(x.PackPath, ct.PackPath, StringComparison.OrdinalIgnoreCase));
            if (session is not null) await _backend.CloseSessionAsync(session);
            RebuildTree();
        }
        // 关页签的顺序很重要：**先把选中项挪走，再删**。
        // WPF 的 TabControl 在"删掉当前选中项"时会立刻去 ContainerFromIndex 取新选中项的容器，
        // 而生成器还没同步 → IndexOutOfRangeException（app.log 里那条 CloseTabAsync 崩溃就是这个）。
        // 最后一个页签没法"先选别的"，就把选中清空、把删除推到下一帧。
        var wasActive = ReferenceEquals(_activeCenterTab, tab);
        if (wasActive)
        {
            var next = CenterTabs.FirstOrDefault(t => !ReferenceEquals(t, tab));
            if (next is not null) ActiveCenterTab = next;
            else
            {
                _activeCenterTab = null!;
                Raise(nameof(ActiveCenterTab));
                Raise(nameof(IsCanvasActive));
                Raise(nameof(IsWelcomeVisible));
            }
        }
        try { CenterTabs.Remove(tab); }
        catch (IndexOutOfRangeException)
        {
            // 万一 TabControl 还在用旧容器：下一帧再删，别让异常冒到全局弹框
            App.Current.Dispatcher.InvokeAsync(
                () => { if (CenterTabs.Contains(tab)) CenterTabs.Remove(tab); },
                System.Windows.Threading.DispatcherPriority.Background);
        }
        if (_activeCenterTab is null && CenterTabs.Count > 0 && !CenterTabs.Contains(tab))
            ActiveCenterTab = CenterTabs[0];
        PackOpened?.Invoke();
        LoadUiAssets();            // 包可能被关掉了 → 素材库跟着清/换
        Status = $"已关闭页签：{tab.Header}";
    }

    /// <summary>右上选了种族/派系 → 让画布按它过滤（race 空 = 不过滤）。</summary>
    public event Action<string?, string?>? FilterRequested;
    public void FilterBy(string? race, string? faction)
    {
        FilterRequested?.Invoke(race, faction);
        Status = faction is { Length: > 0 } ? $"画布只显示派系：{faction}"
               : race is { Length: > 0 } ? $"画布只显示种族：{race}"
               : "画布显示全部";
    }

    // ── 左下「UI 素材库」：**本地素材**（内置 WUU+雪乃 + 导入的），右键"添加到当前包"（不动画布）──

    public ObservableCollection<UiAssetItem> UiAssets { get; } = [];
    private List<UiAssetItem> _uiAssetsAll = [];
    private string _uiAssetFilter = "";
    public string UiAssetFilter
    {
        get => _uiAssetFilter;
        set { if (Set(ref _uiAssetFilter, value)) ApplyUiAssetFilter(); }
    }

    /// <summary>刷新素材库（打开/切换包、导入、移除之后都调它）。</summary>
    public void LoadUiAssets()
    {
        try
        {
            _uiAssetsAll = _backend.UiLibrary()
                .Select(a => new UiAssetItem(a.Kind, a.Name, a.File, a.Url, a.PackPath, a.Size, a.PreviewFile)).ToList();
            // 面板里按"背景图 / 按钮图 / 其他 UI"分组显示（背景和按钮分开看）
            var view = System.Windows.Data.CollectionViewSource.GetDefaultView(UiAssets);
            if (view.GroupDescriptions.Count == 0)
                view.GroupDescriptions.Add(
                    new System.Windows.Data.PropertyGroupDescription(nameof(UiAssetItem.GroupLabel)));
            ApplyUiAssetFilter();
            Status = $"UI 素材库：{_uiAssetsAll.Count} 张（背景 {_uiAssetsAll.Count(x => x.Kind == "bg")} / " +
                     $"按钮 {_uiAssetsAll.Count(x => x.Kind == "btn")} / 其他 {_uiAssetsAll.Count(x => x.Kind == "other")}）";
        }
        catch (Exception ex)
        {
            FileLog.Write("UI 素材库失败", ex);
            Status = "UI 素材库读不出来：" + ex.Message;
        }
    }

    private void ApplyUiAssetFilter()
    {
        UiAssets.Clear();
        foreach (var a in _uiAssetsAll)
            if (_uiAssetFilter.Length == 0
                || a.Name.Contains(_uiAssetFilter, StringComparison.OrdinalIgnoreCase))
                UiAssets.Add(a);
    }

    /// <summary>导入素材：复制进本地素材库，并按类型改成中性名字（第一次重命名；用到页签上时再改成 <前缀><页签>.png）。</summary>
    public void ImportUiAssets(IEnumerable<string> files, string kind)
    {
        var n = _backend.ImportUiAssets(files, kind);
        // **清掉过滤框**：过滤词（比如上次搜的 "skv"）会把刚导入的新名字挡在外面 —— 看着就像"导入没生效"
        if (UiAssetFilter.Length > 0) UiAssetFilter = "";
        LoadUiAssets();
        if (n > 0) FileLog.Write($"素材库导入：{n} 张（{kind}）；列表现在 {UiAssets.Count} 张（过滤框已清空）");
        Status = n > 0
            ? $"已导入 {n} 张为{(kind == "btn" ? "按钮图" : "背景图")}（本地素材库；用到页签上会自动改成 <前缀><页签key>.png）"
            : "没有导入（只能导入 .png）";
    }

    /// <summary>重命名素材库里的一条（前缀按类型保留；同名会拒绝）。</summary>
    public void RenameUiAsset(UiAssetItem? item, string suffix)
    {
        if (item is null) return;
        var msg = _backend.RenameUiAsset(new WarbandStudio.Ui.Services.Backend.UiAsset(
            item.Kind, item.Name, item.File, item.Url, item.PackPath, item.Size, item.PreviewFile), suffix);
        LoadUiAssets();
        RebuildTree();
        PackOpened?.Invoke();
        Status = msg;
    }

    /// <summary>从素材库移除一条（只删本地那份，不动任何包）。</summary>
    public bool RemoveUiAsset(UiAssetItem? item)
    {
        if (item is null) return false;
        if (!_backend.RemoveUiAsset(new WarbandStudio.Ui.Services.Backend.UiAsset(
                item.Kind, item.Name, item.File, item.Url, item.PackPath, item.Size, item.PreviewFile))) return false;
        LoadUiAssets();
        RebuildTree();
        PackOpened?.Invoke();
        Status = $"已从素材库移除：{item.Name}";
        return true;
    }

    /// <summary>把素材库里的一张图加进**当前编辑的包**（按建议路径；导出时落盘）。</summary>
    // ───────────────────────── 成本工坊（右上角第二页）─────────────────────────

    /// <summary>已有成本（「成本 id」下拉）：原版打底 + 本包覆盖，带金币和额外资源。</summary>
    public System.Collections.ObjectModel.ObservableCollection<Services.Backend.WarbandCost> CostListAll { get; } = [];
    /// <summary>成本下拉里"当前显示"的（打字就按 id 过滤；不打字显示全部）。</summary>
    public System.Collections.ObjectModel.ObservableCollection<Services.Backend.WarbandCost> CostListShown { get; } = [];
    /// <summary>资源清单（全量）。</summary>
    public System.Collections.ObjectModel.ObservableCollection<Services.Backend.PooledResource> PooledResAll { get; } = [];
    /// <summary>资源下拉里"当前显示"的（打字按名称/ key 过滤；不打字显示全部）。</summary>
    public System.Collections.ObjectModel.ObservableCollection<Services.Backend.PooledResource> PooledResShown { get; } = [];

    private string _costId = "";
    private string _costGold = "";
    private string _costResAmount = "";
    private string _costResText = "";
    private string _pickedCostId = "";        // 下拉里选中的成本 id（过滤时保留它，别被 ComboBox 清掉）
    private string _pickedResKey = "";        // 资源下拉里选中的资源 key（同上）
    private string _costStatus = "";
    private string _loadedResKey = "";        // 选中已有成本时，它现在挂的资源（用来判断"资源改没改"）
    private long _loadedResAmount;            // 选中已有成本时，它现在扣的数量（用来判断"数量改没改"）
    private string _loadedPoolId = "";        // 它现在用的池 id（只改数量时复用它，别把作者起的池名换掉）
    private bool _loadedExisting;

    /// <summary>成本 id（可在下拉里选已有的，也可以直接打新的）。</summary>
    public string CostId
    {
        get => _costId;
        set
        {
            _costId = value;
            Raise(nameof(CostId));
            FilterCostList();
            SyncCostFromId();
            UpdateCostPreview();
        }
    }
    /// <summary>金币消耗：**带符号**（负 = 消耗、正 = 获得）。</summary>
    public string CostGold { get => _costGold; set { _costGold = value; Raise(nameof(CostGold)); UpdateCostPreview(); } }
    /// <summary>其他资源的量：同样带符号（负 = 消耗）。</summary>
    public string CostResAmount { get => _costResAmount; set { _costResAmount = value; Raise(nameof(CostResAmount)); UpdateCostPreview(); } }
    /// <summary>资源输入框的文本：**默认空 = 不用其他资源**；打字按名称/key 过滤，选中就用它。</summary>
    public string CostResText
    {
        get => _costResText;
        set { _costResText = value; Raise(nameof(CostResText)); FilterResList(); UpdateCostPreview(); }
    }
    /// <summary>创建工作坊里的一行提示（结果/错误）。</summary>
    public string CostStatus { get => _costStatus; set { _costStatus = value; Raise(nameof(CostStatus)); } }
    /// <summary>主按钮文字：这个 id 已经在包里 → 保存修改；否则 → 创建成本。</summary>
    public string CostActionText => _loadedExisting ? "保存修改" : "创建成本";
    /// <summary>这个 id 在包里存在（决定"保存修改/删除成本"可不可用）。</summary>
    public bool CostIsExisting => _loadedExisting;

    /// <summary>资源文本解析出来的资源（空 / 打了一半没选中 → null = 不建资源池、不写 junction 表）。</summary>
    private Services.Backend.PooledResource? ResolvedResource()
    {
        var t = (_costResText ?? "").Trim();
        if (t.Length == 0) return null;
        return PooledResAll.FirstOrDefault(x => x.Name.Equals(t, StringComparison.OrdinalIgnoreCase))
            ?? PooledResAll.FirstOrDefault(x => x.Key.Equals(t, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 把"过滤后的结果"**增量**同步进显示集合。
    /// 不能用 Clear + 重填：那会触发 Reset，而 Reset 会把 ComboBox 的选中项清掉，
    /// 可编辑下拉随即把输入框也清空（用户实测："点了下拉里的选项没有自动填入"）。
    /// 增量（Remove/Move/Insert）不动其它项，选中项只要还在结果里就能活下来。
    /// </summary>
    private static void SyncFiltered<T>(System.Collections.ObjectModel.ObservableCollection<T> shown, List<T> want)
    {
        for (var i = shown.Count - 1; i >= 0; i--)
            if (!want.Contains(shown[i])) shown.RemoveAt(i);
        for (var i = 0; i < want.Count; i++)
        {
            if (i >= shown.Count) { shown.Add(want[i]); continue; }
            if (EqualityComparer<T>.Default.Equals(shown[i], want[i])) continue;
            var at = shown.IndexOf(want[i]);
            if (at >= 0) shown.Move(at, i);
            else shown.Insert(i, want[i]);
        }
    }

    private void FilterCostList()
    {
        var keep = _costId;      // 过滤会移除"选中项" → ComboBox 顺手把输入框清空（连带写回 VM）→ 事后补回来
        var t = (_costId ?? "").Trim();
        var want = CostListAll
            .Where(c => t.Length == 0 || c.Id.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0)
            .ToList();
        // **下拉里当前选中的那条永远留在结果里**：它被过滤掉的话 ComboBox 会把选中项清掉，
        // 可编辑下拉随即把输入框也清空（"选了一条再改字 → 字突然没了"）。钉在第一条。
        if (_pickedCostId.Length > 0 && !want.Any(c => c.Id.Equals(_pickedCostId, StringComparison.OrdinalIgnoreCase)))
        {
            var sel = CostListAll.FirstOrDefault(c => c.Id.Equals(_pickedCostId, StringComparison.OrdinalIgnoreCase));
            if (sel is not null) want.Insert(0, sel);
        }
        SyncFiltered(CostListShown, want);
        if (keep.Length > 0 && _costId.Length == 0) { _costId = keep; Raise(nameof(CostId)); }   // 补回被 ComboBox 清掉的文字
    }

    private void FilterResList()
    {
        var keep = _costResText;
        var t = (_costResText ?? "").Trim();
        var want = PooledResAll
            .Where(r => t.Length == 0 || r.Name.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0
                                      || r.Key.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0)
            .ToList();
        if (_pickedResKey.Length > 0 && !want.Any(r => r.Key.Equals(_pickedResKey, StringComparison.OrdinalIgnoreCase)))
        {
            var sel = PooledResAll.FirstOrDefault(r => r.Key.Equals(_pickedResKey, StringComparison.OrdinalIgnoreCase));
            if (sel is not null) want.Insert(0, sel);
        }
        SyncFiltered(PooledResShown, want);
        if (keep.Length > 0 && _costResText.Length == 0) { _costResText = keep; Raise(nameof(CostResText)); }
    }

    /// <summary>下拉里选中了一条成本 → 记下来（过滤时永远保留它，见 FilterCostList）。</summary>
    public void NotePickedCost(string id) => _pickedCostId = (id ?? "").Trim();

    /// <summary>资源下拉里选中了一条 → 同上。</summary>
    public void NotePickedRes(string key) => _pickedResKey = (key ?? "").Trim();

    /// <summary>id 打全了且命中已有成本 → 预填金币/资源，并把按钮切成「保存修改」。</summary>
    private void SyncCostFromId()
    {
        var hit = CostListAll.FirstOrDefault(c => c.Id.Equals((_costId ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
        var wasExisting = _loadedExisting;
        _loadedExisting = hit is not null;
        if (hit is not null)
        {
            _costGold = hit.Gold.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Raise(nameof(CostGold));
            var resName = hit.Resource.Length > 0
                ? (PooledResAll.FirstOrDefault(x => x.Key.Equals(hit.Resource, StringComparison.OrdinalIgnoreCase))?.Name ?? hit.Resource)
                : "";
            _costResText = resName;
            Raise(nameof(CostResText));
            _costResAmount = hit.Resource.Length > 0
                ? hit.ResourceAmount.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
            Raise(nameof(CostResAmount));
            _loadedResKey = hit.Resource;
            _loadedResAmount = hit.ResourceAmount;     // 记下"加载时的数量"：只改数量也要能写回去
            _loadedPoolId = hit.PoolId;                // 只改数量时复用原来那个池（作者起的池名别丢）
        }
        else if (wasExisting)
        {
            // 从"已有"改成新名字：把预填的值清掉，别把上一个成本的金额带过去
            _costGold = ""; Raise(nameof(CostGold));
            _costResText = ""; Raise(nameof(CostResText));
            _costResAmount = ""; Raise(nameof(CostResAmount));
            _loadedResKey = "";
            _loadedResAmount = 0;
            _loadedPoolId = "";
        }
        Raise(nameof(CostActionText));
        Raise(nameof(CostIsExisting));
    }

    /// <summary>「重命名」按钮：把当前成本 id 改成 newId（= 删旧 + 写新；保存后包里不会留旧 id 那条）。</summary>
    public void RenameCurrentCost(string newId)
    {
        var oldId = (CostId ?? "").Trim();
        newId = (newId ?? "").Trim();
        if (newId.Length == 0) { CostStatus = "新 id 不能为空"; return; }
        if (oldId.Length == 0) { CostStatus = "先在「成本 id」里选/填一个要改名的成本"; return; }
        var msg = _backend.RenameWarbandCost(oldId, newId);
        CostStatus = msg;
        if (msg.StartsWith("成本工坊", StringComparison.Ordinal))
        {
            RebuildTree();
            RefreshPooledResources();
            PackOpened?.Invoke();
            AutoSaveSoon();
            _pickedCostId = "";          // 旧 id 没了，别再钉着它
            CostId = newId;              // 输入框跟着换成新 id
        }
    }

    /// <summary>创建前的预览：写哪三张表、什么值（用户确认用）。</summary>
    public string CostPreview
    {
        get
        {
            var id = (CostId ?? "").Trim();
            var gold = ParseDouble(CostGold);
            var act = _loadedExisting ? "修改已有" : "新建";
            var parts = new List<string>
            {
                $"resource_costs_tables（{act}）：id = {(id.Length > 0 ? id : "（还没填）")}，treasury_cost = {gold:0}（{(gold < 0 ? "消耗" : gold > 0 ? "获得" : "0")}）",
            };
            var res = ResolvedResource();
            if (res is not null)
            {
                var amt = (long)ParseDouble(CostResAmount);
                parts.Add($"pooled_resource_factor_junctions_tables：unique_id = {res.Key}_warband_upgrade（factor=other / resource={res.Key} / min=-2147483647 / max=2147483647 / sort_order=0）");
                parts.Add($"resource_cost_pooled_resource_junctions_tables：{res.Key}_warband_upgrade ← 成本 {id}，amount = {amt}（{(amt < 0 ? "消耗" : "获得")}）");
            }
            else
            {
                parts.Add("其他资源：**空的**（不建资源池、不写 resource_cost_pooled_resource_junctions_tables）");
                if (_loadedExisting) parts.Add("（保存时会把这条成本原来的资源关联清掉）");
            }
            return string.Join("\n", parts);
        }
    }

    private static double ParseDouble(string? s) =>
        double.TryParse((s ?? "").Trim(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;

    private void UpdateCostPreview() => Raise(nameof(CostPreview));

    /// <summary>包打开/切换后刷新：成本清单 + 资源清单（原版打底 + 本包覆盖；包里新建的资源也读得到）。</summary>
    public void RefreshPooledResources()
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            CostListAll.Clear();
            foreach (var c in _backend.CostList()) CostListAll.Add(c);
            PooledResAll.Clear();
            foreach (var r in _backend.PooledResources()) PooledResAll.Add(r);
            FilterCostList();
            FilterResList();
            SyncCostFromId();
            sw.Stop();
            Status = $"成本工坊：已有成本 {CostListAll.Count} 个 / 资源 {PooledResAll.Count} 种";
            FileLog.Write($"[成本工坊] 清单已读：成本 {CostListAll.Count} 个 / 资源 {PooledResAll.Count} 种（{sw.ElapsedMilliseconds} ms）");
            Raise(nameof(CostPackLine));      // "成本写进哪个包"那行跟着当前包变
        }
        catch (Exception ex) { Status = "成本工坊清单读取失败：" + ex.Message; }
    }

    /// <summary>主按钮：新建 或 保存修改（资源那块只有"用户真的改了"才动，没动就保留作者原来的池/关联）。</summary>
    public void CreateWarbandCost()
    {
        var id = (CostId ?? "").Trim();
        var gold = ParseDouble(CostGold);
        var res = ResolvedResource();
        var resKey = res?.Key ?? "";
        var amount = (long)ParseDouble(CostResAmount);
        string msg;
        if (_loadedExisting)
        {
            var resSame = _loadedResKey.Equals(resKey, StringComparison.OrdinalIgnoreCase);
            // **资源或数量变了都要重写关联**：以前只看"资源 key 改没改"，只改数量会被整块跳过（用户实测"改数量没生效"）
            msg = _backend.UpdateWarbandCost(id, gold, resKey.Length > 0 ? resKey : null, amount,
                                             !resSame || amount != _loadedResAmount,
                                             resSame ? _loadedPoolId : null);
        }
        else
            msg = _backend.CreateWarbandCost(id, gold, resKey.Length > 0 ? resKey : null, amount);
        CostStatus = msg;
        if (msg.StartsWith("成本工坊", StringComparison.Ordinal))
        {
            RebuildTree();
            RefreshPooledResources();      // 清单里立刻出现/更新
            PackOpened?.Invoke();          // 升级对话框的成本下拉跟着刷新
            AutoSaveSoon();
        }
    }

    /// <summary>删除这条成本（成本行 + 它的资源关联；资源池留着给别的成本用）。</summary>
    public bool DeleteWarbandCost()
    {
        var id = (CostId ?? "").Trim();
        if (!_loadedExisting) { CostStatus = "这个 id 不在包里（下拉里选一个已有成本再删）"; return false; }
        CostStatus = _backend.DeleteWarbandCost(id);
        RebuildTree();
        RefreshPooledResources();
        PackOpened?.Invoke();
        AutoSaveSoon();
        return true;
    }

    public bool AddUiAssetToPack(UiAssetItem? item)
    {
        if (item is null) return false;
        if (_backend.Pack?.IsOpen != true) { Status = "先打开一个包"; return false; }
        _backend.PushUndo();
        // 目标名由后端自动定：素材 key + _1/_2…（防重名；不给用户弹框了）
        var target = _backend.AddUiAssetToPack(new WarbandStudio.Ui.Services.Backend.UiAsset(
            item.Kind, item.Name, item.File, item.Url, item.PackPath, item.Size));
        if (target.Length == 0) { Status = "这张图没能加进包（名字 _1…_999 都被占了？）"; return false; }
        RebuildTree();                 // 树里立刻出现「<文件名>（待导出）」——不然会以为"添加到包没生效"
        PackOpened?.Invoke();          // **立刻重推画布**：换图/新建页签的选择器要马上能看到这张图
        Status = $"已把「{item.Name}」加进当前包 → {System.IO.Path.GetFileName(target)}（要用到页签上走「换图」，那一步会按页签 key 命名）　{_backend.EditSummary}";
        return true;
    }

    public void PickUnit(UnitCard? card)
    {
        if (card is null) return;
        UnitPicked?.Invoke(card.Key, card.Url);
        Status = $"把「{card.Key}」放到画布…（新开一组）";
    }

    /// <summary>给界面/代码后置设置一行状态（Status 的 setter 是私有的）。</summary>
    /// <summary>Ctrl+Tab / Ctrl+Shift+Tab：在已打开的页签之间循环（多个 pack 画布）。</summary>
    public void CycleTab(int delta)
    {
        if (CenterTabs.Count == 0) return;
        var i = CenterTabs.IndexOf(_activeCenterTab);
        if (i < 0) i = 0;
        var n = ((i + delta) % CenterTabs.Count + CenterTabs.Count) % CenterTabs.Count;
        ActiveCenterTab = CenterTabs[n];
    }

    public void SetStatus(string text) => Status = text;

    /// <summary>画布回执：某个兵被放进新组（记待导出，junction 写回在下一步）。</summary>
    public void OnCanvasUnitAdded(string group, string unit, int x, int y)
    {
        _backend.AddUnitToGroup(group, unit, x, y, isNewGroup: true);
        Status = $"画布新组「{group}」← {unit}（{x},{y}）；{_backend.EditSummary}";
    }

    /// <summary>
    /// 画布上的编辑统一从这里进（都在内存里记成"待导出"，点导出/另存时由 WarbandAmender 落表）。
    /// 处理完把画布数据重推一遍，页面立刻能看到新连线/新组。
    /// </summary>
    public void ApplyCanvasEdit(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var m = doc.RootElement;
            string S(string k) => m.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            // 坐标可能是小数（画布里按 40×87 的卡中心算落点就会出现 .5），GetInt32 会直接抛 FormatException
            int I(string k) => m.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number
                ? v.TryGetInt32(out var n) ? n : (int)Math.Round(v.GetDouble())
                : 0;
            bool B(string k) => m.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
            double Dou(string k) => m.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number
                ? v.GetDouble() : 0;                     // mid_link_offset 这类：浮点、带符号
            var type = S("type");
            // 除了 undo/redo，其它编辑消息在应用前先压一份快照（Ctrl+Z 能退回上一步）
            if (type != "undo" && type != "redo") _backend.PushUndo();
            switch (type)
            {
                case "addUnit":
                case "addUnitDrop":
                {
                    var g = S("group");
                    var isNew = B("isNew") || g.Length == 0;
                    // 新组归**当前页签**（页面上选着哪个页签就归哪个）；
                    // 注意：消息里没有 category（拖进已有组）时必须传 null —— 传空串会把那个组的页签清掉
                    var cat = S("category");
                    // 新组命名：全局选项填了前缀就按 `<前缀>_<页签>_<兵种词…>`（见 GroupNaming），没填退回时间戳名
                    if (isNew) g = _backend.MakeGroupKey(cat.Length > 0 ? cat : null, new[] { S("unit") },
                                                         "studio_new_" + DateTime.Now.ToString("HHmmssff"));
                    // overwrite：画布上问过"这个兵已经被使用"且用户选了继续 → 覆盖旧的 junction（只留这次的位置）
                    _backend.AddUnitToGroup(g, S("unit"), I("x"), I("y"), isNew, cat.Length > 0 ? cat : null,
                                            overwrite: B("overwrite"));
                    // 军事组授权：选了派系只填它那一个组；只选种族就填该种族所有组
                    _backend.GrantUnit(S("unit"), _selectedFaction?.Race, _selectedFaction is { IsFaction: true } ? _selectedFaction.Key : null);
                    RefreshLibrarySoon();          // 兵种库的"已在画布上"灰显立刻跟上
                    break;
                }
                case "removeUnit":
                    _backend.RemoveUnitFromGroup(S("group"), S("unit"));
                    RefreshLibrarySoon();
                    break;
                case "removeGroup":
                    _backend.RemoveGroup(S("group"));
                    RefreshLibrarySoon();
                    break;
                case "setCategory":
                    _backend.SetGroupCategory(S("group"), S("category"), I("x"), I("y"));
                    break;
                case "moveUnits":
                {
                    // 多选建组：选中的兵从原组摘掉、并进新组（写 junction 删+加，再建组与坐标）
                    var g2 = S("group");
                    var cat2 = S("category");
                    var units = new List<(string Group, string Unit)>();
                    if (m.TryGetProperty("units", out var arr) && arr.ValueKind == JsonValueKind.Array)
                        foreach (var u in arr.EnumerateArray())
                        {
                            var ug = u.TryGetProperty("group", out var gv2) ? gv2.GetString() ?? "" : "";
                            var uu = u.TryGetProperty("unit", out var uv2) ? uv2.GetString() ?? "" : "";
                            if (ug.Length > 0 && uu.Length > 0) units.Add((ug, uu));
                        }
                    if (units.Count == 0) return;
                    _backend.MoveUnitsToNewGroup(g2, units, I("x"), I("y"), cat2.Length > 0 ? cat2 : null,
                                                 _selectedFaction?.Race,
                                                 _selectedFaction is { IsFaction: true } ? _selectedFaction.Key : null);
                    RefreshLibrarySoon();
                    break;
                }
                case "mergeGroups":
                    _backend.MergeGroups(S("keep"), new[] { S("drop") });
                    RefreshLibrarySoon();
                    break;
                case "mergeMany":
                {
                    var gs = new List<string>();
                    if (m.TryGetProperty("groups", out var ga) && ga.ValueKind == JsonValueKind.Array)
                        foreach (var x2 in ga.EnumerateArray()) { var v2 = x2.GetString() ?? ""; if (v2.Length > 0) gs.Add(v2); }
                    if (gs.Count == 0) return;
                    var keep = S("group");
                    var cat3 = S("category");
                    _backend.MergeGroups(keep, gs, I("x"), I("y"), cat3.Length > 0 ? cat3 : null);
                    RefreshLibrarySoon();
                    break;
                }
                case "newGroup":
                    _backend.NewGroup(S("group"), I("x"), I("y"), S("category").Length > 0 ? S("category") : null);
                    break;
                case "addToMilGroup":
                {
                    var u1 = new List<string>();
                    if (m.TryGetProperty("units", out var ua) && ua.ValueKind == JsonValueKind.Array)
                        foreach (var x3 in ua.EnumerateArray()) { var s3 = x3.GetString() ?? ""; if (s3.Length > 0) u1.Add(s3); }
                    foreach (var uu in u1)
                        _backend.GrantUnit(uu, _selectedFaction?.Race, _selectedFaction is { IsFaction: true } ? _selectedFaction.Key : null);
                    RefreshLibrarySoon();
                    break;
                }
                case "removeFromMilGroup":
                {
                    var u2 = new List<string>();
                    if (m.TryGetProperty("units", out var ub) && ub.ValueKind == JsonValueKind.Array)
                        foreach (var x4 in ub.EnumerateArray()) { var s4 = x4.GetString() ?? ""; if (s4.Length > 0) u2.Add(s4); }
                    _backend.RemoveUnitFromMilGroup(u2, _selectedFaction?.Race,
                                                   _selectedFaction is { IsFaction: true } ? _selectedFaction.Key : null);
                    RefreshLibrarySoon();
                    break;
                }
                case "deletePackArt":
                {
                    // 换图对话框里右键一张缩略图 →「从包中删除」（清旧图 / 没用到的素材；待导出）
                    var f = S("file").Replace((char)92, '/');
                    if (f.Length == 0) break;
                    var name = Path.GetFileName(f);
                    var owners = _backend.TabsUsingArt(f);
                    if (owners.Count > 0 &&
                        !(Confirm?.Invoke($"「{name}」正被 {string.Join("、", owners)} 页签用着 —— 删了它们会丢图。\n\n仍要从包里删除吗？") ?? false))
                    {
                        Status = "已取消删除";
                        break;
                    }
                    _backend.RemovePackFile(f);                 // 待导出新增→撤掉；包内条目→导出时跳过
                    Status = $"已从包里删除 {name}（待导出）" +
                             (owners.Count > 0 ? $"（原在用：{string.Join("、", owners)}）" : "");
                    break;
                }
                case "renameTab":
                    _backend.RenameTab(S("old"), S("new"));
                    break;
                case "closeTab":
                    _backend.CloseOpenedTab(S("category"));
                    break;
                case "applyTabToRaces":
                {
                    var rs = new List<string>();
                    if (m.TryGetProperty("races", out var ra) && ra.ValueKind == JsonValueKind.Array)
                        foreach (var x6 in ra.EnumerateArray()) { var s6 = x6.GetString() ?? ""; if (s6.Length > 0) rs.Add(s6); }
                    _backend.ApplyTabToRaces(S("category"), rs);
                    break;
                }
                case "openTabs":
                {
                    var tl = new List<string>();
                    if (m.TryGetProperty("tabs", out var ta) && ta.ValueKind == JsonValueKind.Array)
                        foreach (var x5 in ta.EnumerateArray()) { var s5 = x5.GetString() ?? ""; if (s5.Length > 0) tl.Add(s5); }
                    _backend.OpenTabs(tl);
                    break;
                }
                case "setAxis":
                {
                    // 多选统一坐标：**只改一个轴**（X 或 Y），另一轴各自保留 —— 单选是 x/y 一起写，
                    // 多选那样会把每组不同的另一个轴也抹平（用户要"方便统一坐标值"）
                    var gl = new List<string>();
                    if (m.TryGetProperty("groups", out var ga) && ga.ValueKind == JsonValueKind.Array)
                        foreach (var x6 in ga.EnumerateArray()) { var s6 = x6.GetString() ?? ""; if (s6.Length > 0) gl.Add(s6); }
                    var axis = S("axis");
                    var val = I("value");
                    var n6 = _backend.SetGroupsAxis(gl, axis, val);
                    Status = n6 > 0 ? $"已把 {n6} 个组的 {axis.ToUpperInvariant()} 统一为 {val}（另一轴不动）　{_backend.EditSummary}" : "没选组";
                    RebuildTree();
                    PackOpened?.Invoke();
                    AutoSaveSoon();
                    return;
                }
                case "splitGroup":
                {
                    var cat4 = S("category");
                    var n4 = _backend.SplitGroup(S("group"), I("x"), I("y"), cat4.Length > 0 ? cat4 : null,
                                                 _selectedFaction?.Race,
                                                 _selectedFaction is { IsFaction: true } ? _selectedFaction.Key : null);
                    if (n4 == 0) Status = "这个组只有一个兵，不用拆";
                    break;
                }
                case "setTabArt":
                    _backend.SetTabArt(S("category"), S("bg").Length > 0 ? S("bg") : null, S("btn").Length > 0 ? S("btn") : null);
                    LoadUiAssets();          // 素材用到页签上会被改名 → 面板跟着刷新
                    break;
                case "newTab":
                {
                    var tabKey = S("key");
                    var race0 = _selectedFaction?.Race;
                    var fac0 = _selectedFaction is { IsFaction: true } ? _selectedFaction.Key : null;
                    _backend.NewTab(tabKey, S("donor"), S("bg").Length > 0 ? S("bg") : null, S("btn").Length > 0 ? S("btn") : null);
                    _backend.RememberTabScope(tabKey, race0, fac0);      // 记下归属：种族级/派系级
                    LoadUiAssets();                                      // 选中的素材已改名为 <前缀><页签key>.png
                    break;
                }
                case "importTab":
                {
                    // 「从其他 mod 导入页签」：每个勾选的页签都走一遍（键+两张图来自来源包）
                    var kl = new List<string>();
                    if (m.TryGetProperty("keys", out var ka) && ka.ValueKind == JsonValueKind.Array)
                        foreach (var x7 in ka.EnumerateArray()) { var s7 = x7.GetString() ?? ""; if (s7.Length > 0) kl.Add(s7); }
                    var race7 = _selectedFaction?.Race;
                    var fac7 = _selectedFaction is { IsFaction: true } ? _selectedFaction.Key : null;
                    var done = 0;
                    foreach (var k7 in kl)
                    {
                        var r7 = _backend.ImportTab(S("pack"), k7, race7, fac7);
                        if (r7.StartsWith("从 ", StringComparison.Ordinal))
                        {
                            _backend.RememberTabScope(k7, race7, fac7);   // 归属跟新建页签一个口径
                            done++;
                        }
                        else Status = r7;
                    }
                    if (done > 0) Status = $"已从其他 mod 导入 {done} 个页签：{string.Join("、", kl)}　{_backend.EditSummary}";
                    LoadUiAssets();
                    RebuildTree();
                    PackOpened?.Invoke();
                    AutoSaveSoon();
                    return;
                }
                case "addRoute":
                    // routeOnly = 跨页关系：只写路线表，不写界面连线（面板与连接模式都会带这个旗标）
                    _backend.AddRoute(S("base"), S("target"), S("cost"), I("rank"), I("sub"), B("mutual"),
                                      I("childPos"), I("parentPos"), autoCost: I("autoCost"),
                                      alsoLink: !B("routeOnly"), midOffset: Dou("midOffset"));
                    break;
                case "updateRoute":
                    _backend.UpdateRoute(S("key"), S("base"), S("target"), S("cost"), I("rank"), I("sub"));
                    break;
                case "swapRoute":
                    _backend.SwapRoute(S("key"), S("base"), S("target"), S("cost"), I("rank"), I("sub"),
                                       I("childPos"), I("parentPos"), alsoLink: !B("routeOnly"),
                                       midOffset: Dou("midOffset"));
                    break;
                case "undo":
                    if (!_backend.Undo()) Status = "没有可撤销的步骤";
                    else { PackOpened?.Invoke(); RebuildTree(); RefreshLibrarySoon(); Status = "已撤销一步：" + _backend.EditSummary; }
                    return;
                case "redo":
                    if (!_backend.Redo()) Status = "没有可重做的步骤";
                    else { PackOpened?.Invoke(); RebuildTree(); RefreshLibrarySoon(); Status = "已重做一步：" + _backend.EditSummary; }
                    return;
                case "hotkey":
                    switch (S("key"))
                    {
                        case "save": _ = SaveAsync(null); break;
                        case "saveAs": _ = SaveAsAsync(); break;
                        case "nextTab": CycleTab(1); break;
                        case "prevTab": CycleTab(-1); break;
                    }
                    return;
                case "setPos":
                    _backend.SetGroupPos(S("group"), I("x"), I("y"));
                    _backend.InvalidateCanvas();
                    break;
                case "moveMany":
                {
                    // 多选整批拖动：一次消息落完所有组，再把画布/页头/兵种库刷一遍
                    var n5 = 0;
                    if (m.TryGetProperty("groups", out var ma) && ma.ValueKind == JsonValueKind.Array)
                        foreach (var it in ma.EnumerateArray())
                        {
                            var gk = it.TryGetProperty("group", out var gv5) && gv5.ValueKind == JsonValueKind.String ? gv5.GetString() ?? "" : "";
                            if (gk.Length == 0) continue;
                            var mx = it.TryGetProperty("x", out var xv5) && xv5.ValueKind == JsonValueKind.Number
                                ? (int)Math.Round(xv5.GetDouble()) : 0;
                            var my = it.TryGetProperty("y", out var yv5) && yv5.ValueKind == JsonValueKind.Number
                                ? (int)Math.Round(yv5.GetDouble()) : 0;
                            _backend.SetGroupPos(gk, mx, my);
                            n5++;
                        }
                    Status = $"已移动 {n5} 个组；{_backend.EditSummary}";
                    break;                    // 落到公共收尾：重推画布
                }
                case "move":
                    _backend.SetGroupPos(S("group"), I("x"), I("y"));
                    _backend.InvalidateCanvas();
                    Status = $"已移动「{S("group")}」（{I("x")},{I("y")}）拖动即应用；{_backend.EditSummary}";
                    RebuildTree();             // 拖动也会改 studio_layout → 树里的红标要跟上
                    PackOpened?.Invoke();      // **松手就重推一次**：页面与坐标框跟着"已记录的位置"走（不会再出现"看似没应用"）
                    AutoSaveSoon();            // 拖动松手也走自动保存（防抖合并）
                    return;
                case "removeLink":
                    // 只删这条界面连线（路线行不动）——给"这条线找不到对应的升级行"和"多选→删除连线"用
                    _backend.RemoveLinkOnly(S("child"), S("parent"));
                    break;
                case "addLink":
                case "adjustLink":
                    // 只补/只调这条界面连线（路线行不动）：多选「添加连线」、自动/手动调整画线、拖转点手柄都走这里；
                    // midOffset = 中间那段沿起始方向走多少像素（写 ui_links.mid_link_offset，游戏里同一个走线）
                    _backend.AddLinkOnly(S("child"), S("parent"), I("childPos"), I("parentPos"),
                                         type == "adjustLink" ? "调整画线" : "补连线", Dou("midOffset"));
                    break;
                case "removeRoute":
                    _backend.RemoveRoute(S("key"), S("base"), S("target"), alsoLink: !B("routeOnly"));
                    break;
                default:
                    FileLog.Write("画布编辑：不认识的消息 " + type);
                    return;
            }
            Status = "画布编辑：" + _backend.EditSummary + "（点「导出…」写进 pack）";
            _backend.InvalidateCanvas();   // 有改动 → 缓存失效 → 重算
            // **每次编辑都重建树**：文件树要"随改随变"——红标（这次会动到哪些文件）和「（待导出）」都得立刻反映，
            // 以前只在"换图/新建页签"后重建，红标要等到下一次保存/重开才出现（用户实测到的问题）
            RebuildTree();
            PackOpened?.Invoke();          // 重推画布数据，页面上立刻生效
            AutoSaveSoon();                // 开了自动保存 → 停下来就写回原包
        }
        catch (Exception ex)
        {
            FileLog.Write("画布编辑失败：" + json, ex);
            Status = "画布编辑失败：" + ex.Message;
        }
    }

    /// <summary>读种族/派系列表（原版 db.pack 打底 + 当前包覆盖）。打开包之后调一次。</summary>
    public async Task LoadFactionsAsync()
    {
        if (_backend.Pack?.IsOpen != true) return;
        try
        {
            var json = await Task.Run(() => _backend.BuildFactionJson());
            // **就地更新**（不 Clear 重建）：清空重建会把 TreeView 的选中项/展开状态弄丢，
            // 用户的"选了某个种族/派系"就被重置了。这里按 Key 对齐，只更新计数与子表。
            var fresh = new List<FactionNode>();
            using var doc = JsonDocument.Parse(json);
            // 不再放「全部兵」那条：一次算全派系的兵很慢，实际也没人用（要看得先选种族/派系）
            foreach (var r in doc.RootElement.GetProperty("races").EnumerateArray())
            {
                var sub = r.GetProperty("race").GetString() ?? "";
                var raceName = r.TryGetProperty("name", out var rn) && rn.ValueKind == JsonValueKind.String
                    ? rn.GetString()! : sub;
                var node = new FactionNode
                {
                    Key = "race:" + sub,
                    Race = sub,
                    Display = raceName,
                    Tip = $"种族（亚文化）key：{sub}",
                    UnitCount = r.TryGetProperty("units", out var ru) ? ru.GetInt32() : 0,
                };
                var genericUnits = r.TryGetProperty("genericUnits", out var gu) ? gu.GetInt32() : 0;
                var legendaries = 0;
                foreach (var f in r.GetProperty("factions").EnumerateArray())
                {
                    if (!(f.TryGetProperty("legendary", out var lg) && lg.GetBoolean())) continue;   // 非传奇 → 归「通用」
                    var uc = f.GetProperty("units").GetInt32();
                    if (uc == 0) continue;
                    var key = f.GetProperty("key").GetString() ?? "";
                    var name = f.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String
                        ? nm.GetString()! : key;
                    node.Children.Add(new FactionNode
                    {
                        Key = key, Race = sub, Display = name, UnitCount = uc,
                        Badge = f.TryGetProperty("milGeneric", out var gv) && gv.ValueKind == JsonValueKind.True ? "通" : "专",
                        Tip = f.TryGetProperty("milGroup", out var mgv) && mgv.ValueKind == JsonValueKind.String
                            ? $"派系 key：{key}　军事组：{mgv.GetString()}（{(f.TryGetProperty("milGeneric", out var g2) && g2.ValueKind == JsonValueKind.True ? "通用军事组" : "专属军事组")}）"
                            : $"派系 key：{key}",
                    });
                    legendaries++;
                }
                if (genericUnits > 0)
                    node.Children.Add(new FactionNode
                    {
                        Key = "generic:" + sub, Race = sub, Display = "通用军事组",
                        Tip = $"「{raceName}」里非传奇派系的兵（合并显示）", UnitCount = genericUnits,
                    });
                if (node.Children.Count > 0 || legendaries > 0) fresh.Add(node);
            }
            SyncFactionTree(fresh);
            Status = $"种族/派系：{Factions.Count} 个种族（选一个看它的兵）";
            Raise(nameof(LibraryTitle));
        }
        catch (Exception ex)
        {
            Status = "派系列表读不出来：" + ex.Message;
            FileLog.Write("派系列表失败", ex);
        }
    }

    /// <summary>把新的种族/派系树**就地同步**进现有集合：按 Key 复用旧节点对象（保住 TreeView 选中态），只更新计数/增删子项。</summary>
    private void SyncFactionTree(List<FactionNode> fresh)
    {
        var oldByKey = new Dictionary<string, FactionNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in Factions) oldByKey[n.Key] = n;
        for (var i = 0; i < fresh.Count; i++)
        {
            var want = fresh[i];
            if (oldByKey.TryGetValue(want.Key, out var have))
            {
                have.UnitCount = want.UnitCount;                 // 计数是 var
                SyncChildren(have, want);
                var at = Factions.IndexOf(have);
                if (at != i) { Factions.Move(at, i); }           // 顺序对齐（Move 不动节点对象）
            }
            else Factions.Insert(i, want);
        }
        while (Factions.Count > fresh.Count) Factions.RemoveAt(Factions.Count - 1);
    }

    private static void SyncChildren(FactionNode have, FactionNode want)
    {
        var oldByKey = new Dictionary<string, FactionNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in have.Children) oldByKey[c.Key] = c;
        for (var i = 0; i < want.Children.Count; i++)
        {
            var w = want.Children[i];
            if (oldByKey.TryGetValue(w.Key, out var h))
            {
                h.UnitCount = w.UnitCount;
                var at = have.Children.IndexOf(h);            // Children 是普通 List：没有 Move，用删+插
                if (at != i) { have.Children.RemoveAt(at); have.Children.Insert(i, h); }
            }
            else have.Children.Insert(i, w);
        }
        while (have.Children.Count > want.Children.Count) have.Children.RemoveAt(have.Children.Count - 1);
    }

    /// <summary>读兵种库（faction 传族/派系 key；空 = 全部）。</summary>
    public async Task LoadLibraryAsync(string? faction)
    {
        if (_backend.Pack?.IsOpen != true) return;
        IsBusy = true;
        try
        {
            var json = await Task.Run(() => _backend.BuildUnitLibraryJson(faction));
            ApplyLibraryJson(json);
            Status = $"兵种库：{Library.Count} 个兵";
        }
        catch (Exception ex)
        {
            Status = "兵种库读不出来：" + ex.Message;
            FileLog.Write("兵种库失败", ex);
        }
        finally { IsBusy = false; }
    }

    private int _facRefreshGen;
    private async Task RefreshFactionsSoon()
    {
        if (_backend.Pack?.IsOpen != true) return;
        var gen = ++_facRefreshGen;
        try
        {
            await Task.Delay(150);                                  // 连续编辑时合并成一次
            if (gen != _facRefreshGen) return;
            await LoadFactionsAsync();
        }
        catch (Exception ex) { FileLog.Write("种族/派系自动刷新失败", ex); }
    }

    /// <summary>兵种库 JSON → 界面（读库和"编辑后自动刷新"共用这一段）。</summary>
    private void ApplyLibraryJson(string json)
    {
        _libAll = [];
        using var doc = JsonDocument.Parse(json);
        var icons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (doc.RootElement.TryGetProperty("icons", out var ic))
            foreach (var p in ic.EnumerateObject()) icons[p.Name] = p.Value.GetString() ?? "";
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (doc.RootElement.TryGetProperty("names", out var nc))
            foreach (var p in nc.EnumerateObject()) names[p.Name] = p.Value.GetString() ?? "";
        foreach (var u in doc.RootElement.GetProperty("units").EnumerateArray())
        {
            var k = u.GetString() ?? "";
            if (k.Length == 0) continue;
            icons.TryGetValue(k, out var url);
            names.TryGetValue(k, out var nm);
            var inTree = doc.RootElement.TryGetProperty("inTree", out var it)
                && it.ValueKind == JsonValueKind.Array && it.EnumerateArray().Any(x => x.GetString() == k);
            _libAll.Add(new UnitCard(k, url, _backend.LocalIconPath(url), nm, inTree));
        }
        ApplyLibFilter();                 // 搜索框里的词在重新读库后依然生效
    }

    /// <summary>
    /// 画布编辑之后**自动刷新兵种库**（"已经在画布上"的灰显、专属/授权变化立刻跟上）。
    /// 静默做：不动忙碌态、不动状态栏；用代数号防止旧结果覆盖新结果。
    /// </summary>
    private int _libRefreshGen;
    private async void RefreshLibrarySoon()
    {
        _ = RefreshFactionsSoon();          // 编辑也会改种族/派系的兵数 → 树也刷（就地更新，不丢选中）
        if (_selectedFaction is null || _backend.Pack?.IsOpen != true) return;
        var gen = ++_libRefreshGen;
        var key = _selectedFaction.Key;
        try
        {
            var json = await Task.Run(() => _backend.BuildUnitLibraryJson(key));
            // 期间又刷新过 / 换了种族派系 → 这次结果作废
            if (gen != _libRefreshGen || !string.Equals(key, _selectedFaction?.Key, StringComparison.OrdinalIgnoreCase)) return;
            ApplyLibraryJson(json);
        }
        catch (Exception ex) { FileLog.Write("兵种库自动刷新失败", ex); }
    }

    /// <summary>点文件树里的表 → 原生解码 → 中间栏开一个表页签。</summary>
    public async Task OpenTableAsync(string innerPath)
    {
        if (!PackSession.IsDbTable(innerPath))
        {
            Status = "这个文件不是 DB 表（目前表视图只支持 db/ 下的表）";
            return;
        }
        IsBusy = true;
        try
        {
            Status = "正在解表：" + innerPath;
            var table = await Task.Run(() => _backend.ReadTable(innerPath));   // 原生解码（内存）

            var dt = new DataTable(table.TableName);
            foreach (var c in table.Columns) dt.Columns.Add(c.Name, typeof(string));
            foreach (var row in table.Rows)
            {
                var dr = dt.NewRow();
                for (var i = 0; i < row.Length && i < table.Columns.Count; i++) dr[i] = row[i].ToTsv();
                dt.Rows.Add(dr);
            }

            FileLog.Write($"表数据就绪：{table.TableName} {table.Rows.Count} 行 × {table.Columns.Count} 列，开始建 UI 页签");
            var existing = CenterTabs.OfType<TableTab>().FirstOrDefault(t => t.InnerPath == innerPath);
            var tab = existing ?? new TableTab
            {
                Header = table.TableName,
                View = dt.DefaultView,
                TableName = table.TableName,
                InnerPath = innerPath,
                RowCount = table.Rows.Count,
                ColumnCount = table.Columns.Count,
            };
            if (existing is null) CenterTabs.Add(tab);
            ActiveCenterTab = tab;
            RefreshEngineText();
            Status = $"已打开表：{tab.Info}";
            FileLog.Write("表页签已激活：" + tab.Header);
        }
        catch (Exception e)
        {
            FileLog.Write("打开表失败", e);
            Status = "打开表失败：" + e.Message;
        }
        finally { IsBusy = false; }
    }

    /// <summary>重读一个表页签（切到它时自动跑；表内容跟着当前包的最新状态走，不再是一次性快照）。</summary>
    private async Task ReloadTableTabAsync(TableTab tab)
    {
        try
        {
            var table = await Task.Run(() => _backend.ReadTable(tab.InnerPath));
            var dt = tab.View?.Table;
            if (dt is null) return;
            dt.BeginLoadData();
            dt.Rows.Clear();
            foreach (var row in table.Rows)
            {
                var dr = dt.NewRow();
                for (var i = 0; i < row.Length && i < table.Columns.Count; i++) dr[i] = row[i].ToTsv();
                dt.Rows.Add(dr);
            }
            dt.EndLoadData();
            tab.RowCount = table.Rows.Count;
            Status = $"表已刷新：{tab.TableName}（{table.Rows.Count} 行）";
        }
        catch (Exception ex) { FileLog.Write("表刷新失败：" + tab.InnerPath, ex); }
    }

    /// <summary>关掉一个表页签。</summary>
    public void CloseTableTab(TableTab tab)
    {
        CenterTabs.Remove(tab);
        if (ReferenceEquals(ActiveCenterTab, tab)) ActiveCenterTab = CenterTabs[0];
    }

    // ── 启动流程 ────────────────────────────────────────────────

    /// <summary>启动：起后端；需要的话走首跑向导；有游戏目录就应用（选游戏 + schema 自举）。</summary>
    public async Task InitializeAsync()
    {
        IsBusy = true;
        try
        {
            if (CenterTabs.Count > 0) _activeCenterTab = CenterTabs[0];
            RefreshEngineText();

            // 没设过、或设的目录已经不可用 → 弹选择框（列出扫描到的候选，用户点了才算）
            if (string.IsNullOrWhiteSpace(GameDir) || !GameLocator.LooksLikeGame(GameDir))
            {
                Status = "请选择战锤3 游戏目录";
                if (GameDirRequested is not null)
                {
                    var picked = await GameDirRequested(GameDir.Length > 0 ? GameDir : null);
                    if (!string.IsNullOrWhiteSpace(picked)) GameDir = picked!.Trim();
                }
            }

            if (!string.IsNullOrWhiteSpace(GameDir) && GameLocator.LooksLikeGame(GameDir))
                await ApplyGameAsync();
            else if (!string.IsNullOrWhiteSpace(GameDir))
                Status = "这个目录里没找到 data\\*.pack，请在左下「全局选项」里改一个正确的游戏目录";

            // **v1.5.0：启动 = 空状态** —— 不再自动生成/打开 WUU 参考包（它仍内置在 bundled/wuu，
            // 走「导入 WUU 模板」）；用户从欢迎面板「新建工程 / 从 Pack 打开工程 / 打开工程」开始。
            // 这里只把"最近工程"里的失效项剔掉，给欢迎面板用。
            PruneRecentProjects();

            // 启动收尾：**强制重刷一次**文件树 + 推一次画布数据（之前出现"刚启动左边空、画布等待数据"）
            RebuildTree();
            FileLog.Write($"启动收尾：pack=(无)，树根 {TreeRoots.Count} 个（空状态，等新建/打开工程）");
            PackOpened?.Invoke();
            LoadUiAssets();
            // 成本工坊的清单（成本 id / 选用资源两个下拉）**也要在这条"启动自动开包"的路上填上** ——
            // 以前只有"手动打开包"那条路会填（OpenPackPathAsync），一进工具直接点「成本工坊」两个下拉都是空的，
            // 点箭头弹出个空条 → 看着就是"箭头点了没反应、不弹选项"（用户实测）。
            RefreshPooledResources();
            if (CenterTabs.Count == 0 && _backend.Pack is { IsOpen: true } p0) EnsureCanvasTab(p0);
            if (CenterTabs.Count > 0) ActiveCenterTab = CenterTabs[0];   // 开好包就把画布摆到前面
            Raise(nameof(IsCanvasActive));
            Raise(nameof(IsWelcomeVisible));
        }
        catch (Exception e)
        {
            FileLog.Write("启动流程失败", e);
            Status = "后端启动失败：" + e.Message;
        }
        finally
        {
            _settings.FirstRunDone = true;
            _settings.Save();
            IsBusy = false;
        }
    }

    // ── 工程（v1.5.0）──────────────────────────────────────────

    /// <summary>切工程/关工程/还原前统一处理未导出编辑：三选（是=**保存所有有编辑的包** / 否=放弃 / 取消=中止）。
    /// 返回 false = 用户取消或保存失败（调用方应中止操作）。</summary>
    private async Task<bool> ConfirmUnsavedBeforeAsync(string what)
    {
        var (packs, edits) = UnsavedEdits();
        if (packs == 0) return true;
        var choice = AskSave?.Invoke(what, $"{edits} 处未导出（{packs} 个包）") ?? 1;
        if (choice == 2) { Status = "已取消"; return false; }
        if (choice == 0) return await SaveAllEditsAsync();
        foreach (var s in _backend.Packs.ToList()) _backend.DiscardEditsOf(s.PackPath);
        return true;
    }

    /// <summary>新建工程（"新建工程"对话框的结果）：工程目录 + 生成 Pack 目录 + 项目 key；
    /// 建好骨架（project.json + old/ + Pack 目录），**不自动导入任何包**。</summary>
    public async Task<bool> NewProjectAsync(string dir, string? packDir = null, string? projectKey = null)
    {
        try
        {
            dir = Path.GetFullPath(dir);
            if (!await ConfirmUnsavedBeforeAsync("新建工程")) return false;
            var info = ProjectStore.Ensure(dir, prefillKey: projectKey ?? _settings.GroupKeyPrefix, packDir: packDir);
            if (!string.IsNullOrWhiteSpace(projectKey)) info.ProjectKey = projectKey!.Trim();
            if (!string.IsNullOrWhiteSpace(packDir)) info.PackDir = packDir!.Trim();
            ProjectStore.Save(dir, info);
            _projectDir = dir;
            Project = info;
            _backend.ProjectKey = info.ProjectKey;
            ProjectStore.TouchRecent(_settings.RecentProjects, dir);
            _settings.Save();
            PruneRecentProjects();
            var packs = ProjectStore.PacksIn(dir);
            if (packs.Count > 0) await OpenPackPathAsync(packs[0], open: true, asUnitPack: false);
            Status = packs.Count > 0
                ? $"工程「{info.Name}」已打开：{Path.GetFileName(packs[0])}"
                : $"工程「{info.Name}」已建好（Pack 目录 {info.PackDir}）—— 用「导入 WUU 模板」或「导入 Pack」放一个包进来";
            FileLog.Write($"工程：新建 {dir}（Pack 目录 {ProjectStore.PackDirOf(dir, info)}，项目 key「{info.ProjectKey}」）");
            return true;
        }
        catch (Exception ex) { Status = "新建工程失败：" + ex.Message; FileLog.Write("新建工程失败", ex); return false; }
    }

    /// <summary>打开一个已有工程（打开它的 lastPack；里面没包就登记着，等导入）。</summary>
    public Task<bool> OpenProjectAsync(string dir) => OpenProjectAtAsync(dir);

    /// <summary>「打开上次工程」：直接开最近工程列表里的第一个（没有就提示从哪开始）。</summary>
    public async Task<bool> OpenLastProjectAsync()
    {
        PruneRecentProjects();
        var last = RecentProjects.FirstOrDefault();
        if (last is null)
        {
            Status = "还没有最近工程 —— 用「新建工程」，或在菜单「工程」里选「从 Pack 打开工程」";
            return false;
        }
        return await OpenRecentProjectAsync(last);
    }

    private async Task<bool> OpenProjectAtAsync(string dir)
    {
        try
        {
            dir = Path.GetFullPath(dir);
            if (!ProjectStore.IsProject(dir))
            {
                Status = "这个文件夹还不是工程（没有 project.json）——用「新建工程」，或菜单「从 Pack 打开工程」";
                return false;
            }
            if (!await ConfirmUnsavedBeforeAsync("打开工程")) return false;
            var info = ProjectStore.Ensure(dir, prefillKey: _settings.GroupKeyPrefix);
            _projectDir = dir;
            Project = info;
            _backend.ProjectKey = info.ProjectKey;
            ProjectStore.TouchRecent(_settings.RecentProjects, dir);
            _settings.Save();
            PruneRecentProjects();
            var packs = ProjectStore.PacksIn(dir);
            var open = !string.IsNullOrWhiteSpace(info.LastPack) && File.Exists(info.LastPack)
                ? info.LastPack
                : packs.Count > 0 ? packs[0] : null;
            if (open is not null) await OpenPackPathAsync(open, open: true, asUnitPack: false);
            Status = packs.Count switch
            {
                0 => $"工程「{info.Name}」已打开 —— 用「导入 WUU 模板」或「导入 Pack」放一个包进来",
                _ => $"工程「{info.Name}」已打开：{Path.GetFileName(open!)}" +
                     (packs.Count > 1 ? $"（工程里还有 {packs.Count - 1} 个包）" : ""),
            };
            FileLog.Write($"工程：打开 {dir}（项目 key「{info.ProjectKey}」，包 {packs.Count} 个）");
            return true;
        }
        catch (Exception ex) { Status = "打开工程失败：" + ex.Message; FileLog.Write("打开工程失败", ex); return false; }
    }

    /// <summary>「从 Pack 打开工程」：把**源 pack 复制**到工程的"生成 Pack 目录"再编辑（**源文件不动**），
    /// 然后建工程并打开复制件。工程目录 / Pack 目录 / 项目 key 来自"新建工程"对话框。</summary>
    public async Task<bool> OpenProjectFromPackAsync(string sourcePack, string projectDir,
                                                     string? packDir = null, string? projectKey = null)
    {
        try
        {
            var src = Path.GetFullPath(sourcePack);
            if (!File.Exists(src)) { Status = "找不到源 pack：" + src; return false; }
            projectDir = Path.GetFullPath(projectDir);
            if (!await ConfirmUnsavedBeforeAsync("打开工程")) return false;
            var info = ProjectStore.Ensure(projectDir, prefillKey: projectKey ?? _settings.GroupKeyPrefix, packDir: packDir);
            if (!string.IsNullOrWhiteSpace(projectKey)) info.ProjectKey = projectKey!.Trim();
            if (!string.IsNullOrWhiteSpace(packDir)) info.PackDir = packDir!.Trim();
            var targetDir = ProjectStore.PackDirOf(projectDir, info);
            Directory.CreateDirectory(targetDir);
            var dest = Path.Combine(targetDir, Path.GetFileName(src));
            if (!Path.GetFullPath(dest).Equals(src, StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(dest))
                    FileLog.Write($"工程：目标已有 {Path.GetFileName(dest)} → 直接打开现有那份（**不覆盖**；源文件也不动）");
                else File.Copy(src, dest);
            }
            info.LastPack = dest;
            ProjectStore.Save(projectDir, info);
            _projectDir = projectDir;
            Project = info;
            _backend.ProjectKey = info.ProjectKey;
            ProjectStore.TouchRecent(_settings.RecentProjects, projectDir);
            _settings.Save();
            PruneRecentProjects();
            await OpenPackPathAsync(dest, open: true, asUnitPack: false);
            Status = $"已复制进工程并打开：{Path.GetFileName(dest)}（源文件不动，编辑都在工程里）";
            FileLog.Write($"工程：从 Pack 建立 {projectDir}（复制 {src} → {dest}，项目 key「{info.ProjectKey}」）");
            return true;
        }
        catch (Exception ex) { Status = "从 Pack 打开工程失败：" + ex.Message; FileLog.Write("从 Pack 打开工程失败", ex); return false; }
    }

    /// <summary>打开"最近工程"里的一个（欢迎面板/菜单的快捷入口）。</summary>
    public async Task<bool> OpenRecentProjectAsync(string dir)
    {
        if (!Directory.Exists(dir) || !ProjectStore.IsProject(dir))
        {
            PruneRecentProjects();
            Status = "这个工程目录已经不在了（已从最近列表移除）";
            return false;
        }
        return await OpenProjectAsync(dir);
    }

    /// <summary>关闭工程：它下面打开的包全关掉（有未导出编辑先问）。</summary>
    public async Task CloseProjectAsync()
    {
        if (_project is null) return;
        if (!await ConfirmUnsavedBeforeAsync("关闭工程")) return;
        foreach (var s in _backend.Packs.ToList()) await _backend.CloseSessionAsync(s);
        AfterPackClosed();
        Project = null;
        _projectDir = "";
        _backend.ProjectKey = "";
        RebuildTree();
        FileLog.Write("工程：已关闭");
        Status = "工程已关闭";
    }

    /// <summary>当前包的历史版本列表（工程 old/ 里的备份，新到旧）。没工程/没包时为空。</summary>
    public List<HistoryRow> HistoryOfCurrentPack()
    {
        var list = new List<HistoryRow>();
        var pack = _backend.Pack?.PackPath;
        if (pack is null || _project is null || _projectDir.Length == 0) return list;
        foreach (var f in ProjectStore.HistoryOf(_projectDir, pack))
            list.Add(new HistoryRow(f.Name, f.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
                                    $"{f.Length / 1024.0 / 1024.0:N1} MB", f.FullName));
        return list;
    }

    /// <summary>当前包所在工程的历史版本目录（对话框的「打开文件夹」用；没工程返回空）。</summary>
    public string HistoryDirOfCurrentPack()
    {
        var pack = _backend.Pack?.PackPath;
        if (pack is null || _project is null || _projectDir.Length == 0) return "";
        return ProjectStore.HistoryDirOf(_projectDir);
    }

    /// <summary>还原到某个历史版本：有未导出编辑先问；Backend 负责"先把当前包也备份一份再拷回"，
    /// 然后走正常打开链路把包重开（画布/树/清单一起刷新）。</summary>
    public async Task<bool> RestoreHistoryAsync(string backupFile)
    {
        var pack = _backend.Pack?.PackPath;
        if (pack is null) { Status = "先打开一个包"; return false; }
        if (!await ConfirmUnsavedBeforeAsync("还原历史版本")) return false;
        try
        {
            var msg = await _backend.RestoreFromBackupAsync(pack, backupFile);
            await OpenPackPathAsync(pack, open: true, asUnitPack: false);   // 重开 + 画布/树/清单全刷
            Status = msg;
            return true;
        }
        catch (Exception ex) { Status = "还原失败：" + ex.Message; FileLog.Write("还原失败", ex); return false; }
    }

    /// <summary>有未导出编辑的包数 / 编辑总处数（关窗口提醒用）。</summary>
    public (int Packs, int Edits) UnsavedEdits()
    {
        var dirty = _backend.Packs.Where(s => _backend.EditCountOf(s.PackPath) > 0).ToList();
        return (dirty.Count, dirty.Sum(s => _backend.EditCountOf(s.PackPath)));
    }

    /// <summary>保存**指定包**（写回原包；工程模式下同时留一份 old/ 历史版本）。失败返回 false。</summary>
    public async Task<bool> SavePackByPathAsync(string packPath)
    {
        var session = _backend.Packs.FirstOrDefault(x =>
            string.Equals(x.PackPath, packPath, StringComparison.OrdinalIgnoreCase));
        if (session is null) return false;
        try
        {
            _backend.SwitchTo(session);
            await _backend.SaveInPlaceAsync();
            return true;
        }
        catch (Exception ex) { Status = "保存失败：" + ex.Message; FileLog.Write("保存失败", ex); return false; }
    }

    /// <summary>把所有有编辑的包都写回原包（关窗口/关工程"保存后继续"用）。返回是否全部成功。</summary>
    public async Task<bool> SaveAllEditsAsync()
    {
        var dirty = _backend.Packs.Where(s => _backend.EditCountOf(s.PackPath) > 0).ToList();
        foreach (var s in dirty)
            if (!await SavePackByPathAsync(s.PackPath)) return false;
        RebuildTree();
        return true;
    }

    /// <summary>把外部 pack 拷进当前工程并打开（"往工程里加一个包"的正路）。</summary>
    public async Task<bool> ImportPackToProjectAsync(string externalPack)
    {
        if (_project is null || _projectDir.Length == 0) { Status = "先新建/打开一个工程"; return false; }
        try
        {
            var dest = Path.Combine(_projectDir, Path.GetFileName(externalPack));
            if (!Path.GetFullPath(externalPack).Equals(Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(dest)) { Status = $"工程里已经有 {Path.GetFileName(dest)}（要么给导入的包改个名，要么先删旧的那份）"; return false; }
                File.Copy(externalPack, dest);
            }
            await OpenPackPathAsync(dest, open: true, asUnitPack: false);
            FileLog.Write($"工程：导入包 {Path.GetFileName(dest)}");
            return true;
        }
        catch (Exception ex) { Status = "导入包失败：" + ex.Message; return false; }
    }

    /// <summary>导入内置的 WUU 模板到当前工程并打开（空工程起步用；素材在 exe 旁 bundled/wuu）。</summary>
    public async Task<bool> ImportWuuTemplateAsync()
    {
        if (_project is null || _projectDir.Length == 0) { Status = "先新建/打开一个工程"; return false; }
        var pack = _backend.BuildWuuTemplatePack(_projectDir);
        if (pack is null) { Status = "导入失败：内置 WUU 素材缺失（exe 旁 bundled\\wuu 里应有 .pack）"; return false; }
        await OpenPackPathAsync(pack, open: true, asUnitPack: false);
        FileLog.Write($"工程：导入 WUU 模板 {Path.GetFileName(pack)}");
        return true;
    }

    // ── 游戏目录 ────────────────────────────────────────────────

    /// <summary>弹"选择游戏目录"框（候选列表 + 手动 + 浏览），选了就套用。</summary>
    private async Task ChooseGameDirAsync()
    {
        if (GameDirRequested is null) return;
        var picked = await GameDirRequested(GameDir.Length > 0 ? GameDir : null);
        if (string.IsNullOrWhiteSpace(picked)) return;
        GameDir = picked!.Trim();
        await ApplyGameAsync();
    }

    private Task ApplyGameDirAsync() => ApplyGameAsync();

    private async Task ApplyGameAsync()
    {
        IsBusy = true;
        try
        {
            _settings.GameDir = GameDir;
            _settings.Save();

            if (!GameLocator.LooksLikeGame(GameDir))
            {
                Status = "这个目录里没有 data\\*.pack，确认选的是游戏根目录（含 data 的那层）";
                SchemaText = "schema：未就绪";
                return;
            }

            Status = "正在记录游戏目录…";
            var msg = await _backend.ApplyGameAsync();
            SchemaText = _backend.SchemaText;
            Status = msg;
        }
        catch (Exception e)
        {
            FileLog.Write("应用游戏目录失败", e);
            Status = "应用游戏目录失败：" + e.Message;
        }
        finally { IsBusy = false; }
    }

    // ── Pack ───────────────────────────────────────────────────

    private async Task OpenPackAsync(bool asUnitPack)
    {
        var dlg = new OpenFileDialog
        {
            Title = asUnitPack ? "加载兵种 mod（参考包：图标/中文名/兵种库；不进战帮树）"
                               : "打开战帮 .pack（或任意战锤3 pack）",
            Filter = "Pack 文件 (*.pack)|*.pack|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };
        if (!string.IsNullOrWhiteSpace(_settings.LastPack) && File.Exists(_settings.LastPack))
            dlg.InitialDirectory = Path.GetDirectoryName(_settings.LastPack);

        if (dlg.ShowDialog() != true) return;
        await OpenPackPathAsync(dlg.FileName, open: true, asUnitPack: asUnitPack);
    }

    /// <summary>左侧文件树右键「在新画布中打开」。</summary>
    public async Task OpenCanvasForNodeAsync(TreeItem item)
    {
        if (item.PackPath is { Length: > 0 } p) await OpenCanvasForPackAsync(p);
        else Status = "这个节点不属于任何已打开的包";
    }

    /// <summary>启动参数 --open-pack 用（多 pack 共存，不关上一个）。</summary>
    public Task OpenPackFromArgsAsync(string packPath) => OpenPackPathAsync(packPath);

    /// <summary>打开指定路径的 pack（「打开 Pack」和"启动时接着开上次的"都走这里）。</summary>
    private async Task OpenPackPathAsync(string packPath, bool open = true, bool asUnitPack = false)
    {
        IsBusy = true;
        try
        {
            Status = (asUnitPack ? "正在加载兵种包 " : "正在打开 ") + Path.GetFileName(packPath) + "…";
            if (open) await _backend.OpenPackAsync(packPath, default, displayName: null, asUnitPack: asUnitPack);
            if (!asUnitPack)
            {
                _settings.LastPack = packPath;
                _settings.Save();
            }
            if (asUnitPack)
            {
                // 兵种包：只当参考 —— 不动画布、不进战帮树；刷新 mod 清单并**选中刚加载的这个**
                RefreshMods(packPath);
                Raise(nameof(PackOpen));
                var up = _backend.Packs.FirstOrDefault(p => (p.PackPath ?? "").Equals(packPath, StringComparison.OrdinalIgnoreCase));
                Status = $"已加载兵种包 {Path.GetFileName(packPath)}（{up?.Files.Count ?? 0} 个文件）：" +
                         "图标/中文名/兵种库都能读到它；画布和战帮树不动";
                return;
            }
            RebuildTree();
            PackOpened?.Invoke();      // 画布要跟着换成这个包的数据
            LoadUiAssets();            // 左下「UI 素材库」
            RefreshPooledResources();  // 右上「成本工坊」的选择资源下拉（原版打底 + 本包覆盖）
            RefreshMods();             // 右上「mod 兵种」页的包清单 + 第一个包的兵种库
            Raise(nameof(PackOpen));
            foreach (var c in new[] { SavePackCommand, SavePackAsCommand, ClosePackCommand,
                                      OpenPackCommand, OpenUnitPackCommand }) c.RaiseCanExecute();

            var pack = _backend.Pack!;
            ActiveCenterTab = EnsureCanvasTab(pack);      // 每个包一张画布，打开就切过去
            var war = pack.Files.Count(f => PackSession.IsWarbandRelated(f.Path));
            PackText = $"Pack：{pack.Info?.FileName ?? Path.GetFileName(packPath)}";
            Status = $"已打开：{pack.Files.Count} 个文件，其中战帮相关 {war} 个";
        }
        catch (Exception e)
        {
            FileLog.Write("打开 pack 失败", e);
            Status = "打开失败：" + e.Message;
        }
        finally { IsBusy = false; }
    }

    /// <summary>这次编辑会动到的文件在树里标红（IsEdited）。</summary>
    private static void MarkEditedFiles(TreeItem item, HashSet<string> edited)
    {
        foreach (var c in item.Children)
        {
            if (!c.IsFolder && edited.Contains(c.Path.Replace(Path.DirectorySeparatorChar, '/'))) c.IsEdited = true;
            if (c.Children.Count > 0) MarkEditedFiles(c, edited);
        }
    }

    /// <summary>把"待导出"的文件在树里标出来（名字后缀「（待导出）」）。</summary>
    private static void MarkPendingFiles(TreeItem item, List<string> pending)
    {
        foreach (var c in item.Children)
        {
            if (!c.IsFolder && pending.Contains(c.Path, StringComparer.OrdinalIgnoreCase))
                c.Name += "（待导出）";
            if (c.Children.Count > 0) MarkPendingFiles(c, pending);
        }
    }

    /// <summary>递归给子树打上“属于哪个包”（右键「在新画布中打开」用）。</summary>
    private static void TagPack(TreeItem item, string packPath)
    {
        item.PackPath = packPath;
        foreach (var c in item.Children) TagPack(c, packPath);
    }

    private void RebuildTree()
    {
        TreeRoots.Clear();
        // **v1.5.0：工程根 → 包 → 文件**。有工程时包根挂在工程根下面（工程信息、导入/历史版本等右键操作都在它上面）；
        // 没工程（--open-pack 之类）退回平铺的"一个 pack 一棵树"。
        var projectRoot = _project is null ? null : new TreeItem
        {
            Name = $"工程：{_project.Name}",
            Path = "",
            IsFolder = true,
            IsExpanded = true,
            Tag = "工程",       // 右键菜单靠它区分"工程根"和"包根"
        };
        foreach (var session in _backend.Packs)
        {
            if (session.IsUnitPack) continue;      // 兵种包不进战帮树（它只出现在右上「mod 兵种」页）
            var files = session.Files.ToList();
            // **待导出**的新增文件也要出现在树里（标"（待导出）"）——不显示的话用户会以为"添加到包没生效"
            // （其实这些编辑是导出时才落盘的，v0.94 之前树里完全看不到）
            var pending = ReferenceEquals(session, _backend.Pack) ? _backend.PendingNewFiles() : [];
            foreach (var p2 in pending)
                files.Add(new WarbandStudio.Rpfm.RFileInfo { Path = p2 });
            if (files.Count == 0) continue;
            var packRoot = new TreeItem
            {
                Name = session.DisplayName,
                Path = "",
                IsFolder = true,
                IsExpanded = ReferenceEquals(session, _backend.Pack),
            };
            foreach (var node in PackTree.Build(files))
                packRoot.Children.Add(TreeItem.From(node));
            if (pending.Count > 0) MarkPendingFiles(packRoot, pending);
            if (ReferenceEquals(session, _backend.Pack))
            {
                var edited = _backend.EditedFiles();
                if (edited.Count > 0) MarkEditedFiles(packRoot, edited);
            }
            packRoot.PackPath = session.PackPath;
            foreach (var c in packRoot.Children) TagPack(c, session.PackPath);   // 子节点也记住自己属于哪个包
            if (projectRoot is not null) projectRoot.Children.Add(packRoot);
            else TreeRoots.Add(packRoot);
            FileLog.Write($"文件树：根「{packRoot.Name}」= {files.Count} 个战帮相关文件");
        }
        if (projectRoot is not null)
        {
            // 工程下暂时没包（或包都关了）也保留工程根：用户能看到"工程开着、还没放包"
            TreeRoots.Add(projectRoot);
            FileLog.Write($"文件树：工程「{_project!.Name}」下 {projectRoot.Children.Count} 个包");
        }
    }

    // ── 自动保存（RPFM 手感：改完就落盘）──
    private bool _autoSave;
    /// <summary>打开后：每次编辑停下来 ~2 秒就自动写回原包（等价于"随时保存"）。</summary>
    public bool AutoSave
    {
        get => _autoSave;
        set { if (Set(ref _autoSave, value)) { _settings.AutoSave = value; _settings.Save();
               Status = value ? "自动保存：开（编辑停下约 2 秒写回原包）" : "自动保存：关"; } }
    }
    private int _autoSaveGen;
    private async void AutoSaveSoon()
    {
        if (!AutoSave || _backend.Pack?.IsOpen != true || _backend.EditCount == 0) return;
        var gen = ++_autoSaveGen;
        try
        {
            await Task.Delay(2000);                      // 编辑密集时合并成一次
            if (gen != _autoSaveGen || !AutoSave || _backend.EditCount == 0) return;
            await SaveAsync(null);
        }
        catch (Exception ex) { FileLog.Write("自动保存失败", ex); }
    }

    /// <summary>保存：把当前编辑**写回打开的那个包**（原地写，原文件留 .bak 备份）。</summary>
    private async Task SaveAsync(string? path)
    {
        if (_backend.Pack?.IsOpen != true) { Status = "先打开一个包"; return; }
        // **保存 = 当前打开的战帮画布那个包**（多画布同时开着时不串台）：先按激活的画布页签把当前包切过去，
        // 这样"用哪份编辑集导出、写回哪个文件"都跟着这个画布走（用户口径）。
        if (ActiveCenterTab is CanvasTab ct)
        {
            var session = _backend.Packs.FirstOrDefault(x =>
                string.Equals(x.PackPath, ct.PackPath, StringComparison.OrdinalIgnoreCase));
            if (session is not null && !ReferenceEquals(session, _backend.Pack)) _backend.SwitchTo(session);
        }
        var packPath = _backend.Pack.PackPath;
        var fileName = System.IO.Path.GetFileName(packPath);
        // 工具自带的参考包：保存前明确警告（用户实测踩过：切到它之后点保存，自己的 mod 没动、"成本消失"）
        var ownWarn = fileName.Contains("WUU战帮升级", StringComparison.OrdinalIgnoreCase)
            ? Environment.NewLine + Environment.NewLine +
              "⚠ 这是工具自带的参考包（WUU战帮升级），不是你自己的 mod ——" + Environment.NewLine +
              "要改自己的包请选「否」，先点它自己的画布页签（右上「成本工坊」那行会显示当前包名）再保存。"
            : "";
        if (EditCountNeedsConfirm()
            && Confirm is not null
            && !Confirm($"把 {_backend.EditSummary} 写回 {fileName} 吗？{ownWarn}" + Environment.NewLine +
                        "（原文件会留一份 .bak 备份）"))
            return;
        IsBusy = true;
        try
        {
            Status = "正在写回原包…";
            var msg = await _backend.SaveInPlaceAsync();
            // 句柄已经换到新文件：把包重新打开，树/画布/派系/素材库全部刷新
            await _backend.OpenPackAsync(packPath);
            RebuildTree();
            PackOpened?.Invoke();
            LoadUiAssets();
            RefreshPooledResources();   // 写回后包内容变了：成本工坊清单重读（新建/改过的成本立刻反映出来）
            await LoadFactionsAsync();
            Raise(nameof(PackOpen));
            Status = msg;
            FileLog.Write("保存（写回原包）：" + msg);
        }
        catch (Exception ex)
        {
            FileLog.Write("保存失败", ex);
            Status = "保存失败：" + ex.Message;
        }
        finally { IsBusy = false; }
    }

    private bool EditCountNeedsConfirm() => _backend.EditCount > 0;

    /// <summary>「添加到当前包」填完名字 → 检查包里/待导出里是否已有同名（给界面提醒用）。</summary>
    public bool ArtTargetExists(string kind, string suffix)
        => _backend.ArtTargetExists(UiTargetOf(kind, suffix));

    /// <summary>文件树右键「删除」（待导出新增 → 撤掉；包内条目 → 导出时跳过）。</summary>
    public void RemovePackFile(TreeItem? item)
    {
        if (item is null || item.IsFolder) return;
        Status = _backend.RemovePackFile(item.Path);
        RebuildTree();
        PackOpened?.Invoke();          // **立刻重推画布**：换图/新建页签的素材列表跟着变
        LoadUiAssets();
    }

    /// <summary>文件树右键「重命名」。</summary>
    public void RenamePackFile(TreeItem? item, string newName)
    {
        if (item is null || item.IsFolder) return;
        Status = _backend.RenamePackFile(item.Path, newName);
        RebuildTree();
        PackOpened?.Invoke();
        LoadUiAssets();
    }

    /// <summary>按类型前缀 + 后缀拼出目标路径（界面提示也用同一套规则）。</summary>
    public static string UiTargetOf(string kind, string? suffix)
        => WarbandStudio.Ui.Services.Backend.UiTargetOf(kind, suffix);

    private async Task SaveAsAsync()
    {
        var dlg = new SaveFileDialog
        {
            Title = "导出为新的 .pack（原包不动）",
            Filter = "Pack 文件 (*.pack)|*.pack",
            FileName = (_backend.Pack?.Info?.FileName ?? "my_warband.pack").Replace(".pack", "_export.pack"),
        };
        if (dlg.ShowDialog() != true) return;
        await ExportToAsync(dlg.FileName);
    }

    /// <summary>导出（原生写）：精英解锁 + 原样搬运其余条目。</summary>
    public async Task ExportToAsync(string destPath)
    {
        IsBusy = true;
        try
        {
            Status = "正在导出…";
            var rep = await Task.Run(() => _backend.Export(destPath));
            Status = $"已导出：{Path.GetFileName(destPath)}（树里 {rep.UnitsInTree} 个兵，解锁 {rep.Unlocked}，" +
                     $"本来就解锁 {rep.AlreadyUnlocked}）";
            FileLog.Write("导出完成：" + Status);
        }
        catch (Exception e)
        {
            FileLog.Write("导出失败", e);
            Status = "导出失败：" + e.Message;
        }
        finally { IsBusy = false; }
    }

    /// <summary>
    /// 「关闭 Pack」= 关掉**当前选中的那个包**（画布页签），**不是所有包** ——
    /// 其它已打开的包和它们的画布原样留着（用户要的就是这个：多开时关一个别把别的也关了）。
    /// 走的是关页签那条路：有未导出的编辑会先问（导出/放弃/取消）。
    /// </summary>
    private async Task ClosePackAsync()
    {
        var tab = _activeCenterTab as CanvasTab
                  ?? CenterTabs.OfType<CanvasTab>().FirstOrDefault(t =>
                         string.Equals(t.PackPath, _backend.Pack?.PackPath, StringComparison.OrdinalIgnoreCase));
        if (tab is null) { Status = "没有打开的包"; return; }
        await CloseTabAsync(tab);
        AfterPackClosed();
    }

    /// <summary>文件树里 pack 名（树根）右键「关闭这个 pack」：只关这一个包。</summary>
    public async Task ClosePackByPathAsync(string packPath)
    {
        var tab = CenterTabs.OfType<CanvasTab>().FirstOrDefault(t =>
            string.Equals(t.PackPath, packPath, StringComparison.OrdinalIgnoreCase));
        if (tab is null) { Status = "这个包没有画布页签（可能已经关了）"; return; }
        // 先切到它的画布：后面的"现在导出/放弃"都对着这个包（不切的话另存会存到别的包上）
        if (!ReferenceEquals(_activeCenterTab, tab)) ActiveCenterTab = tab;
        await CloseTabAsync(tab);
        AfterPackClosed();
    }

    /// <summary>关掉一个包之后的收尾：清单跟着换（没了就清空）、按钮可用性重算。</summary>
    private void AfterPackClosed()
    {
        if (_backend.Pack is { IsOpen: true }) RefreshPooledResources();
        else
        {
            CostListAll.Clear();
            CostListShown.Clear();
            PooledResAll.Clear();
            PooledResShown.Clear();
            PackText = "Pack：未打开";
        }
        Raise(nameof(PackOpen));
        foreach (var c in new[] { SavePackCommand, SavePackAsCommand, ClosePackCommand }) c.RaiseCanExecute();
    }

    // ── 诊断 ───────────────────────────────────────────────────

    /// <summary>自检（--ui-selftest）⑫：页签"实际用的图"清单 + 真换一次图，看落表目标是不是那个文件。</summary>
    public (List<string> Arts, string Swap) SelfTestTabArt()
    {
        var arts = new List<string>();
        try
        {
            foreach (var t in _backend.TabKeys())
                arts.Add($"{t} → {Path.GetFileName(_backend.TabArtPathOf(t, true))} / {Path.GetFileName(_backend.TabArtPathOf(t, false))}");
        }
        catch (Exception ex) { arts.Add("(读取失败：" + ex.Message + ")"); }
        var swap = "(没做)";
        try
        {
            if (arts.Count > 0 && _backend.Pack is { IsOpen: true })
            {
                var cat = arts[0].Split(' ')[0];
                var tmp = Path.Combine(Path.GetTempPath(), "studio_selftest_bg.png");
                File.WriteAllBytes(tmp, Convert.FromBase64String(
                    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFAAH/q842iQAAAABJRU5ErkJggg=="));
                _backend.SetTabArt(cat, tmp, null);
                var hit = _backend.Edits.FileReplacements.LastOrDefault(x => x.Target.Contains("background_images_"));
                var want = _backend.TabArtPathOf(cat, true);
                swap = $"页签 {cat}：写 {Path.GetFileName(hit.Target)}" +
                       (hit.Target.Equals(want, StringComparison.OrdinalIgnoreCase) ? "（= 实际用的那张 ✓）" : $"（✗ 实际用的是 {Path.GetFileName(want)}）");
            }
        }
        catch (Exception ex) { swap = "(换图失败：" + ex.Message + ")"; }
        return (arts, swap);
    }

    /// <summary>自检（--ui-selftest）⑬：改名的"计划名"规则 —— 图跟新 key 走，撞名自动 _1/_2（不覆盖别人的图）。</summary>
    public List<string> SelfTestPlanArt()
    {
        var lines = new List<string>();
        try
        {
            foreach (var k in _backend.TabKeys().Take(2))
                lines.Add($"{k}：改名为 {k}X → {Path.GetFileName(_backend.PlannedArtPathOf(k + "X", true))}；" +
                          $"名字被占用时（拿 {k} 自己当例子）→ {Path.GetFileName(_backend.PlannedArtPathOf(k, true))}");
        }
        catch (Exception ex) { lines.Add("(读取失败：" + ex.Message + ")"); }
        return lines;
    }

    /// <summary>自检（--ui-selftest）⑭：换图预览缓存回归（来源=包内图 → 重推画布后缓存必须还是来源那张，
    /// 见 <see cref="WarbandStudio.Ui.Services.Backend.SelfTestSwapPreview"/>）。</summary>
    public string SelfTestSwapArt() => _backend.SelfTestSwapPreview();

    /// <summary>自检（--ui-selftest）⑮：新组命名规则（`<前缀>_<页签>_<兵种词…>`；见 Backend.SelfTestGroupNaming）。</summary>
    public string SelfTestGroupNaming() => _backend.SelfTestGroupNaming();

    /// <summary>自检（--ui-selftest）前置：启动 = 空状态后，⑫~⑮ 都依赖"有个打开的包" ——
    /// 现场用内置 WUU 素材在临时目录生成一个测试包并打开（已有包就直接用）。</summary>
    public async Task SelfTestPrepareAsync()
    {
        if (_backend.Pack is { IsOpen: true }) return;
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "warbandstudio_selftest");
            Directory.CreateDirectory(dir);
            var pack = _backend.BuildWuuTemplatePack(dir);
            if (pack is not null)
            {
                await _backend.OpenPackAsync(pack, default, displayName: "WUU战帮升级（自检）");
                RebuildTree();
            }
            FileLog.Write($"[selftest] 测试包：{pack ?? "(生成失败)"}");
        }
        catch (Exception ex) { FileLog.Write("[selftest] 测试包准备失败：" + ex.Message); }
    }

    /// <summary>自检（--ui-selftest）⑯：工程目录往返 —— 建临时工程 → 项目 key 落盘回读 →
    /// 历史版本备份/保留 N 份 → 清理。空状态启动后"工程这条路"通不通看它。</summary>
    public string SelfTestProjectRoundTrip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "warbandstudio_selftest_proj");
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            var info = ProjectStore.Ensure(dir);
            info.ProjectKey = "StudioTest";
            ProjectStore.Save(dir, info);
            var back = ProjectStore.Load(dir);
            var fake = Path.Combine(dir, "fake.pack");
            File.WriteAllBytes(fake, [1, 2, 3]);
            for (var i = 0; i < 3; i++) ProjectStore.BackupPack(dir, fake, keep: 2);
            var hist = ProjectStore.HistoryOf(dir, fake);
            var ok = back.ProjectKey == "StudioTest" && hist.Count == 2;
            return $"临时工程：key 回读「{back.ProjectKey}」，历史版本 keep=2 → 实剩 {hist.Count} 份 " + (ok ? "✓" : "✗");
        }
        catch (Exception ex) { return "(工程自检失败：" + ex.Message + ")"; }
        finally { try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>诊断 ───────────────────────────────────────────</summary>
    private async Task RunDiagnosticsAsync()
    {
        IsBusy = true;
        try
        {
            Status = "正在跑诊断…";
            DiagnosticsLines.Clear();
            var text = await _backend.DiagnosticsAsync();
            var flat = new List<string>();
            try
            {
                using var doc = JsonDocument.Parse(text);
                Flatten(doc.RootElement, "", flat);
            }
            catch (JsonException)
            {
                flat.AddRange(text.Split('\n').Select(l => l.TrimEnd()));
            }
            foreach (var line in flat.Where(l => l.Length > 0).Take(300)) DiagnosticsLines.Add(line);
            if (flat.Count > 300) DiagnosticsLines.Add($"…共 {flat.Count} 行，只显示了前 300 行");
            if (flat.Count == 0) DiagnosticsLines.Add("（没有输出 —— 诊断需要先设游戏目录并生成依赖缓存）");
            Status = "诊断完成";
        }
        catch (Exception e)
        {
            DiagnosticsLines.Add("诊断失败：" + e.Message);
            Status = "诊断失败：" + e.Message;
        }
        finally { IsBusy = false; }
    }

    private static void Flatten(JsonElement el, string prefix, List<string> outLines)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in el.EnumerateObject())
                    Flatten(p.Value, prefix.Length == 0 ? p.Name : prefix + "." + p.Name, outLines);
                break;
            case JsonValueKind.Array:
                var i = 0;
                foreach (var item in el.EnumerateArray())
                {
                    Flatten(item, $"{prefix}[{i}]", outLines);
                    i++;
                }
                if (i == 0) outLines.Add(prefix + " = []");
                break;
            default:
                outLines.Add(prefix + " = " + el);
                break;
        }
    }
}
