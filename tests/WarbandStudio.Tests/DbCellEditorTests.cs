using WarbandStudio.Packfile;

namespace WarbandStudio.Tests;

public sealed class DbCellEditorTests
{
    [Fact]
    public void OnlyEditedFieldChangesAndNumericBoundsAreChecked()
    {
        var definition = new SchemaDefinition { TableName = "test_tables", Version = 1 };
        definition.Fields.AddRange([
            new() { Name = "number", Type = SchemaFieldType.I16 },
            new() { Name = "precise", Type = SchemaFieldType.F64 },
            new() { Name = "optional", Type = SchemaFieldType.OptionalI32 },
            new() { Name = "literal", Type = SchemaFieldType.StringU8 },
        ]);
        var table = new DbTable { TableName = definition.TableName, Version = 1, Definition = definition,
            Columns = Schema.ProcessedColumns(definition), Rows = [], RawRows = [[DbValue.Of(1L), DbValue.Of(1.123456789), new(DbValueKind.Int, present:false), DbValue.OfStr("\\n")]] };
        DbCellEditor.Set(table, 0, 0, "32767");
        Assert.Equal(1.123456789, table.RawRows[0][1].Float); Assert.False(table.RawRows[0][2].Present);
        Assert.Throws<OverflowException>(() => DbCellEditor.Set(table, 0, 0, "32768"));
        DbCellEditor.Set(table, 0, 2, "0"); Assert.True(table.RawRows[0][2].Present);
        DbCellEditor.Set(table, 0, 2, "<null>"); Assert.False(table.RawRows[0][2].Present);
        DbCellEditor.Set(table, 0, 3, "\\n"); Assert.Equal("\\n", table.RawRows[0][3].RawStr);
        Assert.Throws<OverflowException>(() => DbCellEditor.Set(table, 0, 1, "NaN"));
    }
    [Fact]
    public void DisplayBitAndEnumMapBackToTheirRawValue()
    {
        var flags = new SchemaField { Name = "flags", Type = SchemaFieldType.I32, IsBitwise = 3 };
        var mode = new SchemaField { Name = "mode", Type = SchemaFieldType.I32 }; mode.EnumValues[7] = "seven";
        var definition = new SchemaDefinition { TableName = "test_tables", Version = 1 }; definition.Fields.AddRange([flags, mode]);
        var table = new DbTable { TableName = definition.TableName, Version = 1, Definition = definition,
            Columns = Schema.ProcessedColumns(definition), Rows = [], RawRows = [[DbValue.Of(1L), DbValue.Of(0L)]] };
        DbCellEditor.Set(table, 0, 2, "true"); Assert.Equal(5, table.RawRows[0][0].Int);
        DbCellEditor.Set(table, 0, 0, "false"); Assert.Equal(4, table.RawRows[0][0].Int);
        DbCellEditor.Set(table, 0, 3, "seven"); Assert.Equal(7, table.RawRows[0][1].Int);
        Assert.Throws<FormatException>(() => DbCellEditor.Set(table, 0, 0, "maybe"));
    }
}
