using System.Text;

namespace WarbandStudio.Packfile;

/// <summary>
/// `.loc` 本地化表（照 RPFM 4.7.4 `rpfm_lib/src/files/loc/mod.rs` 的格式）：
///
/// 头 14 字节：u16 BOM(0xFFFE) + "LOC" + u8 0 + u32 版本(1) + u32 条数
/// 每条记录：StringU16 键 + StringU16 文本 + u8 未知
/// StringU16 = u16 字符数（不是字节数）+ 字符数×2 字节 UTF-16LE
/// </summary>
public static class LocFile
{
    private const ushort ByteOrderMark = 0xFFFE;

    public static bool LooksLikeLoc(ReadOnlySpan<byte> d) =>
        d.Length >= 14 && d[0] == 0xFF && d[1] == 0xFE && d[2] == 'L' && d[3] == 'O' && d[4] == 'C';

    /// <summary>解出一张 loc 表（键 → 文本）。格式不对返回 null。</summary>
    public static Dictionary<string, string>? Read(byte[] data)
    {
        if (!LooksLikeLoc(data)) return null;
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var p = 2;                                        // BOM
        p += 3 + 1;                                       // "LOC" + 0
        p += 4;                                           // 版本
        var count = BitConverter.ToUInt32(data, p); p += 4;
        for (var i = 0u; i < count; i++)
        {
            if (p + 2 > data.Length) break;
            var key = ReadString(data, ref p);
            if (key is null) break;
            var text = ReadString(data, ref p);
            if (text is null) break;
            if (p < data.Length) p++;                     // 那条 u8
            if (key.Length > 0) map[key] = text;
        }
        return map;
    }

    private static string? ReadString(byte[] data, ref int p)
    {
        if (p + 2 > data.Length) return null;
        var chars = BitConverter.ToUInt16(data, p); p += 2;
        var bytes = chars * 2;
        if (p + bytes > data.Length) return null;
        var s = Encoding.Unicode.GetString(data, p, bytes);   // UTF-16LE
        p += bytes;
        return s.TrimEnd('\0');                                // 少数文件把结尾 0 也算进长度
    }
}
