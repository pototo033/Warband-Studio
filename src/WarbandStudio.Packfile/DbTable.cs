using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace WarbandStudio.Packfile;

public enum DbValueKind { Bool, Int, Float, Str, Colour, Bytes }

/// <summary>一个单元格（解码后的值）。</summary>
public readonly struct DbValue(DbValueKind kind, bool b = false, long i = 0, double f = 0, string? s = null, byte[]? raw = null, bool present = true, string? rawStr = null)
{
    public readonly DbValueKind Kind = kind;
    public readonly bool Bool = b;
    public readonly long Int = i;
    public readonly double Float = f;
    public readonly string? Str = s;
    public readonly byte[]? Raw = raw;

    /// <summary>可选字段的"存在位"（false = 原文件里那一位是 0）。写回时要原样还原。</summary>
    public readonly bool Present = present;

    /// <summary>
    /// 字符串的**未转义**原文：显示用的 <see cref="Str"/> 会把换行/制表符转成字面两字符，
    /// 但"字面反斜杠+n"和"换行"转义后长得一样、转义不可逆 —— 写回必须用这个原文。
    /// </summary>
    public readonly string? RawStr = rawStr;

    /// <summary>
    /// 按 Kind 取数值（Int / Float / Bool 都能读）。
    /// **别直接用 <see cref="Float"/>** —— I32/I16/I64 列解出来是 <see cref="Int"/> 载荷，Float 恒为 0
    /// （实测：成本金额一列全读成 0，就是因为 treasury_cost 是 I32）。
    /// </summary>
    public double Num => Kind switch
    {
        DbValueKind.Int => Int,
        DbValueKind.Float => Float,
        DbValueKind.Bool => Bool ? 1 : 0,
        _ => double.TryParse(ToTsv(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0,
    };

    public static DbValue Of(bool v) => new(DbValueKind.Bool, b: v);
    public static DbValue Of(long v) => new(DbValueKind.Int, i: v);
    public static DbValue Of(double v) => new(DbValueKind.Float, f: v);
    public static DbValue OfStr(string v) => new(DbValueKind.Str, s: v);
    public static DbValue OfColour(string v) => new(DbValueKind.Colour, s: v);
    public static DbValue OfBytes(byte[] v) => new(DbValueKind.Bytes, raw: v);

    /// <summary>转成 TSV 里的文本 —— 规则照 rpfm_lib 的 DecodedData::data_to_string（浮点固定 4 位小数）。</summary>
    public string ToTsv() => Kind switch
    {
        DbValueKind.Bool => Bool ? "true" : "false",
        DbValueKind.Int => Int.ToString(CultureInfo.InvariantCulture),
        DbValueKind.Float => Float.ToString("F4", CultureInfo.InvariantCulture),
        DbValueKind.Str or DbValueKind.Colour => Str ?? "",
        DbValueKind.Bytes => Convert.ToBase64String(Raw ?? []),
        _ => "",
    };

    public override string ToString() => ToTsv();
}

/// <summary>
/// 一张 DB 表（解码结果）。解码规则照 rpfm_lib 的 TableInMemory::decode + decode_field/_postprocess：
///   · 头部：可选 GUID、可选版本、mysterious_byte、行数
///   · 逐行逐字段按类型读；随后做三类后处理：位展开 / 枚举换名 / 颜色分列合并（合并值追加在行尾）
///   · version 0 的表要逐个候选定义试解（必须正好读完整个文件）
/// </summary>
public sealed class DbTable
{
    private static readonly byte[] GuidMarker = [0xFD, 0xFE, 0xFC, 0xFF];
    private static readonly byte[] VersionMarker = [0xFC, 0xFD, 0xFE, 0xFF];

    public required string TableName { get; init; }
    public required int Version { get; init; }
    public string Guid { get; init; } = "";
    public bool MysteriousByte { get; init; }
    public required List<ProcessedColumn> Columns { get; init; }

    /// <summary>按<b>显示顺序</b>（键列提前、颜色合并）的行 —— 给界面/TSV 用。</summary>
    public required List<DbValue[]> Rows { get; init; }

    /// <summary>按<b>定义顺序</b>、后处理之前的原始字段值 —— 写回编码用。</summary>
    public required List<DbValue[]> RawRows { get; init; }

    /// <summary>解码用的定义（编码时要按它的字段顺序/类型写回）。</summary>
    public required SchemaDefinition Definition { get; init; }

    public static DbTable Decode(byte[] data, string tableName, Schema schema)
    {
        var defs = schema.DefinitionsFor(tableName);
        if (defs.Count == 0) throw new InvalidDataException($"schema 里没有表 {tableName} 的定义。");

        // 头部：可选 GUID / 可选版本 / mysterious_byte / 行数
        var head = new BinReader(data);
        var guid = "";
        if (head.PeekBytes(GuidMarker)) { head.Skip(4); guid = head.ReadSizedStringU16(); }
        var version = 0;
        if (head.PeekBytes(VersionMarker)) { head.Skip(4); version = head.ReadI32(); }
        var mysterious = head.ReadU8() != 0;
        var rowCount = head.ReadU32();
        var dataStart = head.Position;

        SchemaDefinition def;
        List<DbValue[]> rows;
        List<DbValue[]> rawRows = [];

        if (version != 0)
        {
            def = defs.FirstOrDefault(d => d.Version == version)
                  ?? throw new InvalidDataException($"表 {tableName} 没有版本 {version} 的定义。");
            (rows, rawRows) = DecodeFrom(data, dataStart, def, rowCount);
        }
        else
        {
            // version 0：定义里没有版本号，逐个候选试，必须正好把文件读完
            SchemaDefinition? found = null;
            List<DbValue[]>? foundRows = null;
            List<DbValue[]>? foundRaw = null;
            foreach (var candidate in defs.Where(d => d.Version < 1))
            {
                try
                {
                    var probe = new BinReader(data) { Position = dataStart };
                    var probeRows = DecodeRows(probe, candidate, rowCount);
                    if (probe.Position == data.Length) { found = candidate; foundRows = probeRows.Display; foundRaw = probeRows.Raw; break; }
                }
                catch { /* 换个定义再试 */ }
            }
            def = found ?? throw new InvalidDataException($"表 {tableName} 的 version 0 定义全部解不开。");
            rows = foundRows!;
            rawRows = foundRaw!;
        }

        var (columns, order) = OrderColumns(Schema.ProcessedColumns(def));
        return new DbTable
        {
            TableName = tableName,
            Version = version,
            Guid = guid,
            MysteriousByte = mysterious,
            Columns = columns,
            Rows = ApplyOrder(rows, order),
            RawRows = rawRows,
            Definition = def,
        };
    }

    /// <summary>
    /// 列顺序：**键列提前**（= RPFM 导出 TSV 时的 keys_first），其余列保持定义顺序，
    /// 颜色合并列本来就在尾部。返回列的最终顺序和取值用的下标排列。
    /// </summary>
    private static (List<ProcessedColumn> Columns, int[] Order) OrderColumns(List<ProcessedColumn> cols)
    {
        var keys = new List<int>();
        var rest = new List<int>();
        for (var i = 0; i < cols.Count; i++)
            (cols[i].Source?.IsKey == true ? keys : rest).Add(i);
        if (keys.Count == 0) return (cols, Enumerable.Range(0, cols.Count).ToArray());
        var order = keys.Concat(rest).ToArray();
        return ([.. order.Select(i => cols[i])], order);
    }

    private static List<DbValue[]> ApplyOrder(List<DbValue[]> rows, int[] order)
    {
        var allIdentity = order.Select((v, i) => v == i).All(x => x);
        if (allIdentity) return rows;
        var result = new List<DbValue[]>(rows.Count);
        foreach (var row in rows)
        {
            var reordered = new DbValue[row.Length];
            for (var i = 0; i < order.Length; i++) reordered[i] = row[order[i]];
            result.Add(reordered);
        }
        return result;
    }

    private static (List<DbValue[]> Display, List<DbValue[]> Raw) DecodeFrom(byte[] data, int start, SchemaDefinition def, uint rowCount)
    {
        var r = new BinReader(data) { Position = start };
        var rows = DecodeRows(r, def, rowCount);
        if (r.Position != data.Length)
            throw new InvalidDataException(
                $"表 {def.TableName} 解码后有剩余字节（位置 {r.Position} / 共 {data.Length}）—— 定义不匹配或文件损坏。");
        return rows;
    }

    private static (List<DbValue[]> Display, List<DbValue[]> Raw) DecodeRows(BinReader r, SchemaDefinition def, uint rowCount)
    {
        var display = new List<DbValue[]>((int)Math.Min(rowCount, 1_000_000));
        var raw = new List<DbValue[]>(display.Capacity);
        for (var row = 0u; row < rowCount; row++)
        {
            var (d, w) = DecodeRow(r, def.Fields);
            display.Add(d);
            raw.Add(w);
        }
        return (display, raw);
    }

    private static (DbValue[] Display, DbValue[] Raw) DecodeRow(BinReader r, List<SchemaField> fields)
    {
        var outRow = new List<DbValue>(fields.Count);
        var rawRow = new DbValue[fields.Count];
        var colourGroups = new SortedDictionary<int, Dictionary<string, byte>>();

        for (var fieldIndex = 0; fieldIndex < fields.Count; fieldIndex++)
        {
            var f = fields[fieldIndex];
            var v = DecodeField(r, f);
            rawRow[fieldIndex] = v;

            if (f.IsBitwise > 1 && f.Type is SchemaFieldType.I16 or SchemaFieldType.I32 or SchemaFieldType.I64)
            {
                var raw = v.Int;
                for (var bit = 0; bit < f.IsBitwise; bit++)
                    outRow.Add(DbValue.Of((raw & (1L << bit)) != 0));
            }
            else if (f.EnumValues.Count > 0 && f.Type is SchemaFieldType.I16 or SchemaFieldType.I32 or SchemaFieldType.I64)
            {
                outRow.Add(DbValue.OfStr(f.EnumValues.TryGetValue((int)v.Int, out var name)
                    ? name
                    : v.Int.ToString(CultureInfo.InvariantCulture)));
            }
            else if (f.IsPartOfColour is { } colourIndex && v.Kind is DbValueKind.Int or DbValueKind.Float)
            {
                var channel = ChannelOf(f.Name);
                if (!colourGroups.TryGetValue(colourIndex, out var pack))
                    colourGroups[colourIndex] = pack = [];
                pack[channel] = (byte)(v.Kind == DbValueKind.Int ? v.Int : (long)v.Float);
            }
            else
            {
                outRow.Add(v);
            }
        }

        // 颜色合并：按颜色组序号升序，通道顺序固定 r|red, g|green, b|blue，各两位大写十六进制
        foreach (var pack in colourGroups.Values)
        {
            var hex = new StringBuilder(6);
            foreach (var key in new[] { "r", "red", "g", "green", "b", "blue" })
                if (pack.TryGetValue(key, out var c)) hex.Append(c.ToString("X2", CultureInfo.InvariantCulture));
            outRow.Add(DbValue.OfColour(hex.ToString()));
        }
        return ([.. outRow], rawRow);
    }

    private static string ChannelOf(string fieldName)
    {
        var i = fieldName.LastIndexOf('_');
        return (i >= 0 ? fieldName[(i + 1)..] : fieldName).ToLowerInvariant();
    }

    private static DbValue DecodeField(BinReader r, SchemaField f) => f.Type switch
    {
        SchemaFieldType.Boolean => DbValue.Of(r.ReadU8() != 0),
        SchemaFieldType.F32 => DbValue.Of((double)r.ReadF32()),
        SchemaFieldType.F64 => DbValue.Of(r.ReadF64()),
        SchemaFieldType.I16 => DbValue.Of(r.ReadI16()),
        SchemaFieldType.I32 => DbValue.Of(r.ReadI32()),
        SchemaFieldType.I64 => DbValue.Of(r.ReadI64()),
        SchemaFieldType.ColourRGB => DbValue.OfColour(r.ReadColourRgb()),
        SchemaFieldType.StringU8 => DecodeString(r.ReadSizedStringU8()),
        SchemaFieldType.StringU16 => DecodeString(r.ReadSizedStringU16()),
        SchemaFieldType.OptionalStringU8 => DecodeOptionalString(r, false),
        SchemaFieldType.OptionalStringU16 => DecodeOptionalString(r, true),
        SchemaFieldType.OptionalI16 => DecodeOptionalInt(r, 2),
        SchemaFieldType.OptionalI32 => DecodeOptionalInt(r, 4),
        SchemaFieldType.OptionalI64 => DecodeOptionalInt(r, 8),
        _ => throw new NotSupportedException($"字段 {f.Name} 的类型 {f.Type} 暂不支持（WH3 的 schema 里没有这种）。"),
    };

    private static DbValue DecodeString(string raw) =>
        new(DbValueKind.Str, s: Escape(raw), rawStr: raw);

    private static DbValue DecodeOptionalString(BinReader r, bool u16)
    {
        var present = r.ReadU8() != 0;
        var raw = present ? (u16 ? r.ReadSizedStringU16() : r.ReadSizedStringU8()) : "";
        return new DbValue(DbValueKind.Str, s: Escape(raw), present: present, rawStr: raw);
    }

    private static DbValue DecodeOptionalInt(BinReader r, int size)
    {
        var present = r.ReadU8() != 0;
        var v = present ? size switch { 2 => (long)r.ReadI16(), 4 => r.ReadI32(), _ => r.ReadI64() } : 0L;
        return new DbValue(DbValueKind.Int, i: v, present: present);
    }

    /// <summary>换行/制表符转义（照 escape_special_chars：\n → \\n、\t → \\t）。</summary>
    private static string Escape(string s) =>
        !s.Contains('\n') && !s.Contains('\t')
            ? s
            : s.Replace("\n", "\\n").Replace("\t", "\\t");

    /// <summary>
    /// 导出成 TSV —— 格式与 rpfm_cli 的导出一致：
    /// 第 1 行列名、第 2 行 `#表名;版本;包内路径` + (列数-1) 个空列、之后数据行；行尾 LF。
    /// </summary>
    public string ToTsv(string tablePath)
    {
        var sb = new StringBuilder(1 << 16);
        sb.Append(string.Join('\t', Columns.Select(c => Csv(c.Name)))).Append('\n');
        sb.Append('#').Append(TableName).Append(';').Append(Version).Append(';').Append(tablePath);
        for (var i = 1; i < Columns.Count; i++) sb.Append('\t');
        sb.Append('\n');
        foreach (var row in Rows)
        {
            for (var i = 0; i < row.Length; i++)
            {
                if (i > 0) sb.Append('\t');
                sb.Append(Csv(row[i].ToTsv()));
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// 单元格原样写出：**不做 CSV 引号转义** —— RPFM 就是这么写的
    /// （值里的 \n / \t 在解码时已经转成字面 "\n" / "\t" 两个字符，所以不会破坏分列）。
    /// </summary>
    private static string Csv(string s) => s;
}

/// <summary>DB 表的小端二进制读取器。</summary>
internal sealed class BinReader(byte[] data)
{
    private readonly byte[] _d = data;
    public int Position { get; set; }

    public bool PeekBytes(byte[] marker)
    {
        if (Position + marker.Length > _d.Length) return false;
        return _d.AsSpan(Position, marker.Length).SequenceEqual(marker);
    }
    public void Skip(int n) => Position += n;
    public byte ReadU8() => _d[Position++];
    public bool ReadBool() => ReadU8() != 0;
    public short ReadI16() { var v = BinaryPrimitives.ReadInt16LittleEndian(_d.AsSpan(Position)); Position += 2; return v; }
    public int ReadI32() { var v = BinaryPrimitives.ReadInt32LittleEndian(_d.AsSpan(Position)); Position += 4; return v; }
    public long ReadI64() { var v = BinaryPrimitives.ReadInt64LittleEndian(_d.AsSpan(Position)); Position += 8; return v; }
    public uint ReadU32() { var v = BinaryPrimitives.ReadUInt32LittleEndian(_d.AsSpan(Position)); Position += 4; return v; }
    public ushort ReadU16() { var v = BinaryPrimitives.ReadUInt16LittleEndian(_d.AsSpan(Position)); Position += 2; return v; }
    public float ReadF32() { var v = BinaryPrimitives.ReadSingleLittleEndian(_d.AsSpan(Position)); Position += 4; return v; }
    public double ReadF64() { var v = BinaryPrimitives.ReadDoubleLittleEndian(_d.AsSpan(Position)); Position += 8; return v; }

    public string ReadSizedStringU8()
    {
        var len = ReadU16();
        var s = Encoding.UTF8.GetString(_d, Position, len);
        Position += len;
        return s;
    }

    public string ReadSizedStringU16()
    {
        var chars = ReadU16();
        var bytes = chars * 2;
        var s = Encoding.Unicode.GetString(_d, Position, bytes);
        Position += bytes;
        return s;
    }

    /// <summary>四字节 B,G,R,A → "RRGGBB"（照 read_string_colour_rgb 的测试用例）。</summary>
    public string ReadColourRgb()
    {
        var b = _d[Position]; var g = _d[Position + 1]; var r = _d[Position + 2];
        Position += 4;
        return $"{r:X2}{g:X2}{b:X2}";
    }
}
