using WarbandStudio.Pack;
using WarbandStudio.Packfile;
using WarbandStudio.Rpfm;

namespace WarbandStudio.Tests;

public sealed class WarbandReferenceIntegrityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "warband_integrity_" + Guid.NewGuid().ToString("N"));
    private readonly Schema _schema = Schema.Load(RpfmCli.LocateSchema()!);
    private const string Effect = "effect_bonus_value_unit_upgrade_to_unit_group_junctions_tables";
    private const string Tech = "unit_upgrade_to_tech_requirements_tables", Building = "unit_upgrade_to_building_level_requirements_tables";
    public WarbandReferenceIntegrityTests() => Directory.CreateDirectory(_root);
    private byte[] Table(string name, params (string Key, string Value)[][] rows)
    {
        var definition = _schema.DefinitionsFor(name).OrderByDescending(item => item.Version).First();
        var raw = rows.Select(values => definition.Fields.Select(field =>
        {
            var text = values.FirstOrDefault(value => value.Key == field.Name).Value ?? "";
            return field.Type switch
            {
                SchemaFieldType.Boolean => DbValue.Of(text == "true"),
                SchemaFieldType.StringU8 or SchemaFieldType.StringU16 or SchemaFieldType.OptionalStringU8 or SchemaFieldType.OptionalStringU16 => DbValue.OfStr(text),
                SchemaFieldType.F32 or SchemaFieldType.F64 => DbValue.Of(0d),
                _ => DbValue.Of(long.TryParse(text, out var number) ? number : 0L)
            };
        }).ToArray()).ToList();
        return DbEncoder.Encode(new DbTable { TableName = name, Version = definition.Version, Definition = definition,
            Columns = Schema.ProcessedColumns(definition), Rows = raw, RawRows = raw });
    }
    private string Fixture(bool absentGroup = false)
    {
        var files = new List<(string, byte[])>
        {
            ($"db/{TabFileNaming.Groups}/data__", Table(TabFileNaming.Groups,
                [("unit_group", absentGroup ? "X" : "A")], [("unit_group", "B")], [("unit_group", "C")])),
            ($"db/{TabFileNaming.Routes}/data__", Table(TabFileNaming.Routes,
                [("upgrade_key", "R"), ("base_unit_group", "A"), ("target_unit_group", "B")],
                [("upgrade_key", "KEEP"), ("base_unit_group", "B"), ("target_unit_group", "C")])),
            ($"db/{Effect}/data__", Table(Effect,
                [("bonus_value_id", "percentage_cost_mod"), ("effect", "e1"), ("unit_upgrade_to_unit_group", "R")],
                [("bonus_value_id", "percentage_cost_mod"), ("effect", "e2"), ("unit_upgrade_to_unit_group", "R")],
                [("bonus_value_id", "percentage_cost_mod"), ("effect", "e1"), ("unit_upgrade_to_unit_group", "KEEP")])),
            ($"db/{Tech}/data__", Table(Tech, [("technology", "T"), ("unit_upgrade", "R")], [("technology", "T"), ("unit_upgrade", "KEEP")])),
            ($"db/{Building}/data__", Table(Building, [("building_level", "B"), ("unit_upgrade", "R")], [("building_level", "B"), ("unit_upgrade", "KEEP")])),
            ("db/main_units_tables/data__", Table("main_units_tables"))
        };
        var path = Path.Combine(_root, "source.pack");
        PackWriter.WriteNew(path, files);
        return path;
    }

    [Theory]
    [InlineData("route")]
    [InlineData("group")]
    [InlineData("self_heal")]
    public void EveryRouteDeletionPathCleansEffectTechAndBuildingReferences(string action)
    {
        var source = Fixture(action == "self_heal");
        using var pack = PackArchive.Open(source);
        var edits = new WarbandEdits();
        if (action == "route") edits.RemoveRoute.Add("R");
        if (action == "group") edits.RemoveGroup.Add("A");
        var replacements = new Dictionary<string, byte[]>();
        WarbandAmender.Apply(pack, pack, edits, _schema, replacements, []);
        using var final = PackArchive.OpenView(source, replacements);
        foreach (var (table, column) in new[] { (Effect, "unit_upgrade_to_unit_group"), (Tech, "unit_upgrade"), (Building, "unit_upgrade") })
        {
            var entry = final.Find($"db/{table}/data__")!;
            var decoded = DbTable.Decode(final.ReadDecoded(entry), table, _schema);
            Assert.Equal("KEEP", Assert.Single(decoded.Rows)[decoded.Columns.FindIndex(field => field.Name == column)].ToTsv());
        }
        Assert.Empty(WarbandReferenceIntegrity.Validate(final, pack, _schema).Items);
        Assert.Equal(2, DbTable.Decode(pack.ReadDecoded(pack.Find($"db/{TabFileNaming.Routes}/data__")!), TabFileNaming.Routes, _schema).Rows.Count);
    }

    [Fact]
    public void EmptySamePathOverrideInvalidatesLowerReferencesButDistinctFileKeepsItsKeys()
    {
        var source = Fixture();
        using var vanilla = PackArchive.Open(source);
        var path = Path.Combine(_root, "mod.pack");
        PackWriter.WriteNew(path, [($"db/{TabFileNaming.Routes}/data__", Table(TabFileNaming.Routes))]);
        using (var mod = PackArchive.Open(path))
        {
            var issues = WarbandReferenceIntegrity.Validate(mod, vanilla, _schema);
            Assert.Equal(7, issues.Errors);
            Assert.Contains(issues.Items, item => item.Path == $"db/{Effect}/data__" && item.Value == "R" && item.Cells.Single().Row == 0);
        }
        var distinct = Path.Combine(_root, "distinct.pack");
        PackWriter.WriteNew(distinct, [($"db/{TabFileNaming.Routes}/other", Table(TabFileNaming.Routes))]);
        using var other = PackArchive.Open(distinct);
        Assert.Empty(WarbandReferenceIntegrity.Validate(other, vanilla, _schema).Items);
    }

    [Fact]
    public void ExternalDependencyGroupsAndRoutesAreAcceptedWithoutCopyingTheirUnits()
    {
        var source = Fixture();
        using var dependency = PackArchive.Open(source);
        var emptyPath = Path.Combine(_root, "vanilla.pack"); PackWriter.WriteNew(emptyPath, []);
        var modPath = Path.Combine(_root, "mod.pack");
        PackWriter.WriteNew(modPath, [($"db/{Effect}/author", Table(Effect,
            [("bonus_value_id", "percentage_cost_mod"), ("effect", "e1"), ("unit_upgrade_to_unit_group", "R")]))]);
        using var vanilla = PackArchive.Open(emptyPath); using var mod = PackArchive.Open(modPath);
        Assert.Empty(WarbandReferenceIntegrity.Validate(mod, vanilla, _schema, [dependency]).Items);
        Assert.NotEmpty(WarbandReferenceIntegrity.Validate(mod, vanilla, _schema).Items);
    }

    [Fact]
    public void StructuralErrorsBlockExportEvenWhenRpfmCacheReportsSuccess()
    {
        var source = Fixture();
        var target = Path.Combine(_root, "target.pack"); File.WriteAllText(target, "old target");
        var edits = new WarbandEdits();
        edits.TableReplacements[$"db/{TabFileNaming.Routes}/data__"] = Table(TabFileNaming.Routes);
        Assert.Throws<InvalidDataException>(() => WarbandExporter.Export(source, source, target, _schema, edits,
            _ => new RpfmDiagnosticReport([])));
        Assert.Equal("old target", File.ReadAllText(target));
        Assert.Empty(Directory.GetFiles(_root, "*.exporting.*"));
        Assert.NotEmpty(edits.TableReplacements);
    }

    public void Dispose() => Directory.Delete(_root, true);
}
