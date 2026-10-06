using System.Buffers.Binary;
using SharpCompress.Compressors.LZMA;

namespace WarbandStudio.Packfile;

/// <summary>
/// TW 的裸 LZMA1 解码：属性头 = 1 字节 props（lc/lp/pb 编码，通常 0x5D）+ 4 字节字典大小（通常 0x00400000）。
/// 底层用 SharpCompress 的 LZMA Decoder（MIT）——LzmaStream 的解码构造是 internal，这里的 Decoder 是公开 API。
/// </summary>
internal static class Lzma1
{
    public static byte[] Decode(byte[] compressed, int uncompressedSize, byte props, int dictSize)
    {
        var properties = new byte[5];
        properties[0] = props;
        BinaryPrimitives.WriteUInt32LittleEndian(properties.AsSpan(1), (uint)dictSize);

        var decoder = new Decoder();
        decoder.SetDecoderProperties(properties);

        using var input = new MemoryStream(compressed, writable: false);
        using var output = new MemoryStream(uncompressedSize);
        decoder.Code(input, output, compressed.Length, uncompressedSize, null);

        if (output.Length != uncompressedSize)
            throw new InvalidDataException($"LZMA1 解压长度不符：得到 {output.Length}，应为 {uncompressedSize}。");
        return output.ToArray();
    }
}
