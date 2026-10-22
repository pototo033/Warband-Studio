using System.Collections.ObjectModel;
using WarbandStudio.Ui.Services;

namespace WarbandStudio.Ui.ViewModels;

public sealed partial class MainViewModel
{
    public ObservableCollection<Backend.CostResource> CostResourceLinks { get; } = [];
    private Backend.CostResource? _selectedCostResourceLink;
    private long _costFormRevision;
    private string? _costFormPackPath;
    private bool _applyingCostLists;
    private void ResetCostForm()
    {
        _costId = ""; _costGold = ""; _costResText = ""; _costResAmount = "";
        _pickedCostId = ""; _pickedResKey = ""; _loadedPoolId = ""; _loadedResKey = ""; _loadedResAmount = 0;
        _loadedExisting = false; _addingCostResource = false; _selectedCostResourceLink = null; _costFormPackPath = null;
        CostResourceLinks.Clear(); ++_costFormRevision;
        foreach (var property in new[] { nameof(CostId), nameof(CostGold), nameof(CostResText), nameof(CostResAmount),
            nameof(CostIsExisting), nameof(CostActionText), nameof(SelectedCostResourceLink) }) Raise(property);
    }
    public Backend.CostResource? SelectedCostResourceLink
    {
        get => _selectedCostResourceLink;
        set
        {
            if (!Set(ref _selectedCostResourceLink, value) || value is null) return;
            _addingCostResource = false;
            _loadedPoolId = value.PoolId; _loadedResKey = value.Resource; _loadedResAmount = value.Amount;
            _pickedResKey = value.Resource;
            _costResText = PooledResAll.FirstOrDefault(r => r.Key.Equals(value.Resource, StringComparison.OrdinalIgnoreCase))?.Name ?? value.Resource;
            _costResAmount = value.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture);
            ++_costFormRevision;
            Raise(nameof(CostResText)); Raise(nameof(CostResAmount)); UpdateCostPreview();
        }
    }
    public RelayCommand AddCostResourceCommand => new(() => { AddCostResourceLine(); return Task.CompletedTask; });
    public RelayCommand RemoveCostResourceCommand => new(() => { RemoveSelectedCostResource(); return Task.CompletedTask; });
    private void AddCostResourceLine()
    {
        if (!_loadedExisting) { CostStatus = "先创建或选择一条成本"; return; }
        SelectedCostResourceLink = null;
        _addingCostResource = true;
        _loadedPoolId = ""; _loadedResKey = ""; _loadedResAmount = 0;
        _pickedResKey = "";
        _costResText = ""; _costResAmount = "";
        ++_costFormRevision;
        Raise(nameof(CostResText)); Raise(nameof(CostResAmount));
        CostStatus = "选择要追加的资源和数量，再点保存修改；已有资源保留";
        UpdateCostPreview();
    }
    private void RemoveSelectedCostResource()
    {
        if (!_loadedExisting || SelectedCostResourceLink is not { } link) { CostStatus = "先选中一条资源关联"; return; }
        CostStatus = CostEdit(() => _backend.RemoveWarbandCostResource(CostId, link.PoolId));
        if (!CostStatus.StartsWith("成本工坊", StringComparison.Ordinal)) return;
        CostResourceLinks.Remove(link);
        AddCostResourceLine(); // 立即进入追加状态，清理旧选中项；其余资源不被下一次选择覆盖。
        RebuildTree(); RefreshPooledResources(); PackOpened?.Invoke(); AutoSaveSoon();
    }
    private string CostEdit(Func<string> action)
    {
        try { return _backend.Edit(action); }
        catch (Exception ex) { FileLog.Write("成本编辑事务失败", ex); return "成本编辑失败：" + ex.Message; }
    }
}
