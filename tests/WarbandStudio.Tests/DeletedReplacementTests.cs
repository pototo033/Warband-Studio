using WarbandStudio.Packfile;

namespace WarbandStudio.Tests;

public sealed class DeletedReplacementTests
{
    [Fact]
    public void DeletedEntryCannotResurrectFromAReplacementWhenSourceViewAlreadyHidesIt()
    {
        var root=Path.Combine(Path.GetTempPath(),"studio_deleted_replacement_"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source=Path.Combine(root,"source.pack");var output=Path.Combine(root,"output.pack");
            PackWriter.WriteNew(source,[("db/permissions/author",new byte[]{1}), ("keep",new byte[]{7})]);
            var replacements=new Dictionary<string,byte[]>{["db/permissions/author"]=[2],["newly_removed"]=[3],["newly_kept"]=[4]};
            string[] removed=["DB/PERMISSIONS/AUTHOR","newly_removed"];
            using(var view=PackArchive.OpenView(source,replacements,removed))
                PackWriter.WriteFrom(view,replacements,output,dropPaths:removed);
            using var saved=PackArchive.Open(output);
            Assert.Null(saved.Find("db/permissions/author"));Assert.Null(saved.Find("newly_removed"));
            Assert.NotNull(saved.Find("keep"));Assert.Equal(new byte[]{4},saved.ReadDecoded(saved.Find("newly_kept")!));
        }
        finally { Directory.Delete(root,true); }
    }
}
