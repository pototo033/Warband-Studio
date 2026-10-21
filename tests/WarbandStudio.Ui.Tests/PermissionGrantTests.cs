using System.IO;
using WarbandStudio.Pack;
using WarbandStudio.Packfile;
using WarbandStudio.Ui.Services;

namespace WarbandStudio.Ui.Tests;

public sealed partial class BackendSafetyTests
{
    private const string Military="units_to_groupings_military_permissions_tables",Exclusive="units_to_exclusive_faction_permissions_tables";
    private async Task PermissionFixture(params (string Path,byte[] Data)[] extra)
    {
        var oldGame=_settings.GameDir;
        await _backend.DisposeAsync();
        var game=Path.Combine(_root,"permission_game"); Directory.CreateDirectory(Path.Combine(game,"data"));
        var files=new Dictionary<string,byte[]>();
        using(var old=PackArchive.Open(Path.Combine(oldGame,"data","db.pack")))
            foreach(var e in old.VisibleEntries) files[e.Path]=old.ReadDecoded(e);
        files["db/factions_tables/data__"]=Table("factions_tables",
            new(){["key"]="f1",["subculture"]="race",["military_group"]="g1"},
            new(){["key"]="f2",["subculture"]="race",["military_group"]="g2"},
            new(){["key"]="f3",["subculture"]="race",["military_group"]="g3"});
        files["db/groupings_military_tables/data__"]=Table("groupings_military_tables",new(){["military_group"]="g1"},new(){["military_group"]="g2"},new(){["military_group"]="g3"});
        files["db/main_units_tables/data__"]=Table("main_units_tables",new(){["unit"]="u"},new(){["unit"]="v"});
        files[$"db/{Military}/data__"]=Table(Military,new Dictionary<string,object>{["unit"]="u",["military_group"]="g1"});
        files[$"db/{Exclusive}/data__"]=Table(Exclusive);
        PackWriter.WriteNew(Path.Combine(game,"data","db.pack"),files.Select(p=>(p.Key,p.Value)));
        var ak=Path.Combine(game,"assembly_kit","raw_data","db");Directory.CreateDirectory(ak);
        File.Copy(Path.Combine(oldGame,"assembly_kit","raw_data","db","pooled_resource_change_contexts.xml"),Path.Combine(ak,"pooled_resource_change_contexts.xml"));
        _settings=new AppSettings{GameDir=game,BackupDir=Path.Combine(_root,"backup"),PersistChanges=false,AutoSave=false};
        _backend=new Backend(_settings,ValidateFixturePack);
        _pack=MakePack("permissions.pack",extra); await _backend.OpenPackAsync(_pack);
    }

    [Fact]
    public async Task AutomaticPlacementPreservesPartialRaceAndFactionPermissionsButExplicitGrantCanBroaden()
    {
        await PermissionFixture();
        Assert.Equal(0,_backend.EnsureUnitPermissions("u","race",null));
        Assert.Equal(0,_backend.EnsureUnitPermissions("u","race","f1"));
        Assert.Empty(_backend.Edits.AddUnitGroup);Assert.Empty(_backend.Edits.AddExclusive);
        Assert.Equal(2,_backend.GrantUnit("u","race",null));
        Assert.Equal(new[]{"g2","g3"},_backend.Edits.AddUnitGroup.Select(p=>p.MilitaryGroup).OrderBy(x=>x));
        Assert.Equal(0,_backend.GrantUnit("u","race",null));
        Assert.Equal(1,_backend.GrantUnit("u","race","f1"));
        Assert.Equal(0,_backend.GrantUnit("u","race","f1"));
        Assert.Single(_backend.Edits.AddExclusive);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AutomaticPlacementUnderstandsExclusiveAllowedAndDoesNotExpandExistingRaceScope(bool allowed)
    {
        await PermissionFixture(($"db/{Exclusive}/author",Table(Exclusive,new Dictionary<string,object>{["key"]="v",["faction"]="f1",["allowed"]=allowed})));
        Assert.Equal(allowed ? 0:3,_backend.EnsureUnitPermissions("v","race",null));
        Assert.Equal(allowed ? 0:3,_backend.Edits.AddUnitGroup.Count);
        Assert.Empty(_backend.Edits.AddExclusive);
        Assert.Equal(0,_backend.EnsureUnitPermissions("v","race",null));
    }

    [Fact]
    public async Task BrandNewFactionGrantUsesExclusiveOnlyAndIsIdempotent()
    {
        await PermissionFixture();
        Assert.Equal(1,_backend.EnsureUnitPermissions("v","race","f2"));
        Assert.Empty(_backend.Edits.AddUnitGroup);Assert.Equal(("v","f2"),Assert.Single(_backend.Edits.AddExclusive));
        Assert.Equal(0,_backend.EnsureUnitPermissions("v","race","f2"));
    }

    [Fact]
    public async Task WriterDeduplicatesAgainstAuthorVanillaAndRepeatedSavesIncludingStalePendingGrants()
    {
        var author=Table(Military,new(){["unit"]="v",["military_group"]="g2"},new(){["unit"]="v",["military_group"]="g3"});
        await PermissionFixture(($"db/{Military}/author",author));
        // Also exercise old/recovered queues which bypass the new UI's existence checks.
        _backend.Edits.SetUnitGroup("u","g1",true);_backend.Edits.SetUnitGroup("v","g2",true);_backend.Edits.SetUnitGroup("v","g3",true);
        _backend.Edits.SetUnitGroup("v","g1",true);
        await _backend.SaveInPlaceAsync();
        using(var pack=PackArchive.Open(_pack))
        {
            Assert.Equal(author,pack.ReadDecoded(pack.Find($"db/{Military}/author")!));
            var added=DbTable.Decode(pack.ReadDecoded(pack.Find($"db/{Military}/zzzz_studio_edits")!),Military,_schema);
            Assert.Single(added.Rows);Assert.Equal("v",added.Rows[0][added.Columns.FindIndex(c=>c.Name=="unit")].ToTsv());
        }
        await _backend.SaveInPlaceAsync();
        using(var pack=PackArchive.Open(_pack))
        {
            var rows=TableFiles.EntriesFor(pack,Military).SelectMany(p=>DbTable.Decode(pack.ReadDecoded(p),Military,_schema).Rows).ToList();
            Assert.Equal(3,rows.Count);
        }
    }

    [Fact]
    public async Task DeletedAuthorFileDoesNotSuppressAnExplicitGrantEvenWhenOldBufferStillExists()
    {
        var path=$"db/{Military}/author";
        var author=Table(Military,new Dictionary<string,object>{["unit"]="v",["military_group"]="g2"});
        await PermissionFixture((path,author));
        _backend.Edits.TableReplacements[path]=author;
        _backend.Edits.RemoveFiles.Add(path);
        _backend.Edits.SetUnitGroup("v","g2",true);
        await _backend.SaveInPlaceAsync();
        using var pack=PackArchive.Open(_pack);
        Assert.Null(pack.Find(path));
        var saved=TableFiles.ReadComposite(pack,Military,["unit","military_group"],_schema)!;
        Assert.Single(saved.Rows);
        Assert.Equal("g2",saved.Rows[0][saved.Columns.FindIndex(c=>c.Name=="military_group")].ToTsv());
    }

    [Fact]
    public async Task GrantRemoveReaddTransitionsKeepCompositePairsWithoutDuplicateOrResurrection()
    {
        await PermissionFixture(($"db/{Military}/author",Table(Military,new Dictionary<string,object>{["unit"]="v",["military_group"]="g2"})));
        _backend.Edit(()=>{_backend.GrantUnit("v","race",null);return true;});
        Assert.Equal(2,_backend.Edits.AddUnitGroup.Count);
        Assert.True(_backend.Undo());Assert.Empty(_backend.Edits.AddUnitGroup);
        Assert.True(_backend.Redo());Assert.Equal(2,_backend.Edits.AddUnitGroup.Count);
        _backend.Edits.SetUnitGroup("v","g2",false);
        _backend.GrantUnit("v","race",null);
        Assert.Empty(_backend.Edits.RemoveUnitGroup);
        Assert.DoesNotContain(_backend.Edits.AddUnitGroup,p=>p.MilitaryGroup=="g2");
        _backend.Edits.SetUnitGroup("v","g1",false); // Added then removed: not restored by structural edits.
        Assert.DoesNotContain(_backend.Edits.AddUnitGroup,p=>p.MilitaryGroup=="g1");
    }

    [Fact]
    public async Task OpenRenderConnectSplitAndMoveNeverExpandMilitaryPermissions()
    {
        await PermissionFixture(
            ("db/unit_upgrade_groups_tables/author",Table("unit_upgrade_groups_tables",new Dictionary<string,object>{["unit_group"]="G"})),
            ("db/unit_upgrade_group_ui_infos_tables/author",Table("unit_upgrade_group_ui_infos_tables",new Dictionary<string,object>{["unit_upgrade_group"]="G",["category"]="EMP",["x"]=0,["y"]=0})),
            ("db/unit_to_unit_group_junctions_tables/author",Table("unit_to_unit_group_junctions_tables",new(){["unit"]="u",["unit_group"]="G"},new(){["unit"]="v",["unit_group"]="G"})),
            ($"db/{Military}/author",Table(Military,new Dictionary<string,object>{["unit"]="v",["military_group"]="g1"})));
        var original=File.ReadAllBytes(_pack);
        _backend.BuildWarbandJson();Assert.Equal(original,File.ReadAllBytes(_pack));Assert.True(_backend.Edits.IsEmpty);
        _backend.SplitGroup("G",0,0,"EMP","race","f2");
        _backend.MoveUnitsToNewGroup("NEW",[("G","u")],0,0,"EMP","race","f2");
        _backend.AddRoute("G","NEW","C",0,0,false,0,0);
        Assert.Empty(_backend.Edits.AddUnitGroup);Assert.Empty(_backend.Edits.AddExclusive);
    }
}
