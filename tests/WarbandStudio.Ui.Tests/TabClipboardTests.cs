using System.IO;
using System.Text.Json;
using WarbandStudio.Pack;
using WarbandStudio.Packfile;

namespace WarbandStudio.Ui.Tests;

public sealed partial class BackendSafetyTests
{
    private async Task<string> ClipboardFixture()
    {
        var path=MakePack("clipboard.pack",
            ($"db/{TG}/SourceData",Table(TG,new(){["unit_group"]="A"},new(){["unit_group"]="B"},new(){["unit_group"]="C"})),
            ($"db/{TG}/DestData",Table(TG,new Dictionary<string,object>{["unit_group"]="D"})),
            ($"db/{TJ}/SourceData",Table(TJ,new(){["unit_group"]="A",["unit"]="u1"},new(){["unit_group"]="A",["unit"]="u2"},new(){["unit_group"]="B",["unit"]="u3"},new(){["unit_group"]="C",["unit"]="u4"})),
            ($"db/{TJ}/DestData",Table(TJ,new Dictionary<string,object>{["unit_group"]="D",["unit"]="u1"})),
            ($"db/{TI}/SourceData",Table(TI,new(){["unit_upgrade_group"]="A",["category"]="SRC",["x"]=10,["y"]=20},new(){["unit_upgrade_group"]="B",["category"]="SRC",["x"]=150,["y"]=250},new(){["unit_upgrade_group"]="C",["category"]="OUT"})),
            ($"db/{TI}/DestData",Table(TI,new Dictionary<string,object>{["unit_upgrade_group"]="D",["category"]="DEST",["x"]=600})),
            ($"db/{TR}/SourceData",Table(TR,new(){["upgrade_key"]="R1",["base_unit_group"]="A",["target_unit_group"]="B",["resource_cost"]="C",["required_rank"]=4,["subtracted_rank"]=1},new(){["upgrade_key"]="R2",["base_unit_group"]="B",["target_unit_group"]="C",["resource_cost"]="C"})),
            ($"db/{TL}/SourceData",Table(TL,new(){["parent_key"]="A",["child_key"]="B",["parent_link_position"]=1,["child_link_position"]=4,["parent_link_position_offset"]=0.25,["child_link_position_offset"]=0.75,["mid_link_offset"]=-30},new(){["parent_key"]="B",["child_key"]="C"})),
            ($"db/{TabFileNaming.Cats}/author",Table(TabFileNaming.Cats,new(){["category"]="SRC"},new(){["category"]="DEST"},new(){["category"]="OUT"})),
            ("db/unit_upgrade_to_tech_requirements_tables/author",Table("unit_upgrade_to_tech_requirements_tables",new(){["unit_upgrade"]="R1",["technology"]="tech_a"},new(){["unit_upgrade"]="R1",["technology"]="tech_b"})),
            ("db/unit_upgrade_to_building_level_requirements_tables/author",Table("unit_upgrade_to_building_level_requirements_tables",new Dictionary<string,object>{["unit_upgrade"]="R1",["building_level"]="building_a"})),
            ("db/technologies_tables/author",Table("technologies_tables",new(){["key"]="tech_a"},new(){["key"]="tech_b"})),
            ("db/building_levels_tables/author",Table("building_levels_tables",new Dictionary<string,object>{["level_name"]="building_a"})));
        await _backend.OpenPackAsync(path);return path;
    }
    private static string ClipCell(DbTable t,int row,string name)=>t.RawRows[row][t.Definition.Fields.FindIndex(f=>f.Name==name)].ToTsv();
    [Fact]
    public async Task ClipboardCopiesFullInternalPageWithoutPermissionsAndPersistsIndependentKeys()
    {
        var path=await ClipboardFixture();var original=File.ReadAllBytes(path);
        Assert.Contains("2 个组",_backend.CopyTab("SRC"));Assert.True(_backend.Edits.IsEmpty);Assert.False(_backend.Undo());
        Assert.Equal(original,File.ReadAllBytes(path));
        _backend.PasteTab("DEST");
        Assert.Empty(_backend.Edits.AddUnitGroup);Assert.Empty(_backend.Edits.AddExclusive);Assert.Empty(_backend.Edits.UnlockXp);
        Assert.DoesNotContain(_backend.Edits.TableReplacements.Keys,p=>p.Contains("permissions_tables")||p.Contains("main_units_tables"));
        var groups=_backend.ReadTable($"db/{TG}/DestData");Assert.Equal(3,groups.Rows.Count);
        var ids=groups.RawRows.Select(r=>r[PageField(groups,"unit_group")].ToTsv()).Where(k=>k!="D").ToHashSet();
        Assert.Equal(2,ids.Count);Assert.DoesNotContain("A",ids);Assert.DoesNotContain("B",ids);
        var infos=_backend.ReadTable($"db/{TI}/DestData");
        Assert.Contains(infos.RawRows,r=>r[PageField(infos,"x")].ToTsv()=="10"&&r[PageField(infos,"y")].ToTsv()=="20");
        var routes=_backend.ReadTable($"db/{TR}/DestData");Assert.Single(routes.Rows);
        var route=ClipCell(routes,0,"upgrade_key");Assert.NotEqual("R1",route);
        Assert.Contains(ClipCell(routes,0,"base_unit_group"),ids);Assert.Contains(ClipCell(routes,0,"target_unit_group"),ids);
        Assert.Equal("C",ClipCell(routes,0,"resource_cost"));Assert.Equal("4",ClipCell(routes,0,"required_rank"));Assert.Equal("1",ClipCell(routes,0,"subtracted_rank"));
        var links=_backend.ReadTable($"db/{TL}/DestData");Assert.Single(links.Rows);
        Assert.Equal("-30.0000",ClipCell(links,0,"mid_link_offset"));Assert.Equal("0.2500",ClipCell(links,0,"parent_link_position_offset"));
        var tech=_backend.ReadTable("db/unit_upgrade_to_tech_requirements_tables/zzzz_studio_edits");Assert.Equal(2,tech.Rows.Count);
        Assert.All(tech.RawRows,r=>Assert.Equal(route,r[PageField(tech,"unit_upgrade")].ToTsv()));
        Assert.True(_backend.Undo());Assert.True(_backend.Edits.IsEmpty);Assert.True(_backend.Redo());
        _backend.PasteTab("DEST");Assert.Equal(5,_backend.ReadTable($"db/{TG}/DestData").Rows.Count);
        Assert.True(_backend.Undo());Assert.True(_backend.Redo());
        await _backend.SaveInPlaceAsync();await _backend.OpenPackAsync(path);
        using var pack=PackArchive.Open(path);
        Assert.Equal(8,TableFiles.ReadComposite(pack,TG,["unit_group"],_schema)!.Rows.Count);
        Assert.Equal(4,TableFiles.ReadComposite(pack,TR,["upgrade_key"],_schema)!.Rows.Count);
        Assert.Equal(6,TableFiles.ReadComposite(pack,"unit_upgrade_to_tech_requirements_tables",["unit_upgrade","technology"],_schema)!.Rows.Count);
    }
    private static int PageField(DbTable table,string name)=>table.Definition.Fields.FindIndex(f=>f.Name==name);

    [Fact]
    public async Task ClipboardFreezesPendingMovesAndRejectsMissingTargetWithoutPartialWrites()
    {
        await ClipboardFixture();_backend.Edit(()=>{_backend.SetGroupPos("A",123,456);return true;});
        _backend.CopyTab("SRC");_backend.Edit(()=>{_backend.SetGroupPos("A",700,800);return true;});
        var before=JsonSerializer.Serialize(_backend.Edits,WarbandStudio.Ui.Services.UpdateRecovery.Json);
        Assert.Throws<InvalidOperationException>(()=>_backend.PasteTab("MISSING"));
        Assert.Equal(before,JsonSerializer.Serialize(_backend.Edits,WarbandStudio.Ui.Services.UpdateRecovery.Json));
        _backend.PasteTab("DEST");var infos=_backend.ReadTable($"db/{TI}/DestData");
        Assert.Contains(infos.RawRows,r=>r[PageField(infos,"x")].ToTsv()=="123"&&r[PageField(infos,"y")].ToTsv()=="456");
        Assert.DoesNotContain(infos.RawRows,r=>r[PageField(infos,"x")].ToTsv()=="700");
        var savedSource=_backend.ReadTable($"db/{TI}/SourceData");Assert.Equal("700",ClipCell(savedSource,0,"x"));
    }

    [Fact]
    public async Task ClipboardRejectsCrossPackPasteAndSourceRemovalDoesNotEraseSnapshot()
    {
        var path=await ClipboardFixture();_backend.CopyTab("SRC");
        _backend.Edit(()=>{_backend.RemoveGroup("A");_backend.RemoveGroup("B");return true;});
        _backend.PasteTab("DEST");Assert.Equal(3,_backend.ReadTable($"db/{TG}/DestData").Rows.Count);
        await _backend.OpenPackAsync(MakePack("another-pack.pack"));
        Assert.Throws<InvalidOperationException>(()=>_backend.PasteTab("DEST"));Assert.True(_backend.Edits.IsEmpty);
        await _backend.OpenPackAsync(path);Assert.True(_backend.Undo());
    }
}
