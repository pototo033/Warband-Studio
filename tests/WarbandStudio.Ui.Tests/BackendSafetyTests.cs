using System.IO;
using System.Reflection;
using System.Text;
using WarbandStudio.Pack;
using WarbandStudio.Packfile;
using WarbandStudio.Rpfm;
using WarbandStudio.Ui.Services;
using WarbandStudio.Ui.ViewModels;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
namespace WarbandStudio.Ui.Tests;

public sealed partial class BackendSafetyTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "studio_backend_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task TabFactionSettingsUndoSaveReopenAndClearStaySeparateFromUnitPermissions()
    {
        const string inner = "ui/campaign ui/warband_upgrades.twui.xml";
        const string xml = """
            <root><warband_upgrades id="warband_upgrades" this="root">
            <callback_with_context context_object_id="CcoCampaignUnit" context_function_id="FirstValidUpgradeCategoryForUnit(this, &quot;EMP&quot;)"/>
            <callback_with_context context_function_id="UnitGroupUpgradesTree(List.Filter(Category == SelectedCategory))"/>
            <holder_tab_emp id="holder_tab_emp" this="a"><callback_with_context callback_id="ContextVisibilitySetter" context_function_id="HasUpgrades"/><userproperties><property name="God" value="EMP"/></userproperties></holder_tab_emp>
            </warband_upgrades></root>
            """;
        var path = MakePack("visibility.pack", (inner, Encoding.UTF8.GetBytes(xml)));
        await _backend.OpenPackAsync(path);
        _backend.Edit(() => { _backend.SetTabAllowedFactions("EMP", ["reikland", "wissenland"]); return true; });
        Assert.Contains(inner, _backend.EditedFiles());
        Assert.Empty(_backend.Edits.AddUnitGroup); Assert.Empty(_backend.Edits.AddExclusive);
        Assert.True(_backend.Undo()); Assert.Empty(_backend.Edits.TabAllowedFactions);
        Assert.True(_backend.Redo()); Assert.Equal(2, _backend.Edits.TabAllowedFactions["EMP"].Length);
        await _backend.SaveInPlaceAsync();
        Assert.Equal(0, _backend.EditCount);
        using (var archive = PackArchive.Open(path))
        {
            var saved = TabFactionVisibility.Read(Encoding.UTF8.GetString(archive.ReadDecoded(archive.Find(inner)!)));
            Assert.Equal(["reikland", "wissenland"], saved["EMP"]);
        }
        await _backend.OpenPackAsync(_pack);
        Assert.Empty(_backend.Edits.TabAllowedFactions);
        await _backend.OpenPackAsync(path);
        _backend.Edit(() => { _backend.SetTabAllowedFactions("EMP", []); return true; });
        await _backend.SaveInPlaceAsync();
        using (var archive = PackArchive.Open(path))
            Assert.Empty(TabFactionVisibility.Read(Encoding.UTF8.GetString(archive.ReadDecoded(archive.Find(inner)!))));
    }
    private readonly Schema _schema = Schema.Load(RpfmCli.LocateSchema()!);
    private Backend _backend = null!;
    private AppSettings _settings = null!;
    private string _pack = "";
    private const string Cost = "resource_costs_tables", Pool = "pooled_resource_factor_junctions_tables", Link = "resource_cost_pooled_resource_junctions_tables";

    private byte[] Table(string name, params Dictionary<string, object>[] rows)
    {
        var definition = _schema.DefinitionsFor(name).OrderByDescending(d => d.Version).First();
        var raw = rows.Select(values => definition.Fields.Select(field =>
        {
            values.TryGetValue(field.Name, out var value);
            return field.Type switch
            {
                SchemaFieldType.Boolean => DbValue.Of(value is true),
                SchemaFieldType.F32 or SchemaFieldType.F64 => DbValue.Of(value is null ? 0d : Convert.ToDouble(value)),
                SchemaFieldType.StringU8 or SchemaFieldType.StringU16 or SchemaFieldType.OptionalStringU8 or SchemaFieldType.OptionalStringU16 => DbValue.OfStr(value?.ToString() ?? ""),
                SchemaFieldType.SequenceU16 => DbValue.OfBytes(new byte[2]),
                SchemaFieldType.SequenceU32 => DbValue.OfBytes(new byte[4]),
                _ => DbValue.Of(value is null ? 0L : Convert.ToInt64(value)),
            };
        }).ToArray()).ToList();
        return DbEncoder.Encode(new DbTable { TableName = name, Version = definition.Version, Guid = Guid.NewGuid().ToString(),
            Definition = definition, Columns = Schema.ProcessedColumns(definition), RawRows = raw, Rows = raw });
    }
    private string MakePack(string name, params (string Path, byte[] Data)[] extra)
    {
        var path = Path.Combine(_root, name);
        var unitReferences = extra.Where(e => e.Path.StartsWith("db/unit_to_unit_group_junctions_tables/", StringComparison.OrdinalIgnoreCase))
            .SelectMany(e => DbTable.Decode(e.Data, "unit_to_unit_group_junctions_tables", _schema).Rows.Select(r => r[0].ToTsv()))
            .Distinct().Select(u => new Dictionary<string, object> { ["unit"] = u }).ToArray();
        var referencedUnits = unitReferences.Length == 0 ? Array.Empty<(string, byte[])>()
            : new[] { ("db/main_units_tables/test_referenced_units", Table("main_units_tables", unitReferences)) };
        PackWriter.WriteNew(path, new[]
        {
            ("payload", Encoding.UTF8.GetBytes(name)),
            ($"db/{Cost}/author", Table(Cost, new Dictionary<string, object> { ["id"] = "C", ["treasury_cost"] = -100L })),
            ($"db/{Pool}/author", Table(Pool, new() { ["unique_id"] = "food_pool", ["resource"] = "food", ["factor"] = "other" },
                new() { ["unique_id"] = "chiv_pool", ["resource"] = "chivalry", ["factor"] = "other" })),
            ($"db/{Link}/author", Table(Link,
                new() { ["pooled_resource_factor"] = "food_pool", ["resource_cost"] = "C", ["amount"] = -3L, ["context"] = "absolute", ["ui_resource_transaction_pooled_resource"] = "default" },
                new() { ["pooled_resource_factor"] = "chiv_pool", ["resource_cost"] = "C", ["amount"] = -4L, ["context"] = "percentage", ["ui_resource_transaction_pooled_resource"] = "secondary" })),
        }.Concat(extra).Concat(referencedUnits));
        return path;
    }
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        var game = Path.Combine(_root, "game"); Directory.CreateDirectory(Path.Combine(game, "data"));
        var ak = Path.Combine(game, "assembly_kit", "raw_data", "db"); Directory.CreateDirectory(ak);
        File.WriteAllText(Path.Combine(ak, "pooled_resource_change_contexts.xml"),
            "<dataroot><pooled_resource_change_contexts><id>absolute</id></pooled_resource_change_contexts><pooled_resource_change_contexts><id>percentage</id></pooled_resource_change_contexts></dataroot>");
        PackWriter.WriteNew(Path.Combine(game, "data", "db.pack"), new[] { "main_units_tables", Cost, Pool, Link, "factions_tables", "unit_upgrade_groups_tables" }
            .Select(name => ($"db/{name}/data__", Table(name))).Concat(new[]
            {
                ("db/pooled_resources_tables/data__", Table("pooled_resources_tables", new() { ["key"]="food" }, new() { ["key"]="chivalry" })),
                ("db/pooled_resource_factors_tables/data__", Table("pooled_resource_factors_tables", new Dictionary<string, object> { ["key"]="other" })),
                ("db/ui_resource_transaction_pooled_resource_junctions_tables/data__", Table("ui_resource_transaction_pooled_resource_junctions_tables", new() { ["key"]="default" }, new() { ["key"]="secondary" })),
                ("db/unit_upgrade_group_ui_categories_tables/data__", Table("unit_upgrade_group_ui_categories_tables", new Dictionary<string, object> { ["category"]="EMP" })),
                ("db/technologies_tables/data__", Table("technologies_tables", new Dictionary<string, object> { ["key"]="test_tech" })),
                ("db/building_levels_tables/data__", Table("building_levels_tables", new Dictionary<string, object> { ["level_name"]="test_building" })),
            }));
        _settings = new AppSettings { GameDir = game, BackupDir = Path.Combine(_root, "backup"), PersistChanges = false, AutoSave = false };
        _backend = new Backend(_settings);
        _pack = MakePack("a.pack");
        await _backend.OpenPackAsync(_pack);
    }

    [Fact]
    public async Task InvalidFinalReferencesKeepOriginalOpenAndPendingEdits()
    {
        var original = File.ReadAllBytes(_pack);
        var bad = Path.Combine(_root, "bad-link.bin");
        File.WriteAllBytes(bad, Table(Link, new Dictionary<string, object>
        { ["pooled_resource_factor"] = "missing_factor", ["resource_cost"] = "C", ["amount"] = -1L }));
        _backend.Edit(() => { _backend.Edits.FileReplacements.Add(($"db/{Link}/author", bad)); return true; });
        var session = _backend.Pack;
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => _backend.SaveInPlaceAsync());
        Assert.Contains("missing_factor", error.Message);
        Assert.Equal(original, File.ReadAllBytes(_pack));
        Assert.Same(session, _backend.Pack); Assert.True(session!.IsOpen);
        Assert.Single(_backend.Edits.FileReplacements);
        Assert.True(_backend.Undo()); Assert.Empty(_backend.Edits.FileReplacements);
    }
    public async Task DisposeAsync()
    {
        foreach (var session in _backend.Packs.ToArray()) await _backend.CloseSessionAsync(session);
        await _backend.DisposeAsync();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Fact]
    public async Task BackupFailureLeavesOriginalSessionAndEditsIntact()
    {
        var original = File.ReadAllBytes(_pack);
        var blocker = Path.Combine(_root, "not_a_directory"); File.WriteAllText(blocker, "x"); _settings.BackupDir = blocker;
        _backend.Edit(() => _backend.UpdateWarbandCost("C", -200, null, 0, false));
        var session = _backend.Pack;
        await Assert.ThrowsAnyAsync<IOException>(() => _backend.SaveInPlaceAsync());
        Assert.Equal(original, File.ReadAllBytes(_pack)); Assert.Same(session, _backend.Pack);
        Assert.True(session!.IsOpen); Assert.Equal(-200, _backend.CostList().Single().Gold); Assert.True(_backend.EditCount > 0);
    }

    [Fact]
    public async Task ReplacementFailureLeavesOriginalOpenAndCanRetry()
    {
        var original = File.ReadAllBytes(_pack);
        _backend.Edit(() => _backend.UpdateWarbandCost("C", -200, null, 0, false));
        using (File.Open(_pack, FileMode.Open, FileAccess.Read, FileShare.Read))
            await Assert.ThrowsAnyAsync<IOException>(() => _backend.SaveInPlaceAsync());
        Assert.Equal(original, File.ReadAllBytes(_pack)); Assert.True(_backend.Pack!.IsOpen); Assert.True(_backend.EditCount > 0);
        await _backend.SaveInPlaceAsync();
        Assert.True(_backend.Pack!.IsOpen); Assert.Equal(_pack, _backend.Pack.PackPath); Assert.Equal(0, _backend.EditCount);
        Assert.Equal(-200, _backend.CostList().Single().Gold);
    }

    [Fact]
    public async Task SavingAnotherSessionPreservesActiveAndUnsavedEdits()
    {
        var first = _backend.Pack!;
        _backend.Edit(() => _backend.UpdateWarbandCost("C", -200, null, 0, false));
        var second = await _backend.OpenPackAsync(MakePack("b.pack"));
        _backend.Edit(() => _backend.UpdateWarbandCost("C", -300, null, 0, false));
        await _backend.SaveInPlaceAsync(first);
        Assert.Same(second, _backend.Pack); Assert.True(first.IsOpen); Assert.Equal(2, _backend.Packs.Count);
        Assert.Equal(-300, _backend.CostList().Single().Gold); Assert.True(_backend.EditCount > 0);
    }

    [Fact]
    public async Task OldReadContextCannotReadAnotherSession()
    {
        var context = _backend.CaptureReadContext();
        await _backend.OpenPackAsync(MakePack("b.pack"));
        var ran = false;
        Assert.Throws<OperationCanceledException>(() => _backend.Read(context, () => { ran = true; return _backend.CostList(); }));
        Assert.False(ran);
    }

    [Fact]
    public async Task RestoreOldestVersionAtRetentionLimitKeepsCurrentBackup()
    {
        var project = Path.Combine(_root, "project"); ProjectStore.Ensure(project);
        _backend.BindProject(_backend.Pack!, project, "PROJECT"); _settings.HistoryKeep = 1;
        var selected = Path.Combine(ProjectStore.HistoryDirOf(project), "a.20000101_000000.pack");
        PackWriter.WriteNew(selected, [("payload", Encoding.UTF8.GetBytes("old"))]);
        await _backend.RestoreFromBackupAsync(_pack, selected);
        using var restored = PackArchive.Open(_pack);
        Assert.Equal("old", Encoding.UTF8.GetString(restored.ReadDecoded(restored.Find("payload")!)));
        Assert.Contains(ProjectStore.HistoryOf(project, _pack), f => f.FullName != selected);
        Assert.True(_backend.Pack!.IsOpen);
    }

    [Fact]
    public void MultiResourceQuantityEditPreservesOtherLinkAndLabels()
    {
        Assert.Equal(2, _backend.CostList().Single().Resources.Count);
        _backend.Edit(() => _backend.UpdateWarbandCost("C", -150, "food", -8, true, "food_pool", "food_pool"));
        var cost = _backend.CostList().Single();
        Assert.Equal(2, cost.Resources.Count); Assert.Equal(-8, cost.Resources.Single(r => r.PoolId == "food_pool").Amount);
        var other = cost.Resources.Single(r => r.PoolId == "chiv_pool");
        Assert.Equal(-4, other.Amount); Assert.Equal("percentage", other.Context); Assert.Equal("secondary", other.UiPooled);
        var output = Path.Combine(_root, "result.pack"); _backend.Export(output);
        using var result = PackArchive.Open(output);
        Assert.NotNull(result.Find($"db/{Link}/author"));
        var table = TableFiles.ReadComposite(result, Link, ["pooled_resource_factor", "resource_cost"], _schema)!;
        Assert.Equal(2, table.Rows.Count);
        Assert.DoesNotContain(result.Entries, entry => entry.Path.Replace('\\', '/').Equals($"db/{Link}/zzzz_studio_edits", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SequentialResourceChangesAndClearDoNotLeavePendingLinks()
    {
        _backend.Edit(() => _backend.CreateWarbandCost("new", -10, "food", -1));
        _backend.Edit(() => _backend.UpdateWarbandCost("new", -10, "chivalry", -2, true));
        Assert.Single(_backend.CostList().Single(c => c.Id == "new").Resources);
        _backend.Edit(() => _backend.UpdateWarbandCost("new", -10, null, 0, true));
        Assert.Empty(_backend.CostList().Single(c => c.Id == "new").Resources);
        Assert.DoesNotContain(_backend.Edits.PoolCosts, row => row.ResourceCost == "new");
        Assert.True(_backend.Undo()); Assert.Single(_backend.CostList().Single(c => c.Id == "new").Resources);
        Assert.True(_backend.Redo()); Assert.Empty(_backend.CostList().Single(c => c.Id == "new").Resources);
    }

    [Fact]
    public void FailedEditRollsBackAndNoOpDoesNotEraseRedo()
    {
        _backend.Edit(() => _backend.CreateWarbandCost("new", -10, null, 0)); Assert.True(_backend.Undo());
        _backend.Edit(() => "validation refused"); Assert.True(_backend.Redo());
        Assert.Throws<IOException>(() => _backend.Edit<int>(() => { _backend.Edits.AddCost["broken"] = -99; throw new IOException("test"); }));
        Assert.DoesNotContain(_backend.CostList(), c => c.Id == "broken");
    }

    [Fact]
    public async Task SwitchingToEmptyProjectClearsSessionsAndCanReopenOriginal()
    {
        var vm = new MainViewModel(_settings);
        var backend = (Backend)typeof(MainViewModel).GetField("_backend", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
        var a = Path.Combine(_root, "A"); var b = Path.Combine(_root, "B");
        Assert.True(await vm.NewProjectAsync(a, "output/packs", "AKEY"));
        Assert.True(await vm.ImportPackToProjectAsync(_pack)); Assert.Single(backend.Packs);
        Assert.Equal(a, backend.Pack!.ProjectDirectory); Assert.Equal("AKEY", backend.Pack.ProjectKey);
        Assert.True(await vm.NewProjectAsync(b, projectKey: "BKEY"));
        Assert.Empty(backend.Packs); Assert.Null(backend.Pack); Assert.Empty(vm.CenterTabs); Assert.Empty(vm.Library);
        Assert.True(await vm.OpenProjectAsync(a)); Assert.Single(backend.Packs); Assert.Equal("AKEY", backend.Pack!.ProjectKey);
        await vm.CloseProjectAsync(); await backend.DisposeAsync();
    }

    [Fact]
    public void ReferencedPendingCostCannotBeDeletedAndGoldUndoWorks()
    {
        _backend.Edit(() => _backend.CreateWarbandCost("used", -10, null, 0));
        _backend.Edit(() => { _backend.Edits.SetRoute(new WarbandEdits.RouteEdit("pending", "g1", "g2", "used", 1, 0), true); return true; });
        var count = _backend.EditCount;
        var refusal = _backend.Edit(() => _backend.DeleteWarbandCost("used"));
        Assert.StartsWith("无法删除", refusal); Assert.Equal(count, _backend.EditCount);
        _backend.Edit(() => _backend.UpdateWarbandCost("used", -50, null, 0, false));
        Assert.Equal(-50, _backend.CostList().Single(c => c.Id == "used").Gold);
        Assert.True(_backend.Undo()); Assert.Equal(-10, _backend.CostList().Single(c => c.Id == "used").Gold);
    }

    [Fact]
    public async Task OtherConsumerTablesProtectDeleteAndRename()
    {
        var consumer = _schema.Definitions.First(kv => kv.Key != Link && kv.Key != "unit_upgrade_to_unit_groups_tables"
            && kv.Value.Any(d => d.Fields.Any(f => f.IsReference is { } r && r.Table.Replace("_tables", "") == "resource_costs")));
        var field = consumer.Value.SelectMany(d => d.Fields).First(f => f.IsReference is { } r && r.Table.Replace("_tables", "") == "resource_costs").Name;
        var pack = MakePack("consumer.pack", ($"db/{consumer.Key}/consumer", Table(consumer.Key, new Dictionary<string, object> { [field] = "C" })));
        await _backend.OpenPackAsync(pack);
        Assert.Contains(consumer.Key, _backend.Edit(() => _backend.DeleteWarbandCost("C")));
        Assert.StartsWith("无法重命名", _backend.Edit(() => _backend.RenameWarbandCost("C", "renamed")));
        Assert.Equal(0, _backend.EditCount);
    }

    [Fact]
    public void CaseOnlyRenamePreservesAllResourcesAndUpdatesTheirForeignKeys()
    {
        Assert.StartsWith("成本工坊", _backend.Edit(() => _backend.RenameWarbandCost("C", "c")));
        Assert.Equal(2, _backend.CostList().Single().Resources.Count);
        var output = Path.Combine(_root, "renamed.pack"); _backend.Export(output);
        using var archive = PackArchive.Open(output);
        var costs = TableFiles.ReadMerged(archive, Cost, _schema)!;
        Assert.Equal("c", costs.Rows.Single()[costs.Columns.FindIndex(f => f.Name == "id")].ToTsv());
        var links = TableFiles.ReadComposite(archive, Link, ["pooled_resource_factor", "resource_cost"], _schema)!;
        Assert.Equal(2, links.Rows.Count);
        Assert.All(links.Rows, row => Assert.Equal("c", row[links.Columns.FindIndex(f => f.Name == "resource_cost")].ToTsv()));
    }

    [Fact]
    public void DeleteThenRecreateExportsOnlyFinalResourceState()
    {
        _backend.Edit(() => _backend.DeleteWarbandCost("C"));
        Assert.StartsWith("成本工坊", _backend.Edit(() => _backend.CreateWarbandCost("C", -77, "food", -9)));
        var output = Path.Combine(_root, "recreated.pack"); _backend.Export(output);
        using var archive = PackArchive.Open(output);
        var links = TableFiles.ReadComposite(archive, Link, ["pooled_resource_factor", "resource_cost"], _schema)!;
        Assert.Single(links.Rows);
        Assert.Equal(-9, links.Rows.Single()[links.Columns.FindIndex(f => f.Name == "amount")].Num);
    }

    [Fact]
    public async Task ClosingWaitsForReadAndDoesNotDisposeItsArchiveMidRead()
    {
        var session = _backend.Pack!; var context = _backend.CaptureReadContext();
        using var started = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var read = Task.Run(() => _backend.Read(context, () =>
        {
            started.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            return session.Archive!.ReadDecoded(session.Archive.Find("payload")!);
        }));
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
        var close = Task.Run(() => _backend.CloseSessionAsync(session));
        release.Set();
        Assert.Equal("a.pack", Encoding.UTF8.GetString(await read)); await close;
        Assert.False(session.IsOpen);
    }

    [Fact]
    public async Task UnrecognizedLastPackDoesNotReopenAndBadProjectPreservesCurrent()
    {
        var vm = new MainViewModel(_settings);
        var backend = (Backend)typeof(MainViewModel).GetField("_backend", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
        var project = Path.Combine(_root, "recognized"); Assert.True(await vm.NewProjectAsync(project));
        Assert.True(await vm.ImportPackToProjectAsync(_pack));
        await vm.ApplyProjectPacksAsync([]); await vm.CloseProjectAsync();
        Assert.True(await vm.OpenProjectAsync(project)); Assert.Empty(backend.Packs);
        Assert.True(await vm.ImportPackToProjectAsync(Path.Combine(ProjectStore.PackDirOf(project), "a.pack")));
        var original = backend.Pack;
        var broken = Path.Combine(_root, "broken"); var info = ProjectStore.Ensure(broken);
        var brokenPack = Path.Combine(ProjectStore.PackDirOf(broken), "broken.pack"); File.WriteAllText(brokenPack, "broken");
        info.Packs = [ProjectStore.RelPath(broken, brokenPack)]; ProjectStore.Save(broken, info);
        Assert.False(await vm.OpenProjectAsync(broken)); Assert.Same(original, backend.Pack); Assert.Equal(project, vm.ProjectDir);
        await vm.CloseProjectAsync(); await backend.DisposeAsync();
    }

    private static byte[] Loc(string text)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(new byte[] { 255, 254, 76, 79, 67, 0 }); writer.Write(1u); writer.Write(1u);
        foreach (var value in new[] { "name_key", text }) { writer.Write((ushort)value.Length); writer.Write(Encoding.Unicode.GetBytes(value)); }
        writer.Write((byte)0); return stream.ToArray();
    }

    [Fact]
    public async Task LocalizationLayersFollowActiveAndReferenceSources()
    {
        var a = MakePack("loc_a.pack", ("text/db/name.loc", Loc("A")));
        var b = MakePack("loc_b.pack", ("text/db/name.loc", Loc("B")));
        var reference = MakePack("loc_ref.pack", ("text/db/name.loc", Loc("REF")));
        var first = await _backend.OpenPackAsync(a); var second = await _backend.OpenPackAsync(b);
        var loc = (Localization)typeof(Backend).GetProperty("Loc", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_backend)!;
        Assert.Equal("B", loc.Text("name_key"));
        _backend.SwitchTo(first); Assert.Equal("A", loc.Text("name_key"));
        var unitPack = await _backend.OpenPackAsync(reference, asUnitPack: true);
        await _backend.CloseSessionAsync(first); await _backend.CloseSessionAsync(second);
        Assert.Equal("REF", loc.Text("name_key"));
        await _backend.CloseSessionAsync(unitPack); Assert.Null(loc.Text("name_key"));
    }

    [Fact]
    public void CostCacheReusesRecordsUntilAnEditInvalidatesIt()
    {
        var first = _backend.CostList().Single();
        Assert.Same(first, _backend.CostList().Single());
        _backend.Edit(() => _backend.UpdateWarbandCost("C", -777, null, 0, false));
        var changed = _backend.CostList().Single();
        Assert.NotSame(first, changed); Assert.Equal(-777, changed.Gold);
        Assert.Equal(2, changed.Resources.Count);
    }

    [Fact]
    public async Task CostFormListsAllLinksPreservesStatusAndResetsOnPackChange()
    {
        var vm = new MainViewModel(_settings);
        var backend = (Backend)typeof(MainViewModel).GetField("_backend", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
        await using var lifetime = backend;
        await backend.OpenPackAsync(_pack);
        vm.SetStatus("已保存");
        await vm.RefreshPooledResourcesAsync();
        vm.CostId = "C";
        Assert.Equal(2, vm.CostResourceLinks.Count);
        vm.SelectedCostResourceLink = vm.CostResourceLinks.Single(r => r.PoolId == "chiv_pool");
        Assert.Equal("chivalry", vm.CostResText); Assert.Equal("-4", vm.CostResAmount);
        Assert.Equal("已保存", vm.Status);
        await backend.OpenPackAsync(MakePack("form_other.pack"));
        await vm.RefreshPooledResourcesAsync();
        Assert.Equal("", vm.CostId); Assert.Empty(vm.CostResourceLinks); Assert.False(vm.CostIsExisting);
    }

    [Fact]
    public void ExportFailureKeepsExistingDestinationAndEdits()
    {
        var destination = MakePack("export.pack"); var original = File.ReadAllBytes(destination);
        _backend.Edit(() => _backend.UpdateWarbandCost("C", -888, null, 0, false));
        using (File.Open(destination, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.ThrowsAny<IOException>(() => _backend.Export(destination));
        Assert.Equal(original, File.ReadAllBytes(destination));
        Assert.True(_backend.EditCount > 0);
        Assert.Empty(Directory.GetFiles(_root, "*.exporting.*"));
    }

    [Fact]
    public async Task ReferenceOnlySessionRemainsReadableWithoutAnActiveEditablePack()
    {
        await _backend.OpenPackAsync(MakePack("reference.pack"), asUnitPack: true);
        await _backend.CloseSessionAsync(_backend.Pack!);
        var context = _backend.CaptureReadContext();
        Assert.Null(context.Session); Assert.True(_backend.IsCurrent(context));
        Assert.Equal(42, _backend.Read(context, () => 42));
    }

    [Fact]
    public void ChangedResourceLinkMarksItsOriginalAuthorFile()
    {
        _backend.Edit(() => _backend.UpdateWarbandCost("C", -100, "food", -22, true, replacePoolId: "food_pool"));
        Assert.Contains($"db/{Link}/author", _backend.EditedFiles());
        _backend.Edit(() => _backend.RemoveWarbandCostResource("C", "chiv_pool"));
        Assert.Contains($"db/{Link}/author", _backend.EditedFiles());
    }

    [Fact]
    public async Task SameIconNameInDifferentPackagesUsesDifferentCachedFiles()
    {
        var a = MakePack("icon_a.pack", ("ui/units/icons/shared.png", new byte[] { 1, 2, 3 }));
        var b = MakePack("icon_b.pack", ("ui/units/icons/shared.png", new byte[] { 4, 5, 6 }));
        var first = await _backend.OpenPackAsync(a); var second = await _backend.OpenPackAsync(b);
        var icons = new IconResolver(_settings, () => _schema, null);
        icons.Prepare(first.Archive!); var firstIcon = icons.IconFileFor("shared", "probe");
        icons.Prepare(second.Archive!); var secondIcon = icons.IconFileFor("shared", "probe");
        Assert.NotEqual(firstIcon, secondIcon); Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(firstIcon!));
        Assert.Equal(new byte[] { 4, 5, 6 }, File.ReadAllBytes(secondIcon!));
    }

    [Fact]
    public async Task MergeThreeKnightGroupsPreservesLocationUnitsUndoAndExport()
    {
        const string groupsTable = "unit_upgrade_groups_tables", junctions = "unit_to_unit_group_junctions_tables",
            infos = "unit_upgrade_group_ui_infos_tables";
        var groups = new[] { "Yukino_Emp_Cav_Empire_Knights_RoR_3", "Yukino_Emp_Cav_Empire_Knights_RoR_2", "Yukino_Emp_Cav_Empire_Knights_RoR_1" };
        var units = new[] { "knights_stubborn_bulls", "knights_everlasting_light", "knights_morr" };
        var input = MakePack("knights.pack",
            ($"db/{groupsTable}/author", Table(groupsTable, groups.Select(g => new Dictionary<string, object> { ["unit_group"] = g }).ToArray())),
            ($"db/{junctions}/author", Table(junctions, groups.Select((g, i) => new Dictionary<string, object> { ["unit"] = units[i], ["unit_group"] = g }).ToArray())),
            ($"db/{infos}/author", Table(infos, groups.Select((g, i) => new Dictionary<string, object> { ["unit_upgrade_group"] = g, ["x"] = 274 + 100 * i, ["y"] = 600, ["category"] = "EMP" }).ToArray())));
        await _backend.OpenPackAsync(input);
        _backend.Edit(() => { _backend.MergeGroups("", groups); return true; });
        var merged = Assert.Single(_backend.Edits.AddGroup);
        Assert.Equal((274, 600, "EMP"), _backend.Edits.InfoEdits[merged]);
        Assert.Equal(units.Order(), _backend.Edits.AddJunction.Where(j => j.Group == merged).Select(j => j.Unit).Order());
        Assert.True(_backend.Undo()); Assert.Empty(_backend.Edits.RemoveGroup); Assert.Empty(_backend.Edits.AddJunction);
        Assert.True(_backend.Redo()); Assert.Equal((274, 600, "EMP"), _backend.Edits.InfoEdits[merged]);
        await _backend.SaveInPlaceAsync();
        Assert.True(_backend.Pack!.IsOpen); Assert.Equal(0, _backend.EditCount);
        using var archive = PackArchive.Open(input);
        var locations = TableFiles.ReadMerged(archive, infos, _schema)!;
        Assert.Single(locations.Rows);
        Assert.Equal(600, locations.Rows[0][locations.Columns.FindIndex(c => c.Name == "y")].Num);
        Assert.Equal("EMP", locations.Rows[0][locations.Columns.FindIndex(c => c.Name == "category")].ToTsv());
        var members = TableFiles.ReadComposite(archive, junctions, ["unit", "unit_group"], _schema)!;
        Assert.Equal(3, members.Rows.Count);
        Assert.All(members.Rows, r => Assert.Equal(merged, r[members.Columns.FindIndex(c => c.Name == "unit_group")].ToTsv()));
    }

    [Fact]
    public async Task BatchMoveUsesOneRefreshAndUndoForFiftyGroups()
    {
        var vm = new MainViewModel(_settings);
        await using var backend = (Backend)typeof(MainViewModel).GetField("_backend", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
        await backend.OpenPackAsync(_pack);
        backend.Edit(() => { for (var i = 0; i < 50; i++) backend.AddUnitToGroup("g" + i, "u" + i, i * 10, 300, true, "EMP"); return true; });
        var depth = backend.UndoDepth; var refreshes = 0;
        vm.PackOpened += () => refreshes++;
        vm.ApplyCanvasEdit(System.Text.Json.JsonSerializer.Serialize(new { type = "setCategories", category = "EMP2",
            groups = Enumerable.Range(0, 50).Select(i => new { group = "g" + i, x = -999, y = -999 }) }));
        Assert.Equal(1, refreshes); Assert.Equal(depth + 1, backend.UndoDepth);
        for (var i = 0; i < 50; i++) Assert.Equal((i * 10, 300, "EMP2"), backend.Edits.InfoEdits["g" + i]);
        Assert.True(backend.Undo());
        Assert.All(backend.Edits.InfoEdits.Values, p => Assert.Equal("EMP", p.Category));
        Assert.True(backend.Redo()); Assert.All(backend.Edits.InfoEdits.Values, p => Assert.Equal("EMP2", p.Category));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MergeClearsAllSelectedGroupsRoutesLinksAndRequirements(bool keepExisting)
    {
        const string groupsTable = "unit_upgrade_groups_tables", junctions = "unit_to_unit_group_junctions_tables",
            infos = "unit_upgrade_group_ui_infos_tables", routes = "unit_upgrade_to_unit_groups_tables", links = "unit_upgrade_group_ui_links_tables",
            tech = "unit_upgrade_to_tech_requirements_tables", buildings = "unit_upgrade_to_building_level_requirements_tables";
        var names = new[] { "keep", "drop", "outside", "outside2" };
        var edges = new[] { (Key: "keep_old", B: "keep", T: "outside"), (Key: "drop_old", B: "outside", T: "drop"), (Key: "unrelated", B: "outside", T: "outside2") };
        var path = MakePack("merge_connections.pack",
            ($"db/{groupsTable}/author", Table(groupsTable, names.Select(g => new Dictionary<string, object> { ["unit_group"] = g }).ToArray())),
            ($"db/{junctions}/author", Table(junctions, names.Select(g => new Dictionary<string, object> { ["unit_group"] = g, ["unit"] = "unit_" + g }).ToArray())),
            ($"db/{infos}/author", Table(infos, names.Select(g => new Dictionary<string, object> { ["unit_upgrade_group"] = g, ["x"] = 100, ["y"] = 200, ["category"] = "EMP" }).ToArray())),
            ($"db/{routes}/author", Table(routes, edges.Select(e => new Dictionary<string, object> { ["upgrade_key"] = e.Key, ["base_unit_group"] = e.B, ["target_unit_group"] = e.T, ["resource_cost"] = "C" }).ToArray())),
            ($"db/{links}/author", Table(links, edges.Select(e => new Dictionary<string, object> { ["child_key"] = e.T, ["parent_key"] = e.B }).ToArray())),
            ($"db/{tech}/author", Table(tech, edges.Select(e => new Dictionary<string, object> { ["unit_upgrade"] = e.Key, ["technology"] = "test_tech" }).ToArray())),
            ($"db/{buildings}/author", Table(buildings, edges.Select(e => new Dictionary<string, object> { ["unit_upgrade"] = e.Key, ["building_level"] = "test_building" }).ToArray())));
        await _backend.OpenPackAsync(path);
        _backend.Edit(() => {
            _backend.Edits.SetRoute(new("pending", "keep", "outside2", "C", 0, 0), true);
            _backend.Edits.SetLink(new("outside2", "keep", 0, 0, 0, 0, 0), true); return true;
        });
        _backend.Edit(() => { _backend.MergeGroups(keepExisting ? "keep" : "", keepExisting ? ["drop"] : ["keep", "drop"]); return true; });
        Assert.Contains("keep_old", _backend.Edits.RemoveRoute); Assert.Contains("drop_old", _backend.Edits.RemoveRoute);
        Assert.Contains("pending", _backend.Edits.RemoveRoute); Assert.Empty(_backend.Edits.AddRoute); Assert.Empty(_backend.Edits.AddLink);
        Assert.Contains($"db/{tech}/author", _backend.EditedFiles());
        Assert.True(_backend.Undo()); Assert.Single(_backend.Edits.AddRoute); Assert.Empty(_backend.Edits.RemoveRoute);
        Assert.True(_backend.Redo());
        var output = Path.Combine(_root, "clean_merge.pack"); _backend.Export(output);
        using var archive = PackArchive.Open(output);
        foreach (var table in new[] { routes, tech, buildings })
        {
            var data = TableFiles.ReadConcat(archive, table, _schema)!;
            Assert.Single(data.Rows);
            var column = data.Columns.FindIndex(c => c.Name == (table == routes ? "upgrade_key" : "unit_upgrade"));
            Assert.Equal("unrelated", data.Rows[0][column].ToTsv());
        }
        Assert.Single(TableFiles.ReadConcat(archive, links, _schema)!.Rows);
        Assert.Single(TableFiles.ReadConcat(archive, Cost, _schema)!.Rows);
    }

    [Fact]
    public void MergeStagedGroupsCapturesPositionBeforeRemovingSourceInfo()
    {
        _backend.Edit(() =>
        {
            _backend.AddUnitToGroup("new_a", "unit_a", 150, 350, true, "EMP");
            _backend.AddUnitToGroup("new_b", "unit_b", 250, 350, true, "EMP");
            return true;
        });
        _backend.Edit(() => { _backend.MergeGroups("", ["new_a", "new_b", "new_a"]); return true; });
        var merged = Assert.Single(_backend.Edits.AddGroup);
        Assert.Equal((150, 350, "EMP"), _backend.Edits.InfoEdits[merged]);
        Assert.Equal(2, _backend.Edits.AddJunction.Count);
        Assert.All(_backend.Edits.AddJunction, j => Assert.Equal(merged, j.Group));
    }
}
