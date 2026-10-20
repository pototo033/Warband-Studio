using System.Collections;
using System.IO;
using System.Reflection;
using System.Text.Json;
using WarbandStudio.Pack;
using WarbandStudio.Ui.Services;
using WarbandStudio.Ui.ViewModels;

namespace WarbandStudio.Ui.Tests;

public sealed partial class BackendSafetyTests
{
    private string RecoveryRoot => Path.Combine(_root,"recovery");
    private string RecoveryInstall => Path.Combine(_root,"installation");
    private RecoverySession RecoveryState()
    {
        var state=_backend.CaptureUpdateRecovery(); state.OwnerPid=-1; return state;
    }

    [Fact]
    public void RecoverySerializationPreservesEveryEditableCollectionIncludingTuplesAndBytes()
    {
        static object Sample(Type type)
        {
            if(type==typeof(string)) return "test_key";
            if(type==typeof(byte[])) return new byte[]{1,7,255};
            if(type==typeof(string[])) return new[]{"faction_a","faction_b"};
            if(type==typeof(int)) return 7;
            if(type==typeof(long)) return -19L;
            if(type==typeof(double)) return -3.25d;
            if(type.IsGenericType && type.FullName!.StartsWith("System.ValueTuple"))
                return Activator.CreateInstance(type,type.GetGenericArguments().Select(Sample).ToArray())!;
            var ctor=type.GetConstructors().OrderByDescending(c=>c.GetParameters().Length).First();
            return ctor.Invoke(ctor.GetParameters().Select(p=>Sample(p.ParameterType)).ToArray());
        }
        var edits=new WarbandEdits { ProjectKey="ProjectKey" };
        foreach(var property in typeof(WarbandEdits).GetProperties())
        {
            if(property.GetValue(edits) is IDictionary dictionary)
            { var types=property.PropertyType.GetGenericArguments(); dictionary.Add(Sample(types[0]),Sample(types[1])); }
            else if(property.GetValue(edits) is IList list) list.Add(Sample(property.PropertyType.GetGenericArguments()[0]));
        }
        var json=JsonSerializer.Serialize(edits,UpdateRecovery.Json);
        var restored=JsonSerializer.Deserialize<WarbandEdits>(json,UpdateRecovery.Json)!;
        Assert.Equal(json,JsonSerializer.Serialize(restored,UpdateRecovery.Json));
        Assert.Equal(-19,restored.PoolCosts[0].Amount);
        Assert.Equal(new byte[]{1,7,255},restored.TableReplacements["TEST_KEY"]);
    }

    [Fact]
    public void RecoveryCopiesAssetsFromCurrentUndoAndRedoAndLeavesOriginalPackUntouched()
    {
        var original=File.ReadAllBytes(_pack);
        var image=Path.Combine(_root,"temporary.png"); File.WriteAllBytes(image,[1,2,3]);
        _backend.Edit(()=>{_backend.Edits.FileReplacements.Add(("ui/test.png",image)); return true;});
        _backend.Edit(()=>{_backend.Edits.NewTabs.Add(("EMPX","EMP",image,image)); return true;});
        Assert.True(_backend.Undo());
        var state=RecoveryState();
        var manifest=UpdateRecovery.Save(RecoveryRoot,RecoveryInstall,state);
        File.Delete(image);
        var loaded=UpdateRecovery.LoadPending(RecoveryRoot,RecoveryInstall)!;
        var pack=Assert.Single(loaded.Session.Packs);
        Assert.Null(UpdateRecovery.LoadPending(RecoveryRoot,RecoveryInstall)); // Claimed by this restoring process.
        Assert.Equal(new byte[]{1,2,3},File.ReadAllBytes(pack.Edits.FileReplacements[0].Source));
        Assert.Equal(pack.Edits.FileReplacements[0].Source,pack.Redo[0].NewTabs[0].BgSource);
        Assert.Equal(original,File.ReadAllBytes(_pack));
        UpdateRecovery.Acknowledge(RecoveryRoot,RecoveryInstall,manifest);
        Assert.Null(UpdateRecovery.LoadPending(RecoveryRoot,RecoveryInstall));
        Assert.True(File.Exists(manifest)); Assert.True(File.Exists(pack.Edits.FileReplacements[0].Source));
    }

    [Fact]
    public void RecoveryTamperAndMissingAssetBlockRestoreAndKeepPendingSnapshot()
    {
        var manifest=UpdateRecovery.Save(RecoveryRoot,RecoveryInstall,RecoveryState());
        var pack=Path.Combine(Path.GetDirectoryName(manifest)!,"packs","000000.pack");
        File.AppendAllText(pack,"corrupted");
        Assert.Throws<InvalidDataException>(()=>UpdateRecovery.LoadPending(RecoveryRoot,RecoveryInstall));
        Assert.Throws<IOException>(()=>UpdateRecovery.Save(RecoveryRoot,RecoveryInstall,RecoveryState()));
        Assert.True(File.Exists(manifest));
    }

    [Fact]
    public void RecoveryDoesNotConsumeSnapshotOfAnotherRunningInstance()
    {
        var state=_backend.CaptureUpdateRecovery();
        UpdateRecovery.Save(RecoveryRoot,RecoveryInstall,state);
        Assert.Null(UpdateRecovery.LoadPending(RecoveryRoot,RecoveryInstall));
        Assert.Throws<IOException>(()=>UpdateRecovery.Save(RecoveryRoot,RecoveryInstall,state));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedOrMissingOriginalUsesBaselineCopyWithoutOverwritingExternalChanges(bool missing)
    {
        var original=File.ReadAllBytes(_pack);
        UpdateRecovery.Save(RecoveryRoot,RecoveryInstall,RecoveryState());
        await _backend.DisposeAsync();
        if(missing) File.Delete(_pack); else File.WriteAllText(_pack,"external change");
        var loaded=UpdateRecovery.LoadPending(RecoveryRoot,RecoveryInstall)!;
        var path=UpdateRecovery.ResolvePack(loaded,loaded.Session.Packs[0],out var copy);
        Assert.True(copy); Assert.NotEqual(_pack,path); Assert.Equal(original,File.ReadAllBytes(path));
        File.WriteAllText(path,"subsequent save to recovered copy");
        var baseline=Path.Combine(Path.GetDirectoryName(loaded.Manifest)!,loaded.Session.Packs[0].Baseline);
        Assert.Equal(original,File.ReadAllBytes(baseline));
        if(missing) Assert.False(File.Exists(_pack)); else Assert.Equal("external change",File.ReadAllText(_pack));
    }

    [Fact]
    public async Task UpdateRestartRestoresMultiplePacksTableDocumentPendingEditsAndUndoRedo()
    {
        var original=File.ReadAllBytes(_pack);
        _backend.Edit(()=>{_backend.Edits.TableReplacements["db/resource_costs_tables/author"]=Table(Cost,new Dictionary<string,object>{["id"]="C",["treasury_cost"]=-111L}); return true;});
        _backend.Edit(()=>{_backend.Edits.AddCost["C"]=-222; return true;});
        _backend.Edit(()=>{_backend.Edits.AddCost["C"]=-333; return true;});
        Assert.True(_backend.Undo());
        var second=MakePack("second.pack"); await _backend.OpenPackAsync(second);
        _backend.Edit(()=>{_backend.Edits.AddCost["C"]=-777; return true;});
        var state=RecoveryState();
        state.Documents=[new(_pack,"db/resource_costs_tables/author")];
        state.ActiveDocument=state.Documents[0];
        UpdateRecovery.Save(RecoveryRoot,RecoveryInstall,state);
        await _backend.DisposeAsync(); // Simulate exit; a second live process must not hold the target open.
        var vm=new MainViewModel(_settings,ValidateFixturePack);
        var backend=(Backend)typeof(MainViewModel).GetField("_backend",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(vm)!;
        await using var lifetime=backend;
        await vm.RestoreUpdateRecoveryAsync(RecoveryRoot,RecoveryInstall);
        Assert.Contains("更新恢复完成",vm.Status);
        Assert.Equal(2,backend.Packs.Count);
        Assert.Equal(_pack,Assert.IsType<TableTab>(vm.ActiveCenterTab).PackPath);
        Assert.Single(backend.Edits.TableReplacements);
        Assert.Equal(original,File.ReadAllBytes(_pack));
        Assert.Equal(-222,backend.Edits.AddCost["C"]);
        Assert.True(backend.Redo()); Assert.Equal(-333,backend.Edits.AddCost["C"]);
        Assert.True(backend.Undo()); Assert.Equal(-222,backend.Edits.AddCost["C"]);
        backend.SwitchTo(backend.Packs.Single(p=>p.PackPath==second));
        Assert.Equal(-777,backend.Edits.AddCost["C"]);
        Assert.Null(UpdateRecovery.LoadPending(RecoveryRoot,RecoveryInstall));
        await backend.SaveInPlaceAsync(); Assert.Equal(0,backend.EditCount);
    }

    [Fact]
    public void MissingExternalAssetPreventsPublishingRecoveryPointer()
    {
        _backend.Edits.FileReplacements.Add(("ui/test.png",Path.Combine(_root,"missing.png")));
        Assert.Throws<FileNotFoundException>(()=>UpdateRecovery.Save(RecoveryRoot,RecoveryInstall,RecoveryState()));
        Assert.Null(UpdateRecovery.LoadPending(RecoveryRoot,RecoveryInstall));
        Assert.Single(_backend.Edits.FileReplacements);
    }
}
