using System.Text.Json;
using WarbandStudio.Pack;
using WarbandStudio.Packfile;

namespace WarbandStudio.Ui.Tests;

public sealed partial class BackendSafetyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteMultiUnitGroupThenDragOneMemberNeverResurrectsOtherMember(bool newlyCreated)
    {
        const string groups="unit_upgrade_groups_tables", members="unit_to_unit_group_junctions_tables", infos="unit_upgrade_group_ui_infos_tables";
        const string oldKey="test_EMP_u";
        var path=MakePack("delete-readd.pack",
            ($"db/{groups}/author",Table(groups,new Dictionary<string,object>{["unit_group"]=oldKey})),
            ($"db/{members}/author",Table(members,new(){["unit"]="u",["unit_group"]=oldKey},new(){["unit"]="v",["unit_group"]=oldKey})),
            ($"db/{infos}/author",Table(infos,new Dictionary<string,object>{["unit_upgrade_group"]=oldKey,["category"]="EMP",["x"]=100,["y"]=200})));
        await _backend.OpenPackAsync(path);_backend.ProjectKey="test";_backend.Pack!.ProjectKey="test";
        if(newlyCreated) { _backend.AddUnitToGroup(oldKey,"u",100,200,true,"EMP");_backend.AddUnitToGroup(oldKey,"v",100,200,false,"EMP"); }
        _backend.Edit(()=>{_backend.RemoveGroup(oldKey);return true;});
        using(var deleted=JsonDocument.Parse(_backend.BuildWarbandJson()))
            Assert.Empty(deleted.RootElement.GetProperty("groups").EnumerateArray());
        var key=_backend.MakeGroupKey("EMP",["u"],"fallback");
        Assert.NotEqual(oldKey,key);
        _backend.Edit(()=>{_backend.AddUnitToGroup(key,"u",300,400,true,"EMP");return true;});
        void Check()
        {
            using var doc=JsonDocument.Parse(_backend.BuildWarbandJson());
            var group=Assert.Single(doc.RootElement.GetProperty("groups").EnumerateArray());
            Assert.Equal(key,group.GetProperty("unit_group").GetString());
            Assert.Equal("u",Assert.Single(group.GetProperty("units").EnumerateArray()).GetString());
        }
        Check();Assert.True(_backend.Undo());Assert.True(_backend.Redo());Check();
        await _backend.SaveInPlaceAsync();Check();await _backend.OpenPackAsync(path);Check();
        using var pack=PackArchive.Open(path);
        Assert.Single(TableFiles.ReadComposite(pack,members,["unit","unit_group"],_schema)!.Rows);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SplitKeepsEveryUnitExactlyOnceThroughUndoAndSave(bool newlyMerged)
    {
        const string groups="unit_upgrade_groups_tables", members="unit_to_unit_group_junctions_tables", infos="unit_upgrade_group_ui_infos_tables";
        var path=MakePack("split.pack",
            ($"db/{groups}/author",Table(groups,new Dictionary<string,object>{["unit_group"]="G"})),
            ($"db/{members}/author",Table(members,new(){["unit"]="u",["unit_group"]="G"},new(){["unit"]="v",["unit_group"]="G"})),
            ($"db/{infos}/author",Table(infos,new Dictionary<string,object>{["unit_upgrade_group"]="G",["category"]="EMP",["x"]=100,["y"]=200})));
        await _backend.OpenPackAsync(path);
        var source="G";
        if(newlyMerged)
        {
            _backend.ProjectKey="test";
            _backend.MergeGroups("MERGED",["G"]);
            source="MERGED";
        }
        void Check(int expectedGroups)
        {
            using var doc=JsonDocument.Parse(_backend.BuildWarbandJson());
            var gs=doc.RootElement.GetProperty("groups").EnumerateArray().ToArray();
            Assert.Equal(expectedGroups,gs.Length);
            Assert.Equal(new[]{"u","v"},gs.SelectMany(g=>g.GetProperty("units").EnumerateArray().Select(u=>u.GetString())).OrderBy(u=>u));
            if(expectedGroups==2) Assert.All(gs,g=>Assert.Equal(1,g.GetProperty("units").GetArrayLength()));
        }
        _backend.Edit(()=>{Assert.Equal(1,_backend.SplitGroup(source,100,200,"EMP",null,null));return true;});
        Check(2);
        Assert.True(_backend.Undo());Check(1);
        Assert.True(_backend.Redo());Check(2);
        await _backend.SaveInPlaceAsync();Check(2);
        await _backend.OpenPackAsync(path);Check(2);
        using var pack=PackArchive.Open(path);
        Assert.Equal(2,TableFiles.ReadComposite(pack,members,["unit","unit_group"],_schema)!.Rows.Count);
    }
}
