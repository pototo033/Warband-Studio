using System.Security.Cryptography;
using WarbandStudio.Pack;
using WarbandStudio.Packfile;
using WarbandStudio.Rpfm;

namespace WarbandStudio.Probe;

/// <summary>Real CLI integration test. Only writes explicitly supplied test output directory.</summary>
public static class SaveGateTest
{
    public static int Run(string[] args)
    {
        if (args.Length != 4) throw new ArgumentException("save-gate-test <source.pack> <game-data/db.pack> <test-output-dir>");
        var source=Path.GetFullPath(args[1]); var vanilla=Path.GetFullPath(args[2]); var output=Path.GetFullPath(args[3]);
        Directory.CreateDirectory(output);
        var schema=WarbandStudio.Packfile.Schema.Load(RpfmCli.LocateSchema()!);
        var good=Path.Combine(output,"warnings.pack");
        RpfmDiagnosticReport Validate(string path) => RpfmExportValidation.Validate(path,vanilla,Console.WriteLine);
        var report=WarbandExporter.Export(source,vanilla,good,schema,validateStaged:Validate);
        File.WriteAllLines(Path.Combine(output,"warning-report.txt"),report.Notes);
        if(!report.Notes.Any(n=>n.StartsWith("Warning:"))) throw new Exception("Fixture did not produce expected no-AK warnings.");
        Console.WriteLine("PASS real RPFM warnings allow export: "+report.Notes.First(n=>n.StartsWith("RPFM：")));

        using var pack=PackArchive.Open(source);
        const string tableName="resource_cost_pooled_resource_junctions_tables";
        var entry=TableFiles.EntriesFor(pack,tableName).First();
        var table=DbTable.Decode(pack.ReadDecoded(entry),tableName,schema);
        var index=table.Definition.Fields.FindIndex(c=>c.Name=="pooled_resource_factor");
        table.RawRows[0][index]=DbValue.OfStr("studio_test_missing_factor");
        var edits=new WarbandEdits(); edits.TableReplacements[entry.Path]=DbEncoder.Encode(table);
        var before=SHA256.HashData(File.ReadAllBytes(good));
        foreach(var target in new[]{good,Path.Combine(output,"must_not_exist.pack")})
        {
            try { WarbandExporter.Export(source,vanilla,target,schema,edits,Validate); throw new Exception("Bad reference was accepted."); }
            catch(InvalidDataException ex) when(ex.Message.Contains("studio_test_missing_factor"))
            { Console.WriteLine("PASS real RPFM InvalidReference blocks "+Path.GetFileName(target)); }
        }
        if(!before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(good))) || File.Exists(Path.Combine(output,"must_not_exist.pack")) ||
            edits.TableReplacements.Count!=1 || Directory.GetFiles(output,"*.exporting.*").Length!=0)
            throw new Exception("Atomic save / pending edit preservation failed.");
        Console.WriteLine("PASS original target and pending edits preserved; staged packs removed.");
        return 0;
    }
}
