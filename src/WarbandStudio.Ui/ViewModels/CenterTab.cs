using System.Data;

namespace WarbandStudio.Ui.ViewModels;

/// <summary>中间栏的一个页签（隐式 DataTemplate 按类型分发：画布 / 表视图）。</summary>
public abstract class CenterTab
{
    public required string Header { get; init; }

    /// <summary>给 UI Automation / 屏幕阅读器用（否则会读到类型名）。</summary>
    public override string ToString() => Header;
}

/// <summary>战帮画布页签（阶段 2 接 WebView2）。</summary>
/// <summary>战帮画布页签：**一个包一个**（PackPath 认它看的是哪个包）。</summary>
public sealed class CanvasTab : CenterTab
{
    /// <summary>这个画布看哪个包（空的 = 还没绑定）。</summary>
    public string? PackPath { get; set; }
}

/// <summary>表视图页签：一张原生解出来的 DB 表。</summary>
public sealed class TableTab : CenterTab
{
    /// <summary>DataGrid 的数据源（字符串列，只读）。</summary>
    public required DataView View { get; init; }
    public required string TableName { get; init; }
    public required string InnerPath { get; init; }
    public int RowCount { get; set; }      // 刷新时会被更新（set 由 ReloadTableTabAsync 用）
    public int ColumnCount { get; init; }

    public string Info => $"{TableName}　{RowCount} 行 × {ColumnCount} 列　（只读预览；编辑在下一步）";
}
