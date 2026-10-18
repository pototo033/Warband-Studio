using System.Globalization;
using System.Text;

namespace WarbandStudio.Packfile;

/// <summary>只更新选中的原始字段；其余浮点精度、字符串原文、可选存在位原样保留。</summary>
public static class DbCellEditor
{
    public static void Set(DbTable table, int row, int column, string text)
    {
        var col = table.Columns[column];
        var field = col.Source ?? throw new NotSupportedException("该列没有原始字段。");
        var at = table.Definition.Fields.IndexOf(field);
        var raw = table.RawRows[row];
        if (field.IsPartOfColour is { } colour)
        {
            var hex = Hex(text);
            foreach (var f in table.Definition.Fields.Where(f => f.IsPartOfColour == colour))
            {
                var channel = f.Name[(f.Name.LastIndexOf('_') + 1)..].ToLowerInvariant();
                var offset = channel is "r" or "red" ? 0 : channel is "g" or "green" ? 2 : channel is "b" or "blue" ? 4 : throw new NotSupportedException("未知颜色通道。");
                var value = byte.Parse(hex.Substring(offset, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                raw[table.Definition.Fields.IndexOf(f)] = f.Type is SchemaFieldType.F32 or SchemaFieldType.F64 ? DbValue.Of((double)value) : DbValue.Of((long)value);
            }
        }
        else if (field.IsBitwise > 1)
        {
            var bit = int.Parse(col.Name[(field.Name.Length + 1)..], CultureInfo.InvariantCulture) - 1;
            var mask = 1L << bit;
            raw[at] = DbValue.Of(Boolean(text) ? raw[at].Int | mask : raw[at].Int & ~mask);
        }
        else if (field.EnumValues.Count > 0)
        {
            var values = field.EnumValues.Where(kv => kv.Value == text).ToArray();
            raw[at] = Integer(field.Type, values.Length == 1 ? values[0].Key : long.Parse(text, CultureInfo.InvariantCulture));
        }
        else raw[at] = Parse(field.Type, text);
    }
    private static bool Boolean(string text) => text.Trim().ToLowerInvariant() switch
    { "true" or "1" => true, "false" or "0" => false, _ => throw new FormatException("布尔值请填 true / false 或 1 / 0。") };
    private static string Hex(string text)
    {
        var hex = text.Trim().TrimStart('#');
        if (hex.Length != 6 || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _)) throw new FormatException("颜色请填写六位十六进制 RRGGBB。");
        return hex;
    }
    private static DbValue Integer(SchemaFieldType type, long value)
    {
        if (type is SchemaFieldType.I16 or SchemaFieldType.OptionalI16) _ = checked((short)value);
        if (type is SchemaFieldType.I32 or SchemaFieldType.OptionalI32) _ = checked((int)value);
        return DbValue.Of(value);
    }
    private static DbValue Parse(SchemaFieldType type, string text)
    {
        var optional = type is SchemaFieldType.OptionalI16 or SchemaFieldType.OptionalI32 or SchemaFieldType.OptionalI64 or SchemaFieldType.OptionalStringU8 or SchemaFieldType.OptionalStringU16;
        if (optional && text == "<null>") return new DbValue(type is SchemaFieldType.OptionalStringU8 or SchemaFieldType.OptionalStringU16 ? DbValueKind.Str : DbValueKind.Int, present: false);
        switch (type)
        {
            case SchemaFieldType.Boolean: return DbValue.Of(Boolean(text));
            case SchemaFieldType.I16: case SchemaFieldType.I32: case SchemaFieldType.I64:
            case SchemaFieldType.OptionalI16: case SchemaFieldType.OptionalI32: case SchemaFieldType.OptionalI64:
                return Integer(type, long.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture));
            case SchemaFieldType.F32: case SchemaFieldType.F64:
                var number = double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
                if (!double.IsFinite(number) || type == SchemaFieldType.F32 && !float.IsFinite((float)number)) throw new OverflowException("浮点数超出范围。");
                return DbValue.Of(number);
            case SchemaFieldType.ColourRGB: return DbValue.OfColour(Hex(text));
            case SchemaFieldType.StringU8: case SchemaFieldType.StringU16:
            case SchemaFieldType.OptionalStringU8: case SchemaFieldType.OptionalStringU16:
                if ((type is SchemaFieldType.StringU8 or SchemaFieldType.OptionalStringU8 ? Encoding.UTF8.GetByteCount(text) : text.Length) > ushort.MaxValue) throw new OverflowException("字符串超出表格式长度限制。");
                return new DbValue(DbValueKind.Str, s: text, rawStr: text);
            default: throw new NotSupportedException("该字段类型暂不支持编辑。");
        }
    }
}
