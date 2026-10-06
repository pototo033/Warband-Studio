namespace WarbandStudio.Ui.ViewModels;

/// <summary>
/// 右栏的种族/派系节点：种族为父节点，下面是**传奇派系**（新战役可选的那些）+ 一个「通用」（非传奇合并）。
/// 名字用 loc 里的中文名，真实 key 放 <see cref="Tip"/>（鼠标悬浮显示）。
/// </summary>
public sealed class FactionNode
{
    /// <summary>
    /// 取数据用的 key：派系 = 派系 key；种族节点 = "race:亚文化"；通用节点 = "generic:亚文化"；「全部兵」= 空。
    /// </summary>
    public string Key { get; init; } = "";

    /// <summary>显示名（中文；loc 查不到时调用方填 key）。</summary>
    public string Display { get; init; } = "";

    /// <summary>悬浮提示（真实 key + 说明）。</summary>
    public string Tip { get; init; } = "";

    public string Race { get; init; } = "";

    public int UnitCount { get; set; }

    /// <summary>「全部兵」那一条</summary>
    public bool IsAll { get; init; }

    /// <summary>真派系（有 key、能点出具体兵表）</summary>
    public bool IsFaction => !IsAll
        && !Key.StartsWith("race:", StringComparison.Ordinal)
        && !Key.StartsWith("generic:", StringComparison.Ordinal);

    /// <summary>小标签（如"通"= 属于通用军事组）。</summary>
    public string Badge { get; init; } = "";

    public string Label => IsAll
        ? $"全部兵（{UnitCount}）"
        : IsFaction && Badge.Length > 0 ? $"{Display}（{UnitCount}）·{Badge}"
        : $"{Display}（{UnitCount}）";

    public List<FactionNode> Children { get; } = [];

    public override string ToString() => Label;      // 无头验证（UIAutomation）读这个
}

/// <summary>兵种库里的一张牌：Url 给画布用（http://icons.local/…），Icon 给 WPF 用（本地文件路径）。</summary>
public sealed class UnitCard(string key, string? url, string? iconFile, string? name = null, bool inTree = false)
{
    public string Key { get; } = key;
    public string? Url { get; } = url;
    public string? Icon { get; } = iconFile;

    /// <summary>中文名（loc 里没有就退回 key）</summary>
    public string Name { get; } = string.IsNullOrWhiteSpace(name) ? key : name!;

    /// <summary>已经在画布上了（兵种库里显示成灰的，避免重复入树）。</summary>
    public bool InTree { get; } = inTree;

    public override string ToString() => Name;      // 无头验证读这个
}

/// <summary>
/// 「UI 素材库」里的一条（**本地素材**）：<see cref="File"/> 是本地文件路径（WPF 直接当图用），
/// <see cref="Kind"/> = bg/btn/other（界面按它分组），<see cref="PackPath"/> = 写进包时用的位置。
/// </summary>
public sealed class UiAssetItem(string kind, string name, string file, string url, string packPath, long size, string previewFile = "")
{
    public string Kind { get; } = kind;
    public string Name { get; } = name;
    public string File { get; } = file;
    public string Url { get; } = url;
    public string PackPath { get; } = packPath;
    public long Size { get; } = size;
    /// <summary>预览用**缓存里的副本**（不绑素材库原文件：WPF 的 Image 会把原文件锁住 → 删不掉/改不了名）。</summary>
    public string PreviewFile { get; } = previewFile.Length > 0 ? previewFile : file;
    public string GroupLabel => Services.Backend.UiKindLabel(Kind);
    public string SizeText => Size >= 1024 ? $"{Size / 1024.0:0.#} KB" : $"{Size} B";
    public string Tip => $"{Name}\n{GroupLabel}　{SizeText}\n写进包时：{PackPath}\n右键：「添加到当前包」/「复制写进包时的路径」/「从素材库移除」";
    public override string ToString() => Name;      // 无头验证读这个
}
