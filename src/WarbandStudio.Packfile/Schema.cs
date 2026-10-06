using System.Globalization;
using System.Text;

namespace WarbandStudio.Packfile;

/// <summary>schema 里的字段类型（rpfm_lib 的 FieldType）。WH3 实际只用到其中 11 种。</summary>
public enum SchemaFieldType
{
    Boolean, F32, F64, I16, I32, I64, ColourRGB, StringU8, StringU16,
    OptionalI16, OptionalI32, OptionalI64, OptionalStringU8, OptionalStringU16,
    SequenceU16, SequenceU32,
}

public sealed class SchemaField
{
    public required string Name { get; set; }
    public required SchemaFieldType Type { get; set; }
    public bool IsKey { get; init; }
    public int IsBitwise { get; init; }
    public int? IsPartOfColour { get; init; }

    /// <summary>这一列引用了哪张表的哪一列（schema 的 is_reference；引用体检靠它）。</summary>
    public (string Table, string Column)? IsReference { get; init; }
    public Dictionary<int, string> EnumValues { get; } = [];
}

public sealed class SchemaDefinition
{
    public required string TableName { get; init; }
    public required int Version { get; init; }
    public List<SchemaField> Fields { get; } = [];
}

/// <summary>合并后的列（TSV 表头用）：位展开 / 枚举转换 / 颜色合并之后的样子。</summary>
public sealed record ProcessedColumn(string Name, SchemaFieldType Type, SchemaField? Source);

/// <summary>
/// schema_wh3.ron（v5 格式）的解析与查询。
/// 只解析解码/导出需要的部分（name / field_type / is_key / is_bitwise / enum_values / is_part_of_colour），
/// 其余键（description / is_reference / lookup / default_value…）整块跳过 —— 那些是界面显示用的。
/// </summary>
public sealed class Schema
{
    private readonly Dictionary<string, List<SchemaDefinition>> _definitions = new(StringComparer.Ordinal);

    public int Version { get; private init; }

    /// <summary>表名 → 定义列表（同一张表可能有多个版本）。</summary>
    public IReadOnlyDictionary<string, List<SchemaDefinition>> Definitions => _definitions;

    /// <summary>按表名 + 版本取定义；version 传 0 时取第一个 version=0 的定义。</summary>
    public SchemaDefinition? GetDefinition(string tableName, int version)
    {
        if (!_definitions.TryGetValue(tableName, out var defs)) return null;
        if (version != 0) return defs.FirstOrDefault(d => d.Version == version);
        return defs.FirstOrDefault(d => d.Version == 0) ?? defs.FirstOrDefault();
    }

    /// <summary>表名下的所有定义（version 0 的表要逐个试）。</summary>
    public IReadOnlyList<SchemaDefinition> DefinitionsFor(string tableName) =>
        _definitions.TryGetValue(tableName, out var defs) ? defs : [];

    /// <summary>
    /// 合并后的列（= RPFM 的 fields_processed）：位展开成 N 个布尔、枚举换成字符串、
    /// 颜色分列合并成一个 `xxx_hex`（追加在尾部，按颜色组序号排）。
    /// </summary>
    public static List<ProcessedColumn> ProcessedColumns(SchemaDefinition def)
    {
        var columns = new List<ProcessedColumn>();
        var colourFields = new SortedDictionary<int, SchemaField>();

        foreach (var f in def.Fields)
        {
            if (f.IsBitwise > 1 && f.Type is SchemaFieldType.I16 or SchemaFieldType.I32 or SchemaFieldType.I64)
            {
                for (var i = 1; i <= f.IsBitwise; i++)
                    columns.Add(new ProcessedColumn($"{f.Name}_{i}", SchemaFieldType.Boolean, f));
            }
            else if (f.EnumValues.Count > 0 && f.Type is SchemaFieldType.I16 or SchemaFieldType.I32 or SchemaFieldType.I64)
            {
                columns.Add(new ProcessedColumn(f.Name, SchemaFieldType.StringU8, f));
            }
            else if (f.IsPartOfColour is { } colourIndex)
            {
                colourFields[colourIndex] = f;   // 同组只留一个（名字用第一个遇到的）
            }
            else
            {
                columns.Add(new ProcessedColumn(f.Name, f.Type, f));
            }
        }

        foreach (var (index, f) in colourFields)
        {
            var parts = f.Name.Split('_');
            var name = parts.Length >= 2
                ? string.Join('_', parts[..^1]).ToLowerInvariant() + "_hex"
                : $"unnamed colour group_{index}";
            columns.Add(new ProcessedColumn(name, SchemaFieldType.ColourRGB, f));
        }
        return columns;
    }

    /// <summary>读一个 schema 文件（.ron）。</summary>
    public static Schema Load(string path) => Parse(File.ReadAllText(path, Encoding.UTF8));

    public static Schema Parse(string text)
    {
        var s = new RonScanner(text);
        var schema = new Schema { Version = 0 };
        s.SkipWs();
        s.Expect('(');

        while (true)
        {
            s.SkipWs();
            if (s.Peek() == ')') { s.Next(); break; }
            var key = s.ReadKey();
            s.SkipWs(); s.Expect(':'); s.SkipWs();
            switch (key)
            {
                case "version": schema = new Schema { Version = (int)s.ReadNumber() }; break;
                case "definitions": ParseDefinitions(s, schema); break;
                default: s.SkipValue(); break;
            }
            s.SkipWs();
            if (s.Peek() == ',') s.Next();
        }
        return schema;
    }

    private static void ParseDefinitions(RonScanner s, Schema schema)
    {
        s.Expect('{');
        while (true)
        {
            s.SkipWs();
            if (s.Peek() == '}') { s.Next(); break; }
            var tableName = s.ReadKey();
            s.SkipWs(); s.Expect(':'); s.SkipWs();
            s.Expect('[');
            var list = schema._definitions.TryGetValue(tableName, out var existing) ? existing : [];
            while (true)
            {
                s.SkipWs();
                if (s.Peek() == ']') { s.Next(); break; }
                list.Add(ParseDefinition(s, tableName));
                s.SkipWs();
                if (s.Peek() == ',') s.Next();
            }
            schema._definitions[tableName] = list;
            s.SkipWs();
            if (s.Peek() == ',') s.Next();
        }
    }

    private static SchemaDefinition ParseDefinition(RonScanner s, string tableName)
    {
        s.SkipWs(); s.Expect('(');
        var version = 0;
        var fields = new List<SchemaField>();

        while (true)
        {
            s.SkipWs();
            if (s.Peek() == ')') { s.Next(); break; }
            var key = s.ReadKey();
            s.SkipWs(); s.Expect(':'); s.SkipWs();
            switch (key)
            {
                case "version": version = (int)s.ReadNumber(); break;
                case "fields":
                    s.Expect('[');
                    while (true)
                    {
                        s.SkipWs();
                        if (s.Peek() == ']') { s.Next(); break; }
                        fields.Add(ParseField(s));
                        s.SkipWs();
                        if (s.Peek() == ',') s.Next();
                    }
                    break;
                default: s.SkipValue(); break;
            }
            s.SkipWs();
            if (s.Peek() == ',') s.Next();
        }

        var def = new SchemaDefinition { TableName = tableName, Version = version };
        def.Fields.AddRange(fields);
        return def;
    }

    private static SchemaField ParseField(RonScanner s)
    {
        s.SkipWs(); s.Expect('(');
        string name = "";
        var type = SchemaFieldType.StringU8;
        var isKey = false;
        var isBitwise = 0;
        int? isPartOfColour = null;
        (string, string)? isReference = null;
        var enums = new Dictionary<int, string>();

        while (true)
        {
            s.SkipWs();
            if (s.Peek() == ')') { s.Next(); break; }
            var key = s.ReadKey();
            s.SkipWs(); s.Expect(':'); s.SkipWs();
            switch (key)
            {
                case "name": name = s.ReadString(); break;
                case "field_type":
                    type = Enum.TryParse<SchemaFieldType>(s.ReadIdent(), out var t) ? t : SchemaFieldType.StringU8;
                    break;
                case "is_key": isKey = s.ReadBool(); break;
                case "is_bitwise": isBitwise = (int)s.ReadNumber(); break;
                case "is_part_of_colour":
                    isPartOfColour = s.TryReadNone() ? null : (int)s.ReadSomeNumber();
                    break;
                case "is_reference":
                    isReference = s.TryReadNone() ? null : s.ReadSomeStringTuple();
                    break;
                case "enum_values":
                    s.Expect('{');
                    while (true)
                    {
                        s.SkipWs();
                        if (s.Peek() == '}') { s.Next(); break; }
                        var k = s.ReadKey();
                        s.SkipWs(); s.Expect(':'); s.SkipWs();
                        enums[int.Parse(k, CultureInfo.InvariantCulture)] = s.ReadString();
                        s.SkipWs();
                        if (s.Peek() == ',') s.Next();
                    }
                    break;
                default: s.SkipValue(); break;
            }
            s.SkipWs();
            if (s.Peek() == ',') s.Next();
        }

        var field = new SchemaField
        {
            Name = name, Type = type, IsKey = isKey, IsBitwise = isBitwise,
            IsPartOfColour = isPartOfColour, IsReference = isReference,
        };
        foreach (var kv in enums) field.EnumValues[kv.Key] = kv.Value;
        return field;
    }
}

/// <summary>
/// 够用的 RON 扫描器：认字符串（带转义）、标识符、数字、容器，其余整块跳过。
/// 只服务于 schema 文件这一个用途，不做通用 RON 实现。
/// </summary>
internal sealed class RonScanner(string text)
{
    private readonly string _t = text;
    private int _i;

    public char Peek() => _t[_i];
    public void Next() => _i++;
    public void SkipWs()
    {
        while (_i < _t.Length && char.IsWhiteSpace(_t[_i])) _i++;
    }
    public void Expect(char c)
    {
        SkipWs();
        if (_i >= _t.Length || _t[_i] != c)
            throw new InvalidDataException($"schema 解析：位置 {_i} 期望 '{c}'，实际 '{(_i < _t.Length ? _t[_i].ToString() : "EOF")}'。");
        _i++;
    }

    /// <summary>键：带引号的字符串或不带引号的标识符。</summary>
    public string ReadKey()
    {
        SkipWs();
        return _t[_i] == '"' ? ReadString() : ReadIdent();
    }

    public string ReadString()
    {
        SkipWs();
        Expect('"');
        var sb = new StringBuilder();
        while (true)
        {
            var c = _t[_i++];
            if (c == '"') break;
            if (c != '\\') { sb.Append(c); continue; }
            var e = _t[_i++];
            sb.Append(e switch
            {
                'n' => '\n', 't' => '\t', 'r' => '\r', '0' => '\0',
                '"' => '"', '\'' => '\'', '\\' => '\\',
                _ => e,
            });
        }
        return sb.ToString();
    }

    public string ReadIdent()
    {
        SkipWs();
        var start = _i;
        while (_i < _t.Length && (char.IsLetterOrDigit(_t[_i]) || _t[_i] == '_')) _i++;
        if (_i == start) throw new InvalidDataException($"schema 解析：位置 {_i} 期待标识符。");
        return _t[start.._i];
    }

    public double ReadNumber()
    {
        SkipWs();
        var start = _i;
        if (_i < _t.Length && (_t[_i] == '-' || _t[_i] == '+')) _i++;
        while (_i < _t.Length && (char.IsDigit(_t[_i]) || _t[_i] == '.' || _t[_i] == 'e' || _t[_i] == 'E' || _t[_i] == '-' || _t[_i] == '+')) _i++;
        return double.Parse(_t[start.._i], CultureInfo.InvariantCulture);
    }

    public bool ReadBool()
    {
        var id = ReadIdent();
        return id switch
        {
            "true" => true,
            "false" => false,
            _ => throw new InvalidDataException($"schema 解析：期待 true/false，得到 {id}。"),
        };
    }

    /// <summary>看接下来是不是 None（是就吃掉）。</summary>
    public bool TryReadNone()
    {
        SkipWs();
        if (!_t.AsSpan(_i).StartsWith("None")) return false;
        _i += 4;
        return true;
    }

    /// <summary>读 Some(值)：吃掉 "Some(" 与配对右括号，返回值。</summary>
    public double ReadSomeNumber()
    {
        SkipWs();
        var id = ReadIdent();            // Some
        if (id != "Some") throw new InvalidDataException($"schema 解析：期待 Some，得到 {id}。");
        SkipWs(); Expect('(');
        SkipWs();
        var v = ReadNumber();
        SkipWs(); Expect(')');
        return v;
    }

    /// <summary>读 Some(("表名", "列名")) 这种二元字符串组（is_reference 的格式）。</summary>
    public (string, string) ReadSomeStringTuple()
    {
        var id = ReadIdent();                     // Some
        if (id != "Some") throw new InvalidDataException("schema 解析：期待 Some，得到 " + id);
        SkipWs(); Expect('(');
        SkipWs(); Expect('(');
        var a = ReadString();
        SkipWs(); Expect(',');
        var b = ReadString();
        SkipWs(); Expect(')');
        SkipWs(); Expect(')');
        return (a, b);
    }

    /// <summary>整块跳过一个值（字符串 / 数字 / 标识符 / 容器 / Some(...) / None）。</summary>
    public void SkipValue()
    {
        SkipWs();
        if (_i >= _t.Length) return;
        switch (_t[_i])
        {
            case '"': ReadString(); return;
            case '(': case '[': case '{':
                {
                    var open = _t[_i];
                    var close = open switch { '(' => ')', '[' => ']', _ => '}' };
                    _i++;
                    var depth = 1;
                    var inStr = false;
                    while (_i < _t.Length && depth > 0)
                    {
                        var c = _t[_i++];
                        if (inStr) { if (c == '\\') _i++; else if (c == '"') inStr = false; continue; }
                        if (c == '"') inStr = true;
                        else if (c == open) depth++;
                        else if (c == close) depth--;
                    }
                    return;
                }
            default:
                if (char.IsDigit(_t[_i]) || _t[_i] == '-' || _t[_i] == '+') { ReadNumber(); return; }
                var ident = ReadIdent();
                if (ident == "Some") { SkipWs(); SkipValue(); }   // Some(x) 的括号会被上面分支吃掉
                return;
        }
    }
}
