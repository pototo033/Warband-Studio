using System.Text;
using WarbandStudio.Pack;
using WarbandStudio.Packfile;
using WarbandStudio.Rpfm;

namespace WarbandStudio.Tests;

public sealed class ExportReferenceSafetyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "studio_refs_" + Guid.NewGuid().ToString("N"));
    private readonly Schema _schema = Schema.Parse("""
        (version: 5, definitions: {
          "targets_tables": [(version: 0, fields: [(name: "key", field_type: StringU8, is_key: true), (name: "alternate", field_type: StringU8, is_key: false)])],
          "sources_tables": [(version: 0, fields: [(name: "key", field_type: StringU8, is_key: true),
            (name: "target", field_type: StringU8, is_key: false, is_reference: Some(("targets", "key"))),
            (name: "other", field_type: StringU8, is_key: false, is_reference: Some(("targets", "alternate")))])]
        })
        """);
    public ExportReferenceSafetyTests() => Directory.CreateDirectory(_dir);
    private byte[] Table(string name, params string[][] rows)
    {
        var def = _schema.GetDefinition(name, 0)!;
        var raw = rows.Select(r => r.Select(DbValue.OfStr).ToArray()).ToList();
        return DbEncoder.Encode(new DbTable { TableName = name, Version = 0, Definition = def,
            Columns = Schema.ProcessedColumns(def), Rows = raw, RawRows = raw });
    }
    private (PackArchive Pack, PackArchive Vanilla) Packs(bool target = true)
    {
        var p = Path.Combine(_dir, "source.pack"); var v = Path.Combine(_dir, "db.pack");
        PackWriter.WriteNew(p, [("db/sources_tables/author", Table("sources_tables", ["S", "A", "alias"]))]);
        PackWriter.WriteNew(v, target ? [("db/targets_tables/data__", Table("targets_tables", ["A", "alias"]))] : []);
        return (PackArchive.Open(p), PackArchive.Open(v));
    }

    [Fact]
    public void FinalReplacementIsValidatedRatherThanOldSource()
    {
        var (p,v)=Packs(); using (p) using (v)
        {
            Assert.True(ExportReferenceValidator.Validate(p,v,_schema,new Dictionary<string,byte[]>()).Ok);
            var changed=new Dictionary<string,byte[]> { ["db/sources_tables/author"] = Table("sources_tables", ["S", "missing", "alias"]) };
            var report=ExportReferenceValidator.Validate(p,v,_schema,changed);
            Assert.False(report.Ok); Assert.Equal("missing", Assert.Single(report.Issues).Value);
            changed["db/targets_tables/new"] = Table("targets_tables", ["missing", "alias"]);
            Assert.True(ExportReferenceValidator.Validate(p,v,_schema,changed).Ok);
        }
    }

    [Fact]
    public void DeletedTargetAndMissingSourceCannotPass()
    {
        var (p,v)=Packs(false); using(p) using(v)
        {
            var changed=new Dictionary<string,byte[]> { ["db/targets_tables/new"] = Table("targets_tables", ["A", "alias"]) };
            Assert.True(ExportReferenceValidator.Validate(p,v,_schema,changed).Ok);
            var report=ExportReferenceValidator.Validate(p,v,_schema,changed,["db/targets_tables/new"]);
            Assert.False(report.Ok); Assert.Contains("targets.key",report.MissingTargets);
        }
    }

    [Fact]
    public void ReferencesToDifferentColumnsDoNotShareKeyCache()
    {
        var (p,v)=Packs(); using(p) using(v)
        {
            var report=ExportReferenceValidator.Validate(p,v,_schema,new Dictionary<string,byte[]> { ["db/sources_tables/author"] = Table("sources_tables", ["S", "A", "A"]) });
            Assert.False(report.Ok); Assert.Equal("other",Assert.Single(report.Issues).Column);
        }
    }

    [Fact]
    public void FailedExportPreservesExistingTargetAndDoesNotCreateNewTarget()
    {
        var (p,v)=Packs(); var source=p.FilePath; var vanilla=v.FilePath; p.Dispose(); v.Dispose();
        // 使用新添文件模拟错误 DB，不依赖任何真实游戏文件。
        var bad=Path.Combine(_dir,"bad.bin"); File.WriteAllBytes(bad, Table("sources_tables", ["S", "missing", "alias"]));
        var edits=new WarbandEdits(); edits.FileReplacements.Add(("db/sources_tables/author",bad));
        var target=Path.Combine(_dir,"existing.pack"); File.WriteAllText(target,"original target");
        Assert.Throws<InvalidDataException>(()=>WarbandExporter.Export(source,vanilla,target,_schema,edits));
        Assert.Equal("original target",File.ReadAllText(target));
        var absent=Path.Combine(_dir,"absent.pack");
        Assert.Throws<InvalidDataException>(()=>WarbandExporter.Export(source,vanilla,absent,_schema,edits));
        Assert.False(File.Exists(absent)); Assert.Single(edits.FileReplacements);
    }

    [Fact]
    public void MissingTargetColumnAndDamagedTablesAreNotSilentlySkipped()
    {
        var (p,v)=Packs(); using(p) using(v)
        {
            Assert.ThrowsAny<Exception>(()=>ExportReferenceValidator.Validate(p,v,_schema,
                new Dictionary<string,byte[]> { ["db/sources_tables/author"] = Encoding.UTF8.GetBytes("broken") }));
            var report=ReferenceChecker.Check(["sources_tables"],_schema,name=> name.StartsWith("sources")
                ? DbTable.Decode(Table("sources_tables",["S","A","alias"]),"sources_tables",_schema)
                : DbTable.Decode(Table("sources_tables",["S","A","alias"]),"sources_tables",_schema));
            Assert.False(report.Ok); Assert.NotEmpty(report.MissingTargets);
        }
    }

    [Theory]
    [InlineData("[INFO] starting\n{\"results\":[]}", true)]
    [InlineData("io error: ", false)]
    [InlineData("{}", false)]
    [InlineData("{\"results\":null}", false)]
    public void DiagnosticOutputMustBeARealReport(string text,bool valid)
    {
        if(valid) Assert.Contains("results",RpfmCli.ParseDiagnosticsJson(text));
        else Assert.Throws<RpfmException>(()=>RpfmCli.ParseDiagnosticsJson(text));
    }

    public void Dispose() => Directory.Delete(_dir,true);
}
