namespace WarbandStudio.Packfile;

/// <summary>PFH 版本（.pack 头部前 4 字节的 ASCII 串）。</summary>
public enum PfhVersion { Pfh0 = 0, Pfh2 = 2, Pfh3 = 3, Pfh4 = 4, Pfh5 = 5, Pfh6 = 6 }

/// <summary>Pack 类型 = 头部 u32 的低 4 位。</summary>
public enum PfhFileType { Boot = 0, Release = 1, Patch = 2, Mod = 3, Movie = 4 }

/// <summary>头部 u32 里除类型以外的标志位（数值取自 rpfm_lib/src/files/pack/mod.rs）。</summary>
[Flags]
public enum PfhFlags : uint
{
    None = 0,
    /// <summary>数据区加密（老游戏/Arena；WH3 原版包不带）。</summary>
    HasEncryptedData = 0b0000_0000_0001_0000,   // 0x0010
    /// <summary>文件索引带时间戳（PFH4+ 是 u32 截断时间戳）。</summary>
    HasIndexWithTimestamps = 0b0000_0000_0100_0000,   // 0x0040
    /// <summary>索引加密（老游戏）。</summary>
    HasEncryptedIndex = 0b0000_0000_1000_0000,   // 0x0080
    /// <summary>头部后面多 20 字节扩展头（校验用；RPFM 读的时候实际上跳过了）。</summary>
    HasExtendedHeader = 0b0000_0001_0000_0000,   // 0x0100
}

/// <summary>索引里的一条文件记录。</summary>
public sealed class PackEntry
{
    /// <summary>包内路径（已统一成正斜杠）。</summary>
    public required string Path { get; init; }

    /// <summary>数据区里这一条占的字节数（压缩过的话是压缩后的大小）。</summary>
    public required uint StoredSize { get; init; }

    /// <summary>是否压缩（PFH5 起才有这一位）。</summary>
    public required bool IsCompressed { get; init; }

    /// <summary>数据在文件里的绝对偏移。</summary>
    public required long Offset { get; init; }

    /// <summary>时间戳（索引带时间戳的标志打开时才有，否则 0）。</summary>
    public uint Timestamp { get; init; }

    /// <summary>
    /// RPFM 存在包里的内部文件（依赖缓存引用 / 备注 / 包设置），RPFM 自己的文件列表里不显示它们。
    /// </summary>
    public bool IsReserved => Path.EndsWith(".rpfm_reserved", StringComparison.OrdinalIgnoreCase);

    public override string ToString() =>
        $"{Path}  {StoredSize}B{(IsCompressed ? " 压缩" : "")}";
}
