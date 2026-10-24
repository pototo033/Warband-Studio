using System.Text;
using System.IO;
using WarbandStudio.Packfile;
using WarbandStudio.Ui.Services;

namespace WarbandStudio.Ui.Tests;

public sealed partial class BackendSafetyTests
{
    [Fact]
    public void ColdSkinCacheUsesExactVanillaReferenceButCurrentPackWinsAndEscapesUrl()
    {
        const string inner = "ui/skins/default/warband_upgrades/background_images_test.png";
        var game = Path.Combine(_root, "fake-game"); Directory.CreateDirectory(Path.Combine(game, "data"));
        PackWriter.WriteNew(Path.Combine(game, "data", "ui3.pack"), [(inner, Encoding.UTF8.GetBytes("vanilla"))]);
        using var empty = PackArchive.Open(MakePack("empty-skin.pack"));
        var tag = "test#雪乃%_" + Guid.NewGuid().ToString("N");
        var cold = new SkinAssets(empty, game, null, tag);
        cold.Prepare([("test", "background_images_test.png", "")]);
        Assert.Equal("vanilla", File.ReadAllText(Path.Combine(cold.CacheDir, "background_images_test.png")));
        Assert.Contains("%23", cold.Background("test")); Assert.Contains("%25", cold.Background("test"));
        using var current = PackArchive.Open(MakePack("current-skin.pack", (inner, Encoding.UTF8.GetBytes("current"))));
        var sameCache = new SkinAssets(current, game, null, tag);
        sameCache.Prepare([("test", "background_images_test.png", "")]);
        Assert.Equal("current", File.ReadAllText(Path.Combine(sameCache.CacheDir, "background_images_test.png")));
    }

    [Fact]
    public void MissingImageNeverLooksSuccessfulOnlyBecauseOldCacheExistsAndStagingReplacesSameLength()
    {
        using var pack = PackArchive.Open(MakePack("missing-skin.pack"));
        var notes = new List<string>();
        var skin = new SkinAssets(pack, null, notes.Add, "test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(skin.CacheDir);
        var target = Path.Combine(skin.CacheDir, "background_images_test.png"); File.WriteAllText(target, "old");
        skin.Prepare([("test", "background_images_test.png", "")]);
        Assert.Null(skin.Background("test")); Assert.Contains(notes, n => n.Contains("图片缺失"));
        var local = Path.Combine(_root, "new-art.png"); File.WriteAllText(local, "new");
        skin.StageLocalFile(local, "background_images_test.png");
        Assert.Equal("new", File.ReadAllText(target));
        skin.Prepare([("test", "background_images_test.png", "")]);
        Assert.NotNull(skin.Background("test"));
    }
}
