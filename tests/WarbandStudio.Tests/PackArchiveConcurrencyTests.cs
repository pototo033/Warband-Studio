using WarbandStudio.Packfile;

namespace WarbandStudio.Tests;

public class PackArchiveConcurrencyTests
{
    [Fact]
    public void SharedArchiveReadsUseIndependentOffsets()
    {
        var path = Path.Combine(Path.GetTempPath(), "studio_read_test_" + Guid.NewGuid().ToString("N") + ".pack");
        var a = Enumerable.Repeat((byte)17, 262144).ToArray();
        var b = Enumerable.Repeat((byte)93, 262144).ToArray();
        try
        {
            PackWriter.WriteNew(path, [("a.bin", a), ("b.bin", b)]);
            using var archive = PackArchive.Open(path);
            Parallel.For(0, 500, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i =>
                Assert.Equal(i % 2 == 0 ? a : b, archive.ReadDecoded(i % 2 == 0 ? "a.bin" : "b.bin")));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
