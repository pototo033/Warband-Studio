using System.Text;

namespace WarbandStudio.Packfile;

/// <summary>
/// 写 `.pack`（PFH5）。格式照 rpfm_lib 的 write_pfh5：
///
///   索引按<b>小写路径</b>排序 → 头部（magic + 类型|标志 + 包索引数/长度 + 文件数/长度 + 时间戳）
///   → 依赖索引（0 结尾 UTF-8）→ 文件索引（size u32 / [timestamp u32] / isCompressed u8 / 0 结尾路径）
///   → 数据区（顺序与索引一致，偏移按 StoredSize 顺次累加）。
///
/// 未改动的条目**原样搬运存储字节**（压缩态连同 is_compressed 位一起保留），
/// 所以重写一个包不会把里面已压缩的文件解压、也不会动它们的字节。
/// </summary>
public static class PackWriter
{
    /// <summary>
    /// 以 <paramref name="source"/> 为底写一个新包；<paramref name="replacements"/> 里的路径用新数据替换
    /// （新路径 = 新增条目，写成未压缩）。<paramref name="dropReserved"/> 为真时不带 RPFM 的内部文件。
    /// </summary>
    public static void WriteFrom(PackArchive source, IReadOnlyDictionary<string, byte[]> replacements,
                                 string destPath, bool dropReserved = false, bool? compressed = null,
                                 IReadOnlyCollection<string>? dropPaths = null)
    {
        var items = new List<Item>();
        var replaced = new HashSet<string>(replacements.Keys, StringComparer.OrdinalIgnoreCase);
        var dropped = dropPaths is null || dropPaths.Count == 0
            ? null : new HashSet<string>(dropPaths, StringComparer.OrdinalIgnoreCase);

        foreach (var e in source.Entries)
        {
            if (dropReserved && e.IsReserved) continue;
            if (dropped is not null && dropped.Contains(e.Path)) continue;   // 用户在文件树里删掉的条目
            if (replaced.Contains(e.Path))
            {
                items.Add(new Item(e.Path, replacements[e.Path], compressed ?? false, e.Timestamp));
            }
            else
            {
                items.Add(new Item(e.Path, source.ReadRaw(e), e.IsCompressed, e.Timestamp));
            }
        }
        foreach (var kv in replacements)
        {
            if (source.Find(kv.Key) is null)
                items.Add(new Item(kv.Key, kv.Value, compressed ?? false, 0));
        }

        Write(destPath, source.FileType, source.Flags, items, source.Dependencies);
    }

    private readonly record struct Item(string Path, byte[] Data, bool Compressed, uint Timestamp);

    /// <summary>写一个全新的包（不含 RPFM 内部文件）。</summary>
    public static void WriteNew(string destPath, IEnumerable<(string Path, byte[] Data)> entries,
                                PfhFileType type = PfhFileType.Mod, PfhFlags flags = PfhFlags.None,
                                IReadOnlyList<string>? dependencies = null)
    {
        var items = entries.Select(e => new Item(e.Path, e.Data, false, 0)).ToList();
        Write(destPath, type, flags, items, dependencies ?? []);
    }

    private static void Write(string destPath, PfhFileType type, PfhFlags flags,
                              List<Item> items, IReadOnlyList<string> dependencies)
    {
        // RPFM 按小写路径排序（反斜杠形式）
        var sorted = items
            .OrderBy(i => i.Path.Replace('/', '\\').ToLowerInvariant(), StringComparer.Ordinal)
            .ToList();

        var withTimestamps = flags.HasFlag(PfhFlags.HasIndexWithTimestamps);

        // 依赖索引
        using var depsMs = new MemoryStream();
        foreach (var d in dependencies) WriteNullTerminated(depsMs, d);

        // 文件索引
        using var indexMs = new MemoryStream();
        using (var iw = new BinaryWriter(indexMs, Encoding.UTF8, leaveOpen: true))
        {
            foreach (var it in sorted)
            {
                iw.Write((uint)it.Data.Length);
                if (withTimestamps) iw.Write(it.Timestamp);
                iw.Write((byte)(it.Compressed ? 1 : 0));
                iw.Write(Encoding.UTF8.GetBytes(it.Path.Replace('/', '\\')));
                iw.Write((byte)0);
            }
        }

        var dir = Path.GetDirectoryName(Path.GetFullPath(destPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: true);
        w.Write(Encoding.ASCII.GetBytes("PFH5"));
        w.Write((uint)type | (uint)flags);
        w.Write((uint)dependencies.Count);
        w.Write((uint)depsMs.Length);
        w.Write((uint)sorted.Count);
        w.Write((uint)indexMs.Length);
        w.Write((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        w.Write(depsMs.ToArray());
        w.Write(indexMs.ToArray());
        foreach (var it in sorted) w.Write(it.Data);
        w.Flush();
    }

    private static void WriteNullTerminated(Stream s, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        s.Write(bytes, 0, bytes.Length);
        s.WriteByte(0);
    }
}
