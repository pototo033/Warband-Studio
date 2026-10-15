using System.Collections.ObjectModel;
using System.Data;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using WarbandStudio.Pack;
using WarbandStudio.Ui.Services;

namespace WarbandStudio.Ui.ViewModels;

// 成本表单独立维护，避免表单筛选与画布消息分发混在一起。
public sealed partial class MainViewModel
{
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

}
