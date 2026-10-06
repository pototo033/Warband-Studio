using System.Text.Json;

namespace WarbandStudio.Rpfm;

/// <summary>pack 容器元数据（docs/server/ws-shared-types.md → ContainerInfo）。</summary>
public sealed class ContainerInfo
{
    public string FileName { get; set; } = "";
    public string FilePath { get; set; } = "";
    /// <summary>PFH 版本等枚举先原样留着（阶段 0 只显示，不解释）。</summary>
    public JsonElement PfhVersion { get; set; }
    public JsonElement PfhFileType { get; set; }
    public JsonElement Compress { get; set; }
}

/// <summary>pack 里一个文件的元数据（→ RFileInfo）。</summary>
public sealed class RFileInfo
{
    public string Path { get; set; } = "";
    public string? ContainerName { get; set; }
    public long? Timestamp { get; set; }
    /// <summary>FileType 枚举值，如 "DB" / "Loc" / "Text"。</summary>
    public string FileType { get; set; } = "";
}

/// <summary>文件类型 / 数据来源等协议枚举的字符串常量（序列化为纯字符串）。</summary>
public static class RpfmEnums
{
    // DataSource
    public const string PackFile = "PackFile";
    public const string GameFiles = "GameFiles";
    public const string ParentFiles = "ParentFiles";
    public const string AssKitFiles = "AssKitFiles";
    public const string ExternalFile = "ExternalFile";

    /// <summary>战锤 3 的游戏键（SetGameSelected 用）。</summary>
    public const string GameWarhammer3 = "warhammer_3";
}

/// <summary>引用搜索结果一条（SearchReferences 的元组）。</summary>
public sealed record ReferenceHit(
    string DataSource, string PackKey, string Path,
    string ColumnName, long ColumnNumber, long RowNumber);
