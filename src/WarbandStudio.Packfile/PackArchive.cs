using System.Text;

namespace WarbandStudio.Packfile;

/// <summary>
/// 原生 `.pack` 读取器（PFH5；格式规格照 rpfm_lib，MIT）。
///
/// 头部（PFH4+）：
///   4   ASCII   版本串（"PFH5"）；老 Workshop 包前面还有 8 字节 "MFH" 前缀
///   4   u32     Pack 类型（低 4 位）+ 标志位
///   4   u32     依赖包数量
///   4   u32     依赖索引字节数
///   4   u32     文件数量
///   4   u32     文件索引字节数
///   4   u32     内部时间戳
///   [20 字节扩展头，仅当 HAS_EXTENDED_HEADER]
/// 之后：依赖索引（若干 0 结尾 UTF-8 串）→ 文件索引 → 数据区。
///
/// 文件索引每条：size u32（数据区里占多少字节）→ [timestamp u32 仅当 HAS_INDEX_WITH_TIMESTAMPS]
/// → isCompressed u8 → 路径（0 结尾 UTF-8，反斜杠）。
/// 数据顺序与索引顺序一致，偏移按 StoredSize 顺次累加。
/// </summary>
public sealed class PackArchive : IDisposable
{
    private readonly FileStream _stream;
    private readonly Dictionary<string, PackEntry> _byPath;

    private PackArchive(FileStream stream, string filePath, PfhVersion version, PfhFileType type,
                        PfhFlags flags, uint timestamp, List<string> dependencies, List<PackEntry> entries)
    {
        _stream = stream;
        FilePath = filePath;
        Version = version;
        FileType = type;
        Flags = flags;
        Timestamp = timestamp;
        Dependencies = dependencies;
        Entries = entries;
        DataStart = entries.Count > 0 ? entries[0].Offset : stream.Position;
        _byPath = new Dictionary<string, PackEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries) _byPath[e.Path] = e;
    }

    public string FilePath { get; }
    public PfhVersion Version { get; }
    public PfhFileType FileType { get; }
    public PfhFlags Flags { get; }
    public uint Timestamp { get; }
    public IReadOnlyList<string> Dependencies { get; }
    public IReadOnlyList<PackEntry> Entries { get; }
    public long DataStart { get; }

    /// <summary>打开一个包（只读、按需 seek 读）。</summary>
    public static PackArchive Open(string packPath)
    {
        var stream = new FileStream(packPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                                    bufferSize: 1 << 16, FileOptions.RandomAccess);
        try
        {
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

            // 版本串：正常在第 0 字节；老 Workshop 包前面有 8 字节 "MFH" 前缀
            var first8 = reader.ReadBytes(8);
            if (first8.Length < 8) throw new InvalidDataException("文件太短，不是 pack。");

            var hasPreamble = first8[0] == (byte)'M' && first8[1] == (byte)'F' && first8[2] == (byte)'H';
            int versionOffset = hasPreamble ? 8 : 0;
            var magic = Encoding.ASCII.GetString(first8, versionOffset, 4);
            stream.Position = versionOffset + 4;

            if (!magic.StartsWith("PFH", StringComparison.Ordinal))
                throw new InvalidDataException($"不认识的头部：\"{magic}\"（不是 .pack 文件？）");

            var version = magic switch
            {
                "PFH5" => PfhVersion.Pfh5,
                "PFH6" => PfhVersion.Pfh6,
                "PFH4" => PfhVersion.Pfh4,
                "PFH3" => PfhVersion.Pfh3,
                "PFH2" => PfhVersion.Pfh2,
                "PFH0" => PfhVersion.Pfh0,
                _ => throw new InvalidDataException($"不支持的 PFH 版本：\"{magic}\""),
            };
            if (version is not (PfhVersion.Pfh5 or PfhVersion.Pfh6))
                throw new NotSupportedException($"目前只实现了 PFH5/PFH6 的读取（这个包是 {magic}）。");

            var rawType = reader.ReadUInt32();
            var fileType = (PfhFileType)(rawType & 0xF);
            var flags = (PfhFlags)(rawType & ~0xFu);

            var packsCount = reader.ReadUInt32();
            var packsIndexSize = reader.ReadUInt32();
            var filesCount = reader.ReadUInt32();
            var filesIndexSize = reader.ReadUInt32();
            var timestamp = reader.ReadUInt32();

            if (flags.HasFlag(PfhFlags.HasExtendedHeader)) stream.Position += 20;
            if (flags.HasFlag(PfhFlags.HasEncryptedIndex) || flags.HasFlag(PfhFlags.HasEncryptedData))
                throw new NotSupportedException("这个包是加密的（老游戏/Arena），原生读取暂不支持。");

            // 依赖索引
            var dependencies = new List<string>((int)packsCount);
            var packsIndexStart = stream.Position;
            for (var i = 0; i < packsCount; i++) dependencies.Add(ReadNullTerminated(reader));
            var packsRead = stream.Position - packsIndexStart;
            if (packsRead != packsIndexSize) stream.Position = packsIndexStart + packsIndexSize;

            // 文件索引
            var entries = new List<PackEntry>((int)filesCount);
            var filesIndexStart = stream.Position;
            long offset = filesIndexStart + filesIndexSize;   // 数据区起点
            for (var i = 0; i < filesCount; i++)
            {
                var size = reader.ReadUInt32();
                var ts = flags.HasFlag(PfhFlags.HasIndexWithTimestamps) ? reader.ReadUInt32() : 0u;
                var compressed = reader.ReadByte() != 0;
                var path = ReadNullTerminated(reader).Replace('\\', '/');
                entries.Add(new PackEntry
                {
                    Path = path,
                    StoredSize = size,
                    IsCompressed = compressed,
                    Offset = offset,
                    Timestamp = ts,
                });
                offset += size;
            }
            var filesRead = stream.Position - filesIndexStart;
            if (filesRead != filesIndexSize) stream.Position = filesIndexStart + filesIndexSize;

            return new PackArchive(stream, packPath, version, fileType, flags, timestamp, dependencies, entries);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public PackEntry? Find(string path) =>
        _byPath.TryGetValue(path.Replace('\\', '/'), out var e) ? e : null;

    /// <summary>给界面的文件列表：去掉 RPFM 的内部保留文件（和 RPFM 行为一致）。</summary>
    public IEnumerable<PackEntry> VisibleEntries => Entries.Where(e => !e.IsReserved);

    /// <summary>读一条的**原始**字节（压缩的就是压缩态）。</summary>
    public byte[] ReadRaw(PackEntry entry)
    {
        _stream.Position = entry.Offset;
        var buf = new byte[entry.StoredSize];
        var read = 0;
        while (read < buf.Length)
        {
            var n = _stream.Read(buf, read, buf.Length - read);
            if (n <= 0) throw new EndOfStreamException($"读 {entry.Path} 时数据提前结束。");
            read += n;
        }
        return buf;
    }

    /// <summary>读一条并解压（没压缩就是原样）。</summary>
    public byte[] ReadDecoded(PackEntry entry)
    {
        var raw = ReadRaw(entry);
        return entry.IsCompressed ? PackDecompressor.Decompress(raw) : raw;
    }

    public byte[] ReadDecoded(string path) =>
        ReadDecoded(Find(path) ?? throw new FileNotFoundException($"包里没有：{path}"));

    private static string ReadNullTerminated(BinaryReader reader)
    {
        var bytes = new List<byte>(64);
        int b;
        while ((b = reader.ReadByte()) > 0) bytes.Add((byte)b);
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    public void Dispose() => _stream.Dispose();
}
