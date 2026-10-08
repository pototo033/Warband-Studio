using WarbandStudio.Pack;

namespace WarbandStudio.Tests;

/// <summary>工程目录（v1.5.0）：骨架 / 项目 key / 生成 Pack 目录 / 历史版本备份与"保留最近 N 份"。</summary>
public class ProjectStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ws_projtest_" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Ensure_建工程骨架_写project_json和old和packs目录()
    {
        var info = ProjectStore.Ensure(_dir, prefillKey: "Yukino");
        Assert.True(ProjectStore.IsProject(_dir));
        Assert.True(Directory.Exists(ProjectStore.HistoryDirOf(_dir)));
        Assert.True(Directory.Exists(ProjectStore.PackDirOf(_dir)));          // 默认「生成 Pack 目录」
        Assert.Equal(ProjectStore.DefaultPackDir, info.PackDir);
        Assert.Equal("Yukino", info.ProjectKey);
        Assert.Equal(Path.GetFileName(_dir), info.Name);
        Assert.Equal("Yukino", ProjectStore.Load(_dir).ProjectKey);           // 落盘回读一致
    }

    [Fact]
    public void Ensure_已有工程_沿用不改key()
    {
        var a = ProjectStore.Ensure(_dir, "First");
        a.ProjectKey = "Second";
        ProjectStore.Save(_dir, a);
        var b = ProjectStore.Ensure(_dir, "Third");     // 再 Ensure 不该把 key 覆盖回去
        Assert.Equal("Second", b.ProjectKey);
    }

    [Fact]
    public void PackDirOf_默认packs_可自定义绝对路径()
    {
        var info = ProjectStore.Ensure(_dir);
        Assert.Equal(Path.GetFullPath(Path.Combine(_dir, "packs")), Path.GetFullPath(ProjectStore.PackDirOf(_dir)));
        info.PackDir = "out";
        ProjectStore.Save(_dir, info);
        Assert.Equal(Path.GetFullPath(Path.Combine(_dir, "out")), Path.GetFullPath(ProjectStore.PackDirOf(_dir)));
        info.PackDir = Path.Combine(_dir, "deep", "out2");
        ProjectStore.Save(_dir, info);
        Assert.Equal(Path.GetFullPath(info.PackDir), Path.GetFullPath(ProjectStore.PackDirOf(_dir)));
    }

    [Fact]
    public void ProjectDirOfPack_认生成Pack目录里的包_和旧布局()
    {
        ProjectStore.Ensure(_dir);
        // 新布局：包在 <工程>\packs\
        var p1 = Path.Combine(ProjectStore.PackDirOf(_dir), "a.pack");
        File.WriteAllBytes(p1, [1]);
        Assert.Equal(Path.GetFullPath(_dir), Path.GetFullPath(ProjectStore.ProjectDirOfPack(p1)!));
        // 旧布局：包直接放工程根
        var p2 = Path.Combine(_dir, "b.pack");
        File.WriteAllBytes(p2, [1]);
        Assert.Equal(Path.GetFullPath(_dir), Path.GetFullPath(ProjectStore.ProjectDirOfPack(p2)!));
        // 不相干的目录 → null
        var sub = Path.Combine(_dir, "sub");
        Directory.CreateDirectory(sub);
        var p3 = Path.Combine(sub, "x.pack");
        File.WriteAllBytes(p3, [1]);
        Assert.Null(ProjectStore.ProjectDirOfPack(p3));
    }

    [Fact]
    public void PacksIn_列生成Pack目录里的包_不含old()
    {
        ProjectStore.Ensure(_dir);
        File.WriteAllBytes(Path.Combine(ProjectStore.PackDirOf(_dir), "a.pack"), [1]);
        File.WriteAllBytes(Path.Combine(ProjectStore.PackDirOf(_dir), "b.pack"), [1]);
        File.WriteAllBytes(Path.Combine(ProjectStore.HistoryDirOf(_dir), "a.20260101_000000.pack"), [1]);
        var list = ProjectStore.PacksIn(_dir);
        Assert.Equal(2, list.Count);
        Assert.All(list, f => Assert.Equal(Path.GetFullPath(ProjectStore.PackDirOf(_dir)),
                                           Path.GetFullPath(Path.GetDirectoryName(f)!)));
    }

    [Fact]
    public void PacksIn_旧布局_包在工程根也认()
    {
        ProjectStore.Ensure(_dir);
        File.WriteAllBytes(Path.Combine(_dir, "legacy.pack"), [1]);   // 不进 packs/，模拟老工程
        var list = ProjectStore.PacksIn(_dir);
        Assert.Single(list);
        Assert.Equal("legacy.pack", Path.GetFileName(list[0]));
    }

    [Fact]
    public void BackupPack_备份进old_只保留最近N份()
    {
        ProjectStore.Ensure(_dir);
        var pack = Path.Combine(ProjectStore.PackDirOf(_dir), "T.pack");
        File.WriteAllBytes(pack, [1, 2, 3]);
        for (var i = 0; i < 4; i++) Assert.NotNull(ProjectStore.BackupPack(_dir, pack, keep: 2));
        var hist = ProjectStore.HistoryOf(_dir, pack);
        Assert.Equal(2, hist.Count);                       // 备份 4 份、keep=2 → 只剩 2
        Assert.All(hist, f => Assert.StartsWith("T.", f.Name));
        Assert.All(hist, f => Assert.EndsWith(".pack", f.Name));   // old/ 里是完整 .pack，可直接拷出来用
    }

    [Fact]
    public void TouchRecent_去重_新的在前_最多5个()
    {
        var r = new List<string>();
        for (var i = 1; i <= 6; i++) ProjectStore.TouchRecent(r, "D" + i);
        Assert.Equal(5, r.Count);
        Assert.Equal("D6", r[0]);
        ProjectStore.TouchRecent(r, "D3");                 // 已有 → 提到最前、不重复
        Assert.Equal("D3", r[0]);
        Assert.Single(r.Where(x => x == "D3"));
    }
}
