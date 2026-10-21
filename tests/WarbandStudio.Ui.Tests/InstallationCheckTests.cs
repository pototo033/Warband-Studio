using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using WarbandStudio.Ui.Services;

namespace WarbandStudio.Ui.Tests;

public sealed class InstallationCheckTests : IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"studio_install_check_"+Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string,string> _files=new();
    public InstallationCheckTests()
    {
        foreach(var name in new[]{"WarbandStudio.exe","WarbandStudio.dll","WarbandStudio.Core.dll","WarbandStudio.Pack.dll","WarbandStudio.Packfile.dll","WarbandStudio.Rpfm.dll","WarbandStudio.Editors.dll","WarbandStudio.deps.json","WarbandStudio.runtimeconfig.json","Web/canvas.html","schemas/schema_wh3.ron","update_repo.txt"})
        {
            Write(name,name);
            _files[name]=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(_root,name))));
        }
        Manifest();
    }
    private void Write(string name,string value) { var p=Path.Combine(_root,name);Directory.CreateDirectory(Path.GetDirectoryName(p)!);File.WriteAllText(p,value); }
    private void Manifest(string version="1.7.5")=>Write(InstallationCheck.ManifestName,JsonSerializer.Serialize(new InstallationManifest(1,version,_files)));

    [Fact]
    public void ValidInstallQuarantinesOnlyObsoleteProgramFilesAndIsRepeatable()
    {
        Write("WarbandStudio.Ui.dll","old");Write("Web/legacy.js","old");
        foreach(var name in new[]{"packs/user.pack","project.json","recovery/pending.json","custom.dll","rpfm/rpfm_cli.exe","Web/background.png"})Write(name,"user");
        InstallationCheck.Verify(_root,"1.7.5");InstallationCheck.Verify(_root,"1.7.5");
        Assert.False(File.Exists(Path.Combine(_root,"WarbandStudio.Ui.dll")));
        Assert.False(File.Exists(Path.Combine(_root,"Web/legacy.js")));
        Assert.Equal(2,Directory.GetFiles(Path.Combine(_root,".file-quarantine"),"*",SearchOption.AllDirectories).Length);
        foreach(var name in new[]{"packs/user.pack","project.json","recovery/pending.json","custom.dll","rpfm/rpfm_cli.exe","Web/background.png"})Assert.Equal("user",File.ReadAllText(Path.Combine(_root,name)));
    }
    [Theory]
    [InlineData("WarbandStudio.dll")]
    [InlineData("WarbandStudio.Pack.dll")]
    [InlineData("Web/canvas.html")]
    [InlineData("schemas/schema_wh3.ron")]
    public void MixedFilesBlockBeforeAnyCleanup(string name)
    {
        Write(name,"old");Write("WarbandStudio.Ui.dll","retain");
        Assert.Throws<InvalidDataException>(()=>InstallationCheck.Verify(_root,"1.7.5"));
        Assert.True(File.Exists(Path.Combine(_root,"WarbandStudio.Ui.dll")));
    }
    [Fact]
    public void MissingFileManifestAndWrongVersionBlock()
    {
        Assert.Throws<InvalidDataException>(()=>InstallationCheck.Verify(_root,"1.7.4"));
        File.Delete(Path.Combine(_root,"Web/canvas.html"));
        Assert.Throws<InvalidDataException>(()=>InstallationCheck.Verify(_root,"1.7.5"));
        File.Delete(Path.Combine(_root,InstallationCheck.ManifestName));
        Assert.Throws<InvalidDataException>(()=>InstallationCheck.Verify(_root,"1.7.5"));
        InstallationCheck.Verify(_root,"1.7.5",false);
    }
    [Fact]
    public void UntrustedManifestCannotTargetUserDataOrOutsideRoot()
    {
        _files["../WarbandStudio.Other.dll"]=new string('0',64);Manifest();
        Assert.Throws<InvalidDataException>(()=>InstallationCheck.Verify(_root,"1.7.5"));
        _files.Remove("../WarbandStudio.Other.dll");_files["packs/user.pack"]=new string('0',64);Manifest();
        Assert.Throws<InvalidDataException>(()=>InstallationCheck.Verify(_root,"1.7.5"));
    }
    public void Dispose(){Directory.Delete(_root,true);}
}
