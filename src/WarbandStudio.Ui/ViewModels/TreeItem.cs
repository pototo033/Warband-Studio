using System.Collections.ObjectModel;
using WarbandStudio.Pack;

namespace WarbandStudio.Ui.ViewModels;

/// <summary>文件树的一个节点（TreeView 直接绑它）。</summary>
public sealed class TreeItem : ObservableObject
{
    private bool _isExpanded;
    private bool _isSelected;

    public required string Name { get; set; }        // set：待导出文件要在名字后面加「（待导出）」
    public required string Path { get; init; }
    public bool IsFolder { get; init; }

    private bool _isEdited;
    /// <summary>这次编辑会动到这个文件（文件树里标红，像 RPFM 的"已修改"）。</summary>
    public bool IsEdited { get => _isEdited; set { if (_isEdited == value) return; _isEdited = value; Raise(); } }

    /// <summary>这个节点属于哪个 pack（右键「在新画布中打开」用）。</summary>
    public string? PackPath { get; set; }

    /// <summary>文件类型角标（文件的 [DB]/[Text]…，文件夹为空）。</summary>
    public string Tag { get; init; } = "";

    public bool WarbandRelated { get; init; }

    public ObservableCollection<TreeItem> Children { get; } = [];

    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    /// <summary>给 UI Automation / 屏幕阅读器一个可读的名字（否则会回落到类型名）。</summary>
    public override string ToString() => Name;

    /// <summary>由 PackNode 转过来；战帮相关的文件夹默认展开（其余折叠）。</summary>
    public static TreeItem From(PackNode node)
    {
        var item = new TreeItem
        {
            Name = node.Name,
            Path = node.Path,
            IsFolder = node.IsFolder,
            Tag = node.IsFolder ? "" : "[" + (node.File?.FileType ?? "?") + "]",
            WarbandRelated = node.WarbandRelated,
            IsExpanded = node.IsFolder && node.WarbandRelated,
        };
        foreach (var c in node.Children) item.Children.Add(From(c));
        return item;
    }
}
