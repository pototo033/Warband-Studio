using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using WarbandStudio.Ui.Services;

namespace WarbandStudio.Ui.Tests;

public sealed class InstallationAuditTests : IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"studio_full_audit_"+Guid.NewGuid().ToString("N"));
    private string Install=>Path.Combine(_root,"install");
    private string State=>Path.Combine(_root,"audit");
    private Dictionary<string,string> Components {get;}=new();
    public InstallationAuditTests()
    {
        foreach(var n in new[]{"rpfm/rpfm_cli.exe","bundled/wuu/source.pack","coreclr.dll","runtimes/win-x64/native/WebView2Loader.dll","zh-Hans/resources.dll"})
        {Write(n,"current");Components[n]=Hash("current");}
        Manifest("1.7.6");
        foreach(var n in new[]{"packs/user.pack","projects/edits.json","recovery/pending.json","settings.json","iconcache/old.png"})Write(n,"preserve");
    }
    private static string Hash(string value)=>Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
    private void Write(string name,string value){var p=Path.Combine(Install,name);Directory.CreateDirectory(Path.GetDirectoryName(p)!);File.WriteAllText(p,value);}
    private void Manifest(string version)=>Write(InstallationCheck.ManifestName,JsonSerializer.Serialize(new InstallationManifest(1,version,new(),Components)));
    private void CheckPreserved()
    {
        foreach(var n in new[]{"packs/user.pack","projects/edits.json","recovery/pending.json","settings.json","iconcache/old.png"})Assert.Equal("preserve",File.ReadAllText(Path.Combine(Install,n)));
    }
    [Fact]
    public void RunsOncePerReleaseAndInstallationAndPreservesAllUserData()
    {
        Write("unlisted.dll","old");
        var report=InstallationAudit.Run(Install,"1.7.6",State)!;
        var before=File.ReadAllBytes(report);
        Assert.Contains("unlisted.dll",File.ReadAllText(report));
        Assert.Equal(report,InstallationAudit.Run(Install,"1.7.6",State));
        Assert.Equal(before,File.ReadAllBytes(report));
        Manifest("1.7.7");
        InstallationAudit.Run(Install,"1.7.7",State);
        Assert.Contains("1.7.7",File.ReadAllText(report));
        CheckPreserved();Assert.True(File.Exists(Path.Combine(Install,"unlisted.dll")));
    }
    [Theory]
    [InlineData("rpfm/rpfm_cli.exe",false)]
    [InlineData("bundled/wuu/source.pack",false)]
    [InlineData("coreclr.dll",false)]
    [InlineData("runtimes/win-x64/native/WebView2Loader.dll",true)]
    public void WrongRetainedComponentsProduceReportAndRetryUntilRepaired(string name,bool missing)
    {
        if(missing)File.Delete(Path.Combine(Install,name));else Write(name,"old");
        Assert.Throws<InvalidDataException>(()=>InstallationAudit.Run(Install,"1.7.6",State));
        Assert.Empty(Directory.GetFiles(State,"*.ready"));
        Assert.Contains(name,File.ReadAllText(Assert.Single(Directory.GetFiles(State,"*.json"))));
        CheckPreserved();Write(name,"current");
        Assert.NotNull(InstallationAudit.Run(Install,"1.7.6",State));Assert.Single(Directory.GetFiles(State,"*.ready"));
    }
    [Fact]
    public void ExternalOverrideIsVerifiedRatherThanSilentlyUsed()
    {
        var external=Path.Combine(_root,"external.exe");File.WriteAllText(external,"old");
        Assert.Throws<InvalidDataException>(()=>InstallationAudit.Run(Install,"1.7.6",State,new(){["RPFM"]=external}));
        CheckPreserved();
    }
    [Fact]
    public void ComponentTraversalCannotReadOutsideInstall()
    {
        Components["rpfm/../../external.exe"]=Hash("old");Manifest("1.7.6");
        Assert.Throws<InvalidDataException>(()=>InstallationAudit.Run(Install,"1.7.6",State));
        CheckPreserved();
    }
    [Fact]
    public void AutomaticBundledDiscoveryIgnoresUnlistedOldTemplatesWithoutDeletingAnyPack()
    {
        Write("bundled/wuu/legacy.pack","old");
        var files=InstallationAudit.PublishedBundledFiles(Path.Combine(Install,"bundled","wuu"),"*.pack",Install).ToArray();
        Assert.Equal("source.pack",Path.GetFileName(Assert.Single(files)));
        Assert.Equal("old",File.ReadAllText(Path.Combine(Install,"bundled/wuu/legacy.pack")));
        CheckPreserved();
    }
    [Fact]
    public void NewVersionGetsNewDerivedCachesWithoutDeletingOldCacheOrRecovery()
    {
        foreach(var kind in new[]{"iconcache","skincache","webview","diagnostics"})
        {
            var old=DerivedCache.PathFor(_root,"1.7.5",kind);Directory.CreateDirectory(old);File.WriteAllText(Path.Combine(old,"keep"),"data");
            Assert.NotEqual(old,DerivedCache.PathFor(_root,"1.7.6",kind));
            Assert.Equal("data",File.ReadAllText(Path.Combine(old,"keep")));
        }
        CheckPreserved();
    }
    public void Dispose()=>Directory.Delete(_root,true);
}
