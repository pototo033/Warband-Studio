using System.IO;
using System.IO.Compression;
using System.Text.Json;
using WarbandStudio.Pack;
using WarbandStudio.Packfile;
using WarbandStudio.Rpfm;
using WarbandStudio.Ui.Services;

namespace WarbandStudio.Ui.Tests;

public sealed partial class BackendSafetyTests
{
    [Fact]
    public async Task InvalidPermissionRollsBackWholePlacementAndExternalNumericUnitIsRecognized()
    {
        await PermissionFixture();
        var before = JsonSerializer.Serialize(_backend.Edits, UpdateRecovery.Json);
        var error = Assert.Throws<InvalidDataException>(() => _backend.Edit(() =>
        {
            _backend.AddUnitToGroup("NEW", "2", 50, 50, true, "EMP");
            return _backend.EnsureUnitPermissions("2", "race", "f1");
        }));
        Assert.Contains("2", error.Message); Assert.Contains("加载兵种包", error.Message);
        Assert.Equal(before, JsonSerializer.Serialize(_backend.Edits, UpdateRecovery.Json)); Assert.False(_backend.Undo());
        var external = MakePack("external.pack", ("db/main_units_tables/author", Table("main_units_tables", new Dictionary<string, object> { ["unit"] = "2" })));
        var current = _backend.Pack;
        var source = await _backend.OpenPackAsync(external, asUnitPack: true);
        Assert.Same(current, _backend.Pack);
        Assert.Equal(1, _backend.EnsureUnitPermissions("2", "race", "f1"));
        Assert.Equal(("2", "f1"), Assert.Single(_backend.Edits.AddExclusive));
        _backend.Edits.AddExclusive.Clear();
        await _backend.CloseSessionAsync(source);
        Assert.Throws<InvalidDataException>(() => _backend.GrantUnit("2", "race", "f1"));
        Assert.Empty(_backend.Edits.AddExclusive);
    }

    [Fact]
    public async Task UnitCheckUsesPendingTableAddsChangesDeletesAndDeclaredExternalDependency()
    {
        await PermissionFixture();
        const string path = "db/main_units_tables/author_pending";
        _backend.Edits.TableReplacements[path] = Table("main_units_tables", new Dictionary<string, object> { ["unit"] = "new_unit" });
        Assert.Equal(1, _backend.GrantUnit("new_unit", "race", "f1"));
        _backend.Edits.TableReplacements[path] = Table("main_units_tables", new Dictionary<string, object> { ["unit"] = "renamed_unit" });
        Assert.Throws<InvalidDataException>(() => _backend.GrantUnit("new_unit", "race", "f2"));
        Assert.Equal(1, _backend.GrantUnit("renamed_unit", "race", "f1"));
        _backend.Edits.RemoveFiles.Add(path);
        Assert.Throws<InvalidDataException>(() => _backend.GrantUnit("renamed_unit", "race", "f2"));
        _backend.Edits.RemoveFiles.Clear();
        Assert.Equal(1, _backend.GrantUnit("renamed_unit", "race", "f2"));
        var parent = MakePack("parent.pack", ("db/main_units_tables/author", Table("main_units_tables", new Dictionary<string, object> { ["unit"] = "external" })));
        var dependent = Path.Combine(_root, "dependent.pack");
        PackWriter.WriteNew(dependent, [], dependencies: [Path.GetFileName(parent)]);
        await _backend.OpenPackAsync(dependent);
        Assert.Equal(1, _backend.GrantUnit("external", "race", "f1"));
    }

    [Fact]
    public async Task FileTreeListsAuthorAndStudioPermissionsAndEveryDbTable()
    {
        await PermissionFixture(($"db/{Military}/author", Table(Military)), ($"db/{Military}/zzzz_studio_edits", Table(Military)),
            ("db/technologies_tables/custom", Table("technologies_tables")));
        foreach (var p in new[] { $"db/{Military}/author", $"db/{Military}/zzzz_studio_edits", "db/technologies_tables/custom" })
            Assert.Contains(_backend.Pack!.Files, f => f.Path == p);
        _backend.Edits.TableReplacements[$"db/{Exclusive}/pending"] = Table(Exclusive);
        _backend.Pack!.SetPendingTables(_backend.Edits.TableReplacements);
        Assert.Contains(_backend.Pack.Files, f => f.Path == $"db/{Exclusive}/pending");
    }

    [Fact]
    public async Task DiagnosticRowsUseRpfmCoordinatesAndKeepNumericUnitValueSeparate()
    {
        await PermissionFixture(($"db/{Military}/zzzz_studio_edits", Table(Military,
            new() { ["unit"] = "u", ["military_group"] = "g1" }, new() { ["unit"] = "v", ["military_group"] = "g2" },
            new() { ["unit"] = "2", ["military_group"] = "g3" })));
        var parsed = RpfmDiagnosticReport.Parse("""
            {"results":[{"DB":{"path":"db/units_to_groupings_military_permissions_tables/zzzz_studio_edits","results":[
              {"cells_affected":[[2,0]],"column_names":["unit"],"report_type":{"InvalidReference":["2","unit"]}}]}}]}
            """);
        var described = RpfmExportValidation.DescribeRows(parsed, _pack, _schema);
        var message = Assert.Single(described.Items).ToString();
        Assert.Contains("数据行 3", message); Assert.Contains("unit=2", message); Assert.Contains("military_group=g3", message);
        Assert.Contains("zzzz_studio_edits", Assert.Throws<InvalidDataException>(described.RequireSaveAllowed).Message);
    }

    [Fact]
    public async Task ReferenceDiagnosticViewNeverOverwritesTargetFileAndNeverLeaksIntoOriginal()
    {
        await PermissionFixture(($"db/{Military}/author", Table(Military, new Dictionary<string, object> { ["unit"] = "u", ["military_group"] = "g1" })));
        var original = File.ReadAllBytes(_pack);
        var external = MakePack("source.pack", ($"db/{Military}/author", Table(Military, new Dictionary<string, object> { ["unit"] = "external", ["military_group"] = "g2" })),
            ("db/main_units_tables/author", Table("main_units_tables", new Dictionary<string, object> { ["unit"] = "external" })));
        var view = Path.Combine(_root, "reference_view.pack");
        var injected = RpfmExportValidation.BuildReferenceView(_pack, [external], view);
        Assert.NotEmpty(injected);
        using var pack = PackArchive.Open(view);
        using var source = PackArchive.Open(_pack);
        Assert.Equal(source.ReadDecoded(source.Find($"db/{Military}/author")!), pack.ReadDecoded(pack.Find($"db/{Military}/author")!));
        Assert.Equal(original, File.ReadAllBytes(_pack));
        Assert.Contains(TableFiles.ReadMerged(pack, "main_units_tables", _schema)!.Rows, r => r[0].ToTsv() == "external");
    }

    [Fact]
    public async Task PortableBundleReplaysPendingEditsAssetsUndoRedoAndExternalSourcesWithoutSaving()
    {
        await PermissionFixture();
        var external = MakePack("external.pack", ("db/main_units_tables/author", Table("main_units_tables", new Dictionary<string, object> { ["unit"] = "2" })));
        await _backend.OpenPackAsync(external, asUnitPack: true);
        var asset = Path.Combine(_root, "pending.png"); File.WriteAllBytes(asset, [1, 2, 3]);
        var baseline = File.ReadAllBytes(_pack);
        _backend.Edit(() => { _backend.GrantUnit("2", "race", "f1"); _backend.Edits.FileReplacements.Add(("ui/test.png", asset)); return true; });
        _backend.Edit(() => { _backend.SetGroupPos("G", 10, 20); return true; });
        Assert.True(_backend.Undo());
        var before = JsonSerializer.Serialize(_backend.CaptureUpdateRecovery(), UpdateRecovery.Json);
        var bundle = Path.Combine(_root, "repro.zip");
        _backend.ExportDiagnosticBundle(bundle, "测试保存失败后的编辑");
        Assert.Equal(before, JsonSerializer.Serialize(_backend.CaptureUpdateRecovery(), UpdateRecovery.Json));
        Assert.Equal(baseline, File.ReadAllBytes(_pack));
        var extracted = Path.Combine(_root, "extracted"); ZipFile.ExtractToDirectory(bundle, extracted);
        var state = DiagnosticBundle.ReadExtracted(extracted);
        Assert.Equal(2, state.Packs.Count); Assert.True(File.Exists(Path.Combine(extracted, "logs", "app.log")));
        var active = Assert.Single(state.Packs.Where(p => !p.IsUnitPack));
        Assert.Single(active.Edits.AddExclusive); Assert.Single(active.Undo); Assert.Single(active.Redo);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(active.Edits.FileReplacements.Single().Source));
        await using var replay = new Backend(_settings, ValidateFixturePack);
        foreach (var p in state.Packs.OrderByDescending(p => p.IsUnitPack))
            replay.RestoreUpdatePack(p, Path.Combine(extracted, p.Baseline), recoveredCopy: true);
        Assert.Equal(("2", "f1"), Assert.Single(replay.Edits.AddExclusive));
        Assert.True(replay.Redo()); Assert.Equal(10, replay.Edits.InfoEdits["G"].X);
        Assert.True(replay.Undo()); Assert.True(replay.Undo()); Assert.Empty(replay.Edits.AddExclusive);
        Assert.True(replay.Redo()); Assert.Single(replay.Edits.AddExclusive);
        Assert.Equal(1, replay.GrantUnit("2", "race", "f2"));
        File.AppendAllText(Path.Combine(extracted, "context.txt"), "corrupt");
        Assert.Throws<InvalidDataException>(() => DiagnosticBundle.ReadExtracted(extracted));
    }

    [Fact]
    public async Task BundleFailurePreservesExistingZipAndAllUnsavedState()
    {
        await PermissionFixture();
        _backend.Edits.FileReplacements.Add(("ui/test.png", Path.Combine(_root, "missing.png")));
        var destination = Path.Combine(_root, "existing.zip"); File.WriteAllText(destination, "old bundle");
        Assert.Throws<FileNotFoundException>(() => _backend.ExportDiagnosticBundle(destination, "failure"));
        Assert.Equal("old bundle", File.ReadAllText(destination)); Assert.Single(_backend.Edits.FileReplacements);
        Assert.Empty(Directory.GetFiles(_root, "existing.zip.*.tmp"));
    }
}
