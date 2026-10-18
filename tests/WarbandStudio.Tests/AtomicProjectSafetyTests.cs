using System.Text.Json;
using WarbandStudio.Pack;

namespace WarbandStudio.Tests;

public sealed class AtomicProjectSafetyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "studio_safety_" + Guid.NewGuid().ToString("N"));
    public AtomicProjectSafetyTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public void AtomicReplaceFailurePreservesOriginalAndStagedFile()
    {
        var original = Path.Combine(_dir, "a.pack"); var staged = original + ".saving";
        File.WriteAllText(original, "old"); File.WriteAllText(staged, "new");
        using (File.Open(original, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.ThrowsAny<IOException>(() => AtomicFile.Commit(staged, original));
        Assert.Equal("old", File.ReadAllText(original)); Assert.Equal("new", File.ReadAllText(staged));
        AtomicFile.Commit(staged, original);
        Assert.Equal("new", File.ReadAllText(original));
    }

    [Fact]
    public void FailedConfigurationWriteIsReportedWithoutTruncation()
    {
        var info = ProjectStore.Ensure(_dir, "A");
        var config = Path.Combine(_dir, ProjectStore.ProjectFile);
        var original = File.ReadAllText(config);
        using (File.Open(config, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.ThrowsAny<IOException>(() => ProjectStore.Save(_dir, new ProjectInfo { Name = "changed" }));
        Assert.Equal(original, File.ReadAllText(config));
        Assert.Equal("A", ProjectStore.Load(_dir).ProjectKey);
    }

    [Fact]
    public void EmptyRecognizedListIsNotRepopulatedFromLastPack()
    {
        var info = ProjectStore.Ensure(_dir);
        info.LastPack = Path.Combine(ProjectStore.PackDirOf(_dir), "unrecognized.pack");
        info.Packs = [];
        ProjectStore.Save(_dir, info);
        Assert.Empty(ProjectStore.Load(_dir).Packs);
        File.WriteAllText(Path.Combine(_dir, ProjectStore.ProjectFile), JsonSerializer.Serialize(new { Name = "legacy", LastPack = info.LastPack }));
        Assert.Single(ProjectStore.Load(_dir).Packs);
    }

    [Fact]
    public void NestedPackDirectoryResolvesCorrectProject()
    {
        ProjectStore.Ensure(_dir, packDir: "output/packs");
        var path = Path.Combine(_dir, "output", "packs", "a.pack");
        Assert.Equal(Path.GetFullPath(_dir), ProjectStore.ProjectDirOfPack(path));
    }

    [Fact]
    public void BackupRetentionProtectsChosenRestoreSource()
    {
        ProjectStore.Ensure(_dir);
        var pack = Path.Combine(ProjectStore.PackDirOf(_dir), "a.pack"); File.WriteAllText(pack, "old");
        var selected = ProjectStore.BackupPack(_dir, pack, 1);
        File.WriteAllText(pack, "current");
        var current = ProjectStore.BackupPack(_dir, pack, 1, [selected]);
        Assert.Equal("old", File.ReadAllText(selected)); Assert.Equal("current", File.ReadAllText(current));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }
}
