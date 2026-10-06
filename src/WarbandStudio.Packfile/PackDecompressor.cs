using System.Buffers.Binary;
using K4os.Compression.LZ4.Streams;

namespace WarbandStudio.Packfile;

/// <summary>
/// 解压 TW 的三种压缩（规格抄自 rpfm_lib/src/compression/mod.rs）：
///
///   三种格式都是「**u32 原始大小** + 各自的流」：
///     · LZMA1（PFH5 老格式，全系列支持）：u32 大小 + 5 字节 LZMA 属性头 + 裸流
///     · LZ4（WH3 6.2+）：u32 大小 + 以 LZ4 魔术号开头的标准帧
///     · Zstd（WH3 6.2+）：u32 大小 + 以 Zstd 魔术号开头的标准帧
///
/// 索引里只有"压缩了没有"这一位、没有格式信息，所以按魔术号嗅探；认不出就是 LZMA1（老格式占绝大多数）。
/// </summary>
public static class PackDecompressor
{
    private static readonly byte[] Lz4Magic = [0x04, 0x22, 0x4D, 0x18];
    private static readonly byte[] ZstdMagic = [0x28, 0xB5, 0x2F, 0xFD];

    public static byte[] Decompress(ReadOnlySpan<byte> data)
    {
        if (data.Length < 5) throw new InvalidDataException("压缩数据太短。");
        var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(data);
        var body = data[4..];

        if (body.Length >= 4 && body[..4].SequenceEqual(Lz4Magic)) return ReadAll(LZ4Stream.Decode(new MemoryStream(body.ToArray())), size, "LZ4");
        if (body.Length >= 4 && body[..4].SequenceEqual(ZstdMagic)) return ReadAll(new ZstdSharp.DecompressionStream(new MemoryStream(body.ToArray())), size, "Zstd");
        return Lzma1.Decode(body.ToArray(), size, body[0], (int)BinaryPrimitives.ReadUInt32LittleEndian(body[1..]));
    }

    private static byte[] ReadAll(Stream stream, int expected, string what)
    {
        using (stream)
        {
            var buf = new byte[expected];
            var read = 0;
            while (read < expected)
            {
                var n = stream.Read(buf, read, expected - read);
                if (n <= 0) break;
                read += n;
            }
            if (read != expected)
                throw new InvalidDataException($"{what} 解压长度不符：得到 {read}，应为 {expected}。");
            return buf;
        }
    }
}
