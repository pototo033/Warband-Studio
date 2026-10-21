using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using WarbandStudio.Pack;
using WarbandStudio.Packfile;
using WarbandStudio.Ui.Services;

namespace WarbandStudio.Ui.Tests;

public sealed partial class BackendSafetyTests
{
    private const string TG=TabFileNaming.Groups,TJ=TabFileNaming.Junc,TI=TabFileNaming.Infos,TR=TabFileNaming.Routes,TL=TabFileNaming.Links;
    private async Task<string> TabNamingFixture(string tab="EMP2",string file="Yukino_Upgrade_Emp_II",bool shared=false)
    {
        var path=MakePack("tab-naming.pack",
            ($"db/{TG}/{file}",Table(TG,(shared ? new[]{"G","H"}:new[]{"G"}).Select(g=>new Dictionary<string,object>{["unit_group"]=g}).ToArray())),
            ($"db/{TJ}/{file}",Table(TJ,(shared ? new[]{"G","H"}:new[]{"G"}).Select(g=>new Dictionary<string,object>{["unit"]="u",["unit_group"]=g}).ToArray())),
            ($"db/{TI}/{file}",Table(TI,(shared ? new[]{"G","H"}:new[]{"G"}).Select(g=>new Dictionary<string,object>{["unit_upgrade_group"]=g,["category"]=g=="G"?tab:"SKV",["x"]=10,["y"]=20}).ToArray())),
            ($"db/{TR}/{file}",Table(TR,new Dictionary<string,object>{["upgrade_key"]="R",["base_unit_group"]="G",["target_unit_group"]="G",["resource_cost"]="C"})),
            ($"db/{TL}/{file}",Table(TL,new Dictionary<string,object>{["child_key"]="G",["parent_key"]="G"})),
            ($"db/{TabFileNaming.Cats}/author",Table(TabFileNaming.Cats,new(){["category"]=tab},new(){["category"]="SKV"})));
        await _backend.OpenPackAsync(path);_backend.Pack!.ProjectKey="Yukino";_backend.ProjectKey="Yukino";return path;
    }
    [Theory]
    [InlineData("EMP2","Yukino_Upgrade_Emp_II")]
    [InlineData("EMP_III","Yukino_Upgrade_EMPIII")]
    [InlineData("EMP3","Yukino_Upgrade_EMP_III")]
    [InlineData("EMP2","author_knights")]
    public async Task ExistingAuthorFilesAreReusedByActualOwnershipAndRomanAliases(string tab,string file)
    {
        var path=await TabNamingFixture(tab,file);
        var original=File.ReadAllBytes(path);_backend.BuildWarbandJson();Assert.Equal(original,File.ReadAllBytes(path));Assert.True(_backend.Edits.IsEmpty);
        _backend.Edit(()=>{_backend.AddUnitToGroup("N","u",100,200,true,tab);return true;});
        await _backend.SaveInPlaceAsync();
        using var pack=PackArchive.Open(path);
        foreach(var table in new[]{TG,TJ,TI,TR,TL}) Assert.Equal(file,Assert.Single(TableFiles.EntriesFor(pack,table)).Path.Split('/')[^1]);
        Assert.Equal(2,TableFiles.ReadComposite(pack,TJ,["unit","unit_group"],_schema)!.Rows.Count);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualFileNameUndoSaveReopenAndSharedFilesStaySafe(bool shared)
    {
        const string old="Yukino_Upgrade_Emp_II";
        var path=await TabNamingFixture(shared:shared);
        _backend.Edit(()=>{_backend.RenameTab("EMP2","EMP2","EMP_n");return true;});
        Assert.True(_backend.Undo());Assert.Empty(_backend.Edits.TabFileNames);Assert.True(_backend.Redo());
        _backend.Edit(()=>{_backend.AddUnitToGroup("N","u",100,200,true,"EMP2");return true;});
        await _backend.SaveInPlaceAsync();await _backend.OpenPackAsync(path);
        using var pack=PackArchive.Open(path);
        Assert.Equal("EMP_n",TabFileBindings.Read(pack)["EMP2"]);
        foreach(var table in new[]{TG,TJ,TI})
        {Assert.NotNull(pack.Find($"db/{table}/EMP_n"));Assert.Equal(shared,pack.Find($"db/{table}/{old}") is not null);}
        Assert.Equal(shared?3:2,TableFiles.ReadComposite(pack,TJ,["unit","unit_group"],_schema)!.Rows.Count);
        Assert.Equal("db/"+TG+"/EMP_n",new TabResolver(pack,new(),_schema).PathForTab(TG,"EMP2"));
    }
    [Fact]
    public async Task LegacyProjectAndRecoveredTableBuffersDoNotRecreateEmpbAfterContinuousEdits()
    {
        var path=await TabNamingFixture();
        var project=Path.Combine(_root,"old-project");var info=ProjectStore.Ensure(project);info.ProjectKey="Yukino";ProjectStore.Save(project,info);_backend.Pack!.ProjectDirectory=project;
        // A pre-upgrade buffer contains an extra generated file, but the original author table still exists.
        _backend.Edits.TableReplacements[$"db/{TG}/Yukino_Upgrade_EMPB"]=Table(TG,new Dictionary<string,object>{["unit_group"]="OLD_PENDING"});
        _backend.Edits.TableReplacements[$"db/{TI}/Yukino_Upgrade_EMPB"]=Table(TI,new Dictionary<string,object>{["unit_upgrade_group"]="OLD_PENDING",["category"]="EMP2"});
        _backend.Edits.TableReplacements[$"db/{TJ}/Yukino_Upgrade_EMPB"]=Table(TJ,new Dictionary<string,object>{["unit"]="u",["unit_group"]="OLD_PENDING"});
        var state=RecoveryState();
        var json=JsonNode.Parse(JsonSerializer.Serialize(state,UpdateRecovery.Json))!;
        foreach(var item in json["Packs"]!.AsArray())item!["Edits"]!.AsObject().Remove("TabFileNames");
        state=JsonSerializer.Deserialize<RecoverySession>(json.ToJsonString(),UpdateRecovery.Json)!;
        var manifest=UpdateRecovery.Save(RecoveryRoot,RecoveryInstall,state);
        var loaded=UpdateRecovery.LoadPending(RecoveryRoot,RecoveryInstall)!;
        var saved=Assert.Single(loaded.Session.Packs.Where(p=>p.OriginalPath==path));var restored=UpdateRecovery.ResolvePack(loaded,saved,out var copy);
        _backend.RestoreUpdatePack(saved,restored,copy);
        Assert.DoesNotContain(_backend.Pack!.Files,p=>p.Path.EndsWith("Yukino_Upgrade_EMPB",StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(_backend.Edits.TableReplacements.Keys,p=>p.EndsWith("Yukino_Upgrade_EMPB",StringComparison.OrdinalIgnoreCase));
        _backend.Edit(()=>{_backend.AddUnitToGroup("N","u",100,200,true,"EMP2");return true;});
        Assert.True(_backend.Undo());Assert.True(_backend.Redo());
        _backend.Edit(()=>{_backend.RemoveGroup("N");return true;});Assert.True(_backend.Undo());
        await _backend.SaveInPlaceAsync();await _backend.OpenPackAsync(path);
        using var pack=PackArchive.Open(path);
        foreach(var table in new[]{TG,TJ,TI})
        {Assert.Null(pack.Find($"db/{table}/Yukino_Upgrade_EMPB"));Assert.Single(TableFiles.EntriesFor(pack,table));}
        Assert.Equal(3,TableFiles.ReadComposite(pack,TJ,["unit","unit_group"],_schema)!.Rows.Count);
        UpdateRecovery.Acknowledge(RecoveryRoot,RecoveryInstall,manifest);
    }
    [Fact]
    public async Task AmbiguousAuthorFilesRequireExplicitChoiceAndDoNotGuess()
    {
        var path=await TabNamingFixture(file:"author_a");
        _backend.Edits.TableReplacements[$"db/{TG}/author_b"]=Table(TG,new Dictionary<string,object>{["unit_group"]="G"});
        using var pack=PackArchive.Open(path);
        var res=new TabResolver(pack,_backend.Edits,_schema,_backend.Edits.TableReplacements);
        Assert.Throws<InvalidDataException>(()=>res.PathForTab(TG,"EMP2"));
        _backend.Edits.TabFileNames["EMP2"]="author_a";
        Assert.Equal($"db/{TG}/author_a",new TabResolver(pack,_backend.Edits,_schema,_backend.Edits.TableReplacements).PathForTab(TG,"EMP2"));
    }
    [Theory]
    [InlineData("../author")]
    [InlineData("EMP2")]
    [InlineData("")]
    public void UnsafeManualNamesAreRejected(string name)=>Assert.Throws<ArgumentException>(()=>TabFileBindings.Validate(name));

    [Fact]
    public async Task PrepopulatedProjectionBuffersDoNotResurrectNormalizedOldFile()
    {
        var path=await TabNamingFixture();
        _backend.Edits.TableReplacements[$"db/{TI}/Yukino_Upgrade_EMPB"]=Table(TI,new Dictionary<string,object>{["unit_upgrade_group"]="G",["category"]="EMP2",["x"]=321});
        var snapshot=_backend.Edits.Clone();var bytes=new Dictionary<string,byte[]>(snapshot.TableReplacements);
        using var pack=PackArchive.Open(path);
        WarbandAmender.Apply(pack,null,snapshot,_schema,bytes,[]);
        Assert.DoesNotContain($"db/{TI}/Yukino_Upgrade_EMPB",bytes.Keys);
        var infos=DbTable.Decode(bytes[$"db/{TI}/Yukino_Upgrade_Emp_II"],TI,_schema);
        Assert.Equal("321",infos.Rows[0][infos.Columns.FindIndex(c=>c.Name=="x")].ToTsv());
    }

    [Fact]
    public async Task OpeningAndRepeatedBrowsingNeverProducesFilesOrEdits()
    {
        var path=await TabNamingFixture();var original=File.ReadAllBytes(path);
        for(var i=0;i<3;i++)
        {
            _backend.BuildWarbandJson();_backend.EditedFiles();_backend.PendingNewFiles();
            foreach(var table in new[]{TG,TJ,TI,TR,TL}) _backend.ReadTable($"db/{table}/Yukino_Upgrade_Emp_II");
            _backend.InvalidateCanvas();
        }
        Assert.True(_backend.Edits.IsEmpty);Assert.False(_backend.Undo());
        Assert.Empty(_backend.PendingNewFiles());Assert.Equal(original,File.ReadAllBytes(path));
        Assert.DoesNotContain(_backend.Pack!.Files,p=>p.Path.Contains("EMPB",StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ChainedTabRenameAndFileNameSurviveTableEditUndoRecoveryAndSave()
    {
        var path=await TabNamingFixture();
        _backend.Edit(()=>{_backend.RenameTab("EMP2","EMP_III","EMP_first");return true;});
        _backend.Edit(()=>{_backend.RenameTab("EMP_III","EMP_IV","EMP_final");return true;});
        var t=_backend.ReadTable($"db/{TI}/EMP_final");
        _backend.EditTableCell(path,$"db/{TI}/EMP_final",0,"x",t.Rows[0].Select(v=>v.ToTsv()).ToArray(),"123");
        Assert.Equal("EMP_final",_backend.Edits.TabFileNames["EMP_IV"]);
        Assert.True(_backend.Undo());Assert.True(_backend.Redo());
        var saved=_backend.CaptureUpdateRecovery().Packs.Single(p=>p.OriginalPath==path);
        _backend.RestoreUpdatePack(saved,path,false);
        Assert.Contains($"db/{TI}/EMP_final",_backend.Edits.TableReplacements.Keys);
        await _backend.SaveInPlaceAsync();
        using var pack=PackArchive.Open(path);
        Assert.Equal("EMP_final",TabFileBindings.Read(pack)["EMP_IV"]);
        foreach(var table in new[]{TG,TJ,TI,TR,TL}) Assert.Equal($"db/{table}/EMP_final",Assert.Single(TableFiles.EntriesFor(pack,table)).Path);
        var infos=TableFiles.ReadComposite(pack,TI,["unit_upgrade_group"],_schema)!;
        Assert.Equal("EMP_IV",infos.Rows[0][infos.Columns.FindIndex(c=>c.Name=="category")].ToTsv());
        Assert.Equal("123",infos.Rows[0][infos.Columns.FindIndex(c=>c.Name=="x")].ToTsv());
    }
}
