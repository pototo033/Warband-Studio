using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace WarbandStudio.Packfile;

/// <summary>
/// 把 <see cref="DbTable"/> 写回 DB 二进制（解码的逆过程，规格照 rpfm_lib）。
/// 用 <see cref="DbTable.RawRows"/>（按定义顺序、后处理之前的原始值），所以
/// 显示用的键提前 / 颜色合并都不影响写回。
/// </summary>
public static class DbEncoder
{
    private static readonly byte[] GuidMarker = [0xFD, 0xFE, 0xFC, 0xFF];
    private static readonly byte[] VersionMarker = [0xFC, 0xFD, 0xFE, 0xFF];

    public static byte[] Encode(DbTable table)
    {
        using var ms = new MemoryStream(1 << 16);
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        if (table.Guid.Length > 0)
        {
            w.Write(GuidMarker);
            WriteSizedStringU16(w, table.Guid);
        }
        // 版本 0 的表**不写版本标记**（RPFM 的编码器就是这样；写了会多 8 字节）
        if (table.Version != 0)
        {
            w.Write(VersionMarker);
            w.Write(table.Version);
        }
        w.Write((byte)(table.MysteriousByte ? 1 : 0));
        w.Write((uint)table.RawRows.Count);

        var fields = table.Definition.Fields;
        foreach (var row in table.RawRows)
        {
            for (var i = 0; i < fields.Count; i++) WriteField(w, fields[i], row[i]);
        }

        w.Flush();
        return ms.ToArray();
    }

    private static void WriteField(BinaryWriter w, SchemaField f, DbValue v) => _ = f.Type switch
    {
        SchemaFieldType.Boolean => WriteBool(w, v.Bool),
        SchemaFieldType.F32 => WriteF32(w, v.Float),
        SchemaFieldType.F64 => WriteF64(w, v.Float),
        SchemaFieldType.I16 => WriteI16(w, (short)v.Int),
        SchemaFieldType.I32 => WriteI32(w, (int)v.Int),
        SchemaFieldType.I64 => WriteI64(w, v.Int),
        SchemaFieldType.ColourRGB => WriteColourRgb(w, v.Str ?? "000000"),
        SchemaFieldType.StringU8 => WriteStrU8(w, Raw(v)),
        SchemaFieldType.StringU16 => WriteStrU16(w, Raw(v)),
        SchemaFieldType.OptionalStringU8 => WriteOptStrU8(w, v),
        SchemaFieldType.OptionalStringU16 => WriteOptStrU16(w, v),
        SchemaFieldType.OptionalI16 => WriteOptI16(w, v),
        SchemaFieldType.OptionalI32 => WriteOptI32(w, v),
        SchemaFieldType.OptionalI64 => WriteOptI64(w, v),
        _ => throw new NotSupportedException($"字段 {f.Name} 的类型 {f.Type} 暂不支持写回。"),
    };

    private static int WriteBool(BinaryWriter w, bool v) { w.Write((byte)(v ? 1 : 0)); return 0; }
    private static int WriteF32(BinaryWriter w, double v) { w.Write((float)v); return 0; }
    private static int WriteF64(BinaryWriter w, double v) { w.Write(v); return 0; }
    private static int WriteI16(BinaryWriter w, short v) { w.Write(v); return 0; }
    private static int WriteI32(BinaryWriter w, int v) { w.Write(v); return 0; }
    private static int WriteI64(BinaryWriter w, long v) { w.Write(v); return 0; }

    private static int WriteStrU8(BinaryWriter w, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        w.Write((ushort)bytes.Length);
        w.Write(bytes);
        return 0;
    }

    private static int WriteStrU16(BinaryWriter w, string s)
    {
        w.Write((ushort)s.Length);
        w.Write(Encoding.Unicode.GetBytes(s));
        return 0;
    }

    private static int WriteOptStrU8(BinaryWriter w, DbValue v)
    {
        if (!v.Present) { w.Write((byte)0); return 0; }
        w.Write((byte)1);
        return WriteStrU8(w, Raw(v));
    }

    private static int WriteOptStrU16(BinaryWriter w, DbValue v)
    {
        if (!v.Present) { w.Write((byte)0); return 0; }
        w.Write((byte)1);
        return WriteStrU16(w, Raw(v));
    }

    /// <summary>写回一律用未转义的原文（<see cref="DbValue.RawStr"/>）；没有就退回显示串。</summary>
    private static string Raw(DbValue v) => v.RawStr ?? v.Str ?? "";

    private static int WriteOptI16(BinaryWriter w, DbValue v)
    {
        w.Write((byte)(v.Present ? 1 : 0));
        if (v.Present) w.Write((short)v.Int);
        return 0;
    }

    private static int WriteOptI32(BinaryWriter w, DbValue v)
    {
        w.Write((byte)(v.Present ? 1 : 0));
        if (v.Present) w.Write((int)v.Int);
        return 0;
    }

    private static int WriteOptI64(BinaryWriter w, DbValue v)
    {
        w.Write((byte)(v.Present ? 1 : 0));
        if (v.Present) w.Write(v.Int);
        return 0;
    }

    /// <summary>"RRGGBB" → 四字节 B, G, R, A(0)。</summary>
    private static int WriteColourRgb(BinaryWriter w, string hex)
    {
        var r = byte.Parse(hex.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var g = byte.Parse(hex.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var b = byte.Parse(hex.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        w.Write(b); w.Write(g); w.Write(r); w.Write((byte)0);
        return 0;
    }

    private static void WriteSizedStringU16(BinaryWriter w, string s)
    {
        w.Write((ushort)s.Length);
        w.Write(Encoding.Unicode.GetBytes(s));
    }

    /// <summary>解码时把 \n / \t 转成了字面两字符，写回要还原。</summary>
    private static string Unescape(string s) =>
        !s.Contains('\\') ? s : s.Replace("\\n", "\n").Replace("\\t", "\t");
}
