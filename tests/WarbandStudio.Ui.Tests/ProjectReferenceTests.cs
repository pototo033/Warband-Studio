using System.IO;
using WarbandStudio.Pack;
using WarbandStudio.Ui.ViewModels;

namespace WarbandStudio.Ui.Tests;

public sealed partial class BackendSafetyTests
{
    [Fact]
    public async Task ProjectReferencesPersistAcrossRestartsStaySeparateAndCanBeUnloaded()
    {
        var a = Path.Combine(_root, "project_a");
        var b = Path.Combine(_root, "project_b");
        var externalA = MakePack("兵种A.pack", ("db/main_units_tables/external", Table("main_units_tables", new Dictionary<string, object> { ["unit"] = "external_a" })));
        var externalB = MakePack("兵种B.pack");
        var original = File.ReadAllBytes(externalA);
        var first = AiFixture();
        await using (first.Session)
        {
            Assert.True(await first.Vm.NewProjectAsync(a, projectKey: "A"));
            Assert.True(await first.Vm.ImportPackToProjectAsync(_pack));
            var active = first.Backend.Pack;
            var loaded = await AiCall(first.Session, "studio_open_pack", new { path = externalA, reference = true });
            Assert.True(loaded.GetProperty("ok").GetBoolean(), loaded.ToString());
            await AiCall(first.Session, "studio_open_pack", new { path = externalA.ToUpperInvariant(), reference = true });
            Assert.Single(ProjectStore.Load(a).ReferencePacks);
            Assert.Single(ProjectStore.Load(a).Packs);
            Assert.Same(active, first.Backend.Pack);
            Assert.True(await first.Vm.NewProjectAsync(b, projectKey: "B"));
            Assert.Empty(first.Backend.Packs);
            await first.Backend.OpenPackAsync(externalB, asUnitPack: true);
            Assert.Single(ProjectStore.Load(a).ReferencePacks);
            Assert.Single(ProjectStore.Load(b).ReferencePacks);
            await first.Vm.CloseProjectAsync();
            Assert.Single(ProjectStore.Load(b).ReferencePacks);
        }
        var second = AiFixture();
        await using var lifetime = second.Session;
        Assert.True(await second.Vm.OpenRecentProjectAsync(a));
        var reference = Assert.Single(second.Backend.Packs, pack => pack.IsUnitPack);
        Assert.Equal(externalA, reference.PackPath);
        Assert.NotEqual(externalB, reference.PackPath);
        Assert.False(second.Backend.Pack!.IsUnitPack);
        Assert.Equal("A", second.Backend.Pack.ProjectKey);
        Assert.Contains("external_a", second.Backend.BuildModUnitLibraryJson(externalA));
        Assert.All(second.Backend.Packs, pack => Assert.Equal(0, second.Backend.EditCountOf(pack.PackPath)));
        Assert.Equal(0, second.Backend.UndoDepth);
        await second.Vm.UnloadReferenceModAsync(Assert.Single(second.Vm.Mods));
        Assert.Empty(ProjectStore.Load(a).ReferencePacks);
        Assert.Single(second.Backend.Packs);
        Assert.True(await second.Vm.OpenProjectAsync(a));
        Assert.DoesNotContain(second.Backend.Packs, pack => pack.IsUnitPack);
        Assert.Equal(original, File.ReadAllBytes(externalA));
    }

    [Fact]
    public async Task ProjectReferencesSkipMissingBrokenAndWritablePathsWithoutRemovingRecords()
    {
        var project = Path.Combine(_root, "references_partial");
        var info = ProjectStore.Ensure(project);
        var editable = Path.Combine(ProjectStore.PackDirOf(project), "editing.pack");
        File.Copy(_pack, editable);
        info.Packs = [ProjectStore.RelPath(project, editable)];
        var good = MakePack("reference_good.pack");
        var bad = Path.Combine(_root, "reference_bad.pack");
        File.WriteAllText(bad, "broken");
        var missing = Path.Combine(_root, "reference_missing.pack");
        info.ReferencePacks = [missing, bad, good, "packs/editing.pack", good.ToUpperInvariant()];
        ProjectStore.Save(project, info);
        var fixture = AiFixture();
        await using var lifetime = fixture.Session;
        Assert.True(await fixture.Vm.OpenProjectAsync(project));
        Assert.Equal(good, Assert.Single(fixture.Backend.Packs, pack => pack.IsUnitPack).PackPath);
        Assert.Equal(editable, fixture.Backend.Pack!.PackPath);
        Assert.False(fixture.Backend.Pack.IsUnitPack);
        Assert.Contains("3 个未加载", fixture.Vm.Status);
        Assert.Contains(missing, fixture.Vm.Status);
        Assert.Equal(5, ProjectStore.Load(project).ReferencePacks.Count);
        await fixture.Vm.CloseProjectAsync();
        File.Copy(good, missing);
        File.Copy(good, bad, overwrite: true);
        Assert.True(await fixture.Vm.OpenLastProjectAsync());
        Assert.Equal(3, fixture.Backend.Packs.Count(pack => pack.IsUnitPack));
        Assert.Equal(editable, fixture.Backend.Pack!.PackPath);
        Assert.Equal(5, ProjectStore.Load(project).ReferencePacks.Count);
        Assert.All(fixture.Backend.Packs, pack => Assert.Equal(0, fixture.Backend.EditCountOf(pack.PackPath)));
    }

    [Fact]
    public async Task ProjectWithOnlyReferencesReopensAndClosingThroughAiForgetsTheReference()
    {
        var project = Path.Combine(_root, "reference_only");
        var reference = MakePack("reference_only.pack", ("db/main_units_tables/external", Table("main_units_tables", new Dictionary<string, object> { ["unit"] = "reference_only_unit" })));
        var fixture = AiFixture();
        await using var lifetime = fixture.Session;
        Assert.True(await fixture.Vm.NewProjectAsync(project));
        await fixture.Backend.OpenPackAsync(reference, asUnitPack: true);
        await fixture.Vm.CloseProjectAsync();
        Assert.True(await fixture.Vm.OpenLastProjectAsync());
        Assert.Null(fixture.Backend.Pack);
        Assert.True(Assert.Single(fixture.Backend.Packs).IsUnitPack);
        Assert.Equal("工程：reference_only", Assert.Single(fixture.Vm.TreeRoots).Name);
        Assert.Empty(fixture.Vm.CenterTabs);
        var units = await AiCall(fixture.Session, "studio_units", new { reference = reference.Replace('\\', '/') });
        Assert.True(units.GetProperty("ok").GetBoolean(), units.ToString());
        Assert.Equal(1, units.GetProperty("result").GetProperty("total").GetInt32());
        var closed = await AiCall(fixture.Session, "studio_close_pack", new { pack = reference });
        Assert.True(closed.GetProperty("ok").GetBoolean(), closed.ToString());
        Assert.Empty(ProjectStore.Load(project).ReferencePacks);
        Assert.Empty(fixture.Backend.Packs);
    }
}
