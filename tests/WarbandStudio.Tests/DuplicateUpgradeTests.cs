using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using WarbandStudio.Pack;
using WarbandStudio.Packfile;
using WarbandStudio.Rpfm;

namespace WarbandStudio.Tests;

public sealed class DuplicateUpgradeTests : IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"studio_duplicate_"+Guid.NewGuid().ToString("N"));
    private readonly Schema _schema=Schema.Load(RpfmCli.LocateSchema()!);
    private const string Groups=TabFileNaming.Groups,Junc=TabFileNaming.Junc,Infos=TabFileNaming.Infos,Routes=TabFileNaming.Routes;
    private const string Cost="resource_costs_tables",Link="resource_cost_pooled_resource_junctions_tables";
    private const string Ui="ui/campaign ui/warband_upgrades.twui.xml";
    private byte[] Table(string name,params Dictionary<string,object>[] rows)
    {
        var needed=rows.SelectMany(r=>r.Keys).ToHashSet();
        // 最小游戏模型使用含实际测试字段的版本；新版 schema 中同表历史版本不能混着造行。
        var def=_schema.DefinitionsFor(name).Where(d=>needed.All(k=>d.Fields.Any(f=>f.Name==k))).OrderByDescending(d=>d.Version).First();
        var raw=rows.Select(values=>def.Fields.Select(field=>
        {
            values.TryGetValue(field.Name,out var value);
            return field.Type switch
            {
                SchemaFieldType.Boolean=>DbValue.Of(value is not null&&Convert.ToBoolean(value)),
                SchemaFieldType.F32 or SchemaFieldType.F64=>DbValue.Of(value is null?0:Convert.ToDouble(value)),
                SchemaFieldType.StringU8 or SchemaFieldType.StringU16 or SchemaFieldType.OptionalStringU8 or SchemaFieldType.OptionalStringU16=>DbValue.OfStr(value?.ToString()??""),
                SchemaFieldType.SequenceU16=>DbValue.OfBytes(new byte[2]),SchemaFieldType.SequenceU32=>DbValue.OfBytes(new byte[4]),
                _=>DbValue.Of(value is null?0L:Convert.ToInt64(value))
            };
        }).ToArray()).ToList();
        return DbEncoder.Encode(new DbTable{TableName=name,Version=def.Version,Guid=Guid.NewGuid().ToString(),Definition=def,Columns=Schema.ProcessedColumns(def),Rows=raw,RawRows=raw});
    }
    private static Dictionary<string,object> D(params (string Key,object Value)[] values)=>values.ToDictionary(p=>p.Key,p=>p.Value);
    private string File(string name)=>Path.Combine(_root,name);
    private static string Xml(bool separate)
    {
        var settings=new TabFactionVisibility.Settings();
        if(separate){settings.Tabs["ORIG"]=["f1"];settings.Tabs["COPY"]=["f2"];}
        var doc=new XDocument(new XElement("root",new XComment("WarbandStudio.TabFactionVisibility.v1:"+Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(settings)))));
        void Component(string guid,params (string Callback,string Context,string Expression)[] callbacks)=>doc.Root!.Add(new XElement("component",new XAttribute("id",guid),new XAttribute("this",guid),new XElement("callbacks",callbacks.Select(c=>new XElement("callback_with_context",new XAttribute("callback_id",c.Callback),new XAttribute("context_object_id",c.Context),new XAttribute("context_function_id",c.Expression))))));
        const string tx="CcoCampaignResourceTransaction",pool="CcoCampaignPooledResourceTransaction",unit="CcoMainUnitRecord";
        foreach(var guid in new[]{"1327510D-0F47-4163-A5895007FC2C62F1","0EF9609C-AF47-4D87-9ADED8135CE9E866"})Component(guid,("ContextVisibilitySetter",tx,"true"));
        foreach(var guid in new[]{"AC66D87F-E4D5-4535-BE42079C8D1BE917","B9425855-B760-4503-8AD0CBC1AA1F98B8"})Component(guid,("ContextTextLabel",tx,"Value"));
        foreach(var guid in new[]{"2A6EE4EE-97F8-4B03-A68D80D2D5AA60AD","C569EF2A-9F8C-429E-A3B80C5C44A44CAC"})Component(guid,("ContextList",tx,"PooledResourceTransactionList"));
        foreach(var guid in new[]{"ABB3BF43-6107-4F7E-871182361426AB7D","67DE1D18-4503-4182-A9CFD41AEEB45643"})Component(guid,("ContextTextLabel",pool,"Value"),("ContextTooltipSetter",pool,"Name"));
        Component("CD87E96C-7C16-4ACA-B181479669F835DE",("ContextInactiveStateSetter",unit,"true"),("ContextCommandLeftClick",unit,"Component(\"B10C28DC-AFC1-4907-B20296FD966B63E2\").UpgradingUnitsContextList.ForEach(UpgradeUnit(this))"));
        return doc.ToString(SaveOptions.DisableFormatting);
    }
    private string Fixture(bool separate=true,bool sameCost=false,bool multi=false)
    {
        Directory.CreateDirectory(_root);
        PackWriter.WriteNew(File("vanilla.pack"),new[]
        {
            ("db/pooled_resources_tables/data",Table("pooled_resources_tables",D(("key","old_resource"),("scope","FACTION"),("optional_icon_path","icon_old.png")))),
            ("db/pooled_resource_factors_tables/data",Table("pooled_resource_factors_tables",D(("key","other")))),
            ("db/pooled_resource_factor_junctions_tables/data",Table("pooled_resource_factor_junctions_tables",D(("unique_id","old_binding"),("factor","other"),("resource","old_resource")))),
            ($"db/{Cost}/data",Table(Cost,D(("id","template_cost"),("treasury_cost",0)))),
            ($"db/{Link}/data",Table(Link,D(("pooled_resource_factor","old_binding"),("resource_cost","template_cost"),("amount",-1),("context","absolute"),("ui_resource_transaction_pooled_resource","default")))),
            ("db/campaign_groups_tables/data",Table("campaign_groups_tables",D(("id","old_group")))),
            ("db/campaign_group_members_tables/data",Table("campaign_group_members_tables",D(("id","old_member"),("group","old_group")))),
            ("db/campaign_group_member_criteria_factions_tables/data",Table("campaign_group_member_criteria_factions_tables",D(("member","old_member"),("faction","f1"),("context","ACTOR")))),
            ("db/campaign_group_pooled_resources_tables/data",Table("campaign_group_pooled_resources_tables",D(("campaign_group","old_group"),("resource","old_resource")))),
            ("db/factions_tables/data",Table("factions_tables",D(("key","f1"),("subculture","race1")),D(("key","f2"),("subculture","race1"))))
        });
        var junc=new[]{D(("unit_group","a"),("unit","source")),D(("unit_group","b"),("unit","target")),D(("unit_group","x"),("unit","source")),D(("unit_group","y"),("unit","target"))}.ToList();
        if(multi)junc.AddRange([D(("unit_group","a"),("unit","source2")),D(("unit_group","x"),("unit","source2"))]);
        PackWriter.WriteNew(File("source.pack"),new[]
        {
            ($"db/{Groups}/author",Table(Groups,[..new[]{"a","b","x","y"}.Select(g=>D(("unit_group",g)))])),
            ($"db/{Junc}/author",Table(Junc,[..junc])),
            ($"db/{Infos}/author",Table(Infos,[..new[]{"a","b","x","y"}.Select(g=>D(("unit_upgrade_group",g),("category",g is "a" or "b"?"ORIG":"COPY"),("x",20),("y",40)))])),
            ($"db/{Routes}/author",Table(Routes,D(("upgrade_key","r1"),("base_unit_group","a"),("target_unit_group","b"),("resource_cost","c1"),("required_rank",2)),D(("upgrade_key","r2"),("base_unit_group","x"),("target_unit_group","y"),("resource_cost",sameCost?"c1":"c2"),("required_rank",2)))),
            ($"db/{Cost}/author",Table(Cost,D(("id","c1"),("treasury_cost",-100)),D(("id","c2"),("treasury_cost",-500)))),
            (Ui,Encoding.UTF8.GetBytes(Xml(separate)))
        });
        return File("source.pack");
    }
    private Dictionary<string,byte[]> Compile(string path,IReadOnlyDictionary<string,string>? choices=null)
    {
        using var source=DuplicateUpgradeArchive.OpenView(path);using var vanilla=PackArchive.Open(File("vanilla.pack"));
        return DuplicateUpgradeCompiler.Compile(source,vanilla,_schema,new WarbandEdits{ProjectKey="Test"},choices??new Dictionary<string,string>(),[]);
    }
    [Fact]
    public void EmptySamePathOverrideHidesTheWholeLowerFileDuringDuplicateCompilation()
    {
        var path=Fixture(sameCost:true);
        using(var vanilla=PackArchive.Open(File("vanilla.pack")))
            PackWriter.WriteFrom(vanilla,new Dictionary<string,byte[]> {
                [$"db/{Junc}/deleted_lower"]=Table(Junc,D(("unit_group","a"),("unit","unexpected_source"))),
                [$"db/{Junc}/separate_lower"]=Table(Junc,D(("unit_group","a"),("unit","source")))
            },File("vanilla_new.pack"));
        System.IO.File.Move(File("vanilla_new.pack"),File("vanilla.pack"),true);
        using(var authored=PackArchive.Open(path))
            PackWriter.WriteFrom(authored,new Dictionary<string,byte[]> {[$"db/{Junc}/deleted_lower"]=Table(Junc)},File("authored_new.pack"));
        System.IO.File.Move(File("authored_new.pack"),path,true);
        var result=Compile(path);
        Assert.Contains(DuplicateUpgradeArchive.ManifestPath,result.Keys);
    }

    [Fact]
    public async Task EveryVisibleDuplicateUsesClosureAndReopenRestoresAuthoredRowsWithoutEdits()
    {
        var path=Fixture();var original=System.IO.File.ReadAllBytes(path);var bytes=Compile(path);
        Assert.Contains(DuplicateUpgradeArchive.ManifestPath,bytes.Keys);
        using(var source=PackArchive.Open(path))PackWriter.WriteFrom(source,bytes,File("compiled.pack"));
        using(var compiled=PackArchive.Open(File("compiled.pack")))
        {
            var candidates=TableFiles.ReadComposite(compiled,Routes,["upgrade_key"],_schema)!;
            Assert.Equal(4,candidates.Rows.Count);
            var costs=candidates.Rows.Select(r=>r[candidates.Columns.FindIndex(c=>c.Name=="resource_cost")].ToTsv()).Distinct().ToArray();
            var cost=Assert.Single(costs);Assert.StartsWith("studio_du_cost_",cost);
            Assert.Equal(4,candidates.Rows.Select(r=>(r[candidates.Columns.FindIndex(c=>c.Name=="base_unit_group")].ToTsv(),r[candidates.Columns.FindIndex(c=>c.Name=="target_unit_group")].ToTsv())).Distinct().Count());
            var prices=TableFiles.ReadComposite(compiled,Cost,["id"],_schema)!;
            var native=Assert.Single(prices.Rows,r=>r[prices.Columns.FindIndex(c=>c.Name=="id")].ToTsv()==cost);
            Assert.Equal("-100",native[prices.Columns.FindIndex(c=>c.Name=="treasury_cost")].ToTsv());
            using(var source=PackArchive.Open(path))Assert.Equal(source.ReadDecoded(source.Find($"db/{Cost}/author")!),compiled.ReadDecoded(compiled.Find($"db/{Cost}/author")!));
            Assert.Equal(4,TableFiles.ReadComposite(compiled,Junc,["unit","unit_group"],_schema)!.Rows.Count);
            Assert.DoesNotContain(compiled.VisibleEntries,e=>e.Path.Contains("permissions"));
        }
        var session=new PackSession(null);await session.OpenAsync(File("compiled.pack"));
        Assert.Equal(2,TableFiles.ReadComposite(session.Archive!,Routes,["upgrade_key"],_schema)!.Rows.Count);
        Assert.DoesNotContain(session.Files,e=>e.Path.Contains("duplicate_upgrades"));await session.CloseAsync();
        var second=Compile(File("compiled.pack"));
        Assert.True(bytes.Keys.ToHashSet().SetEquals(second.Keys));
        using(var source=DuplicateUpgradeArchive.OpenView(File("compiled.pack")))PackWriter.WriteFrom(source,second,File("again.pack"));
        using(var back=DuplicateUpgradeArchive.OpenView(File("again.pack")))using(var source=PackArchive.Open(path))
            foreach(var entry in source.VisibleEntries)Assert.Equal(source.ReadDecoded(entry),back.ReadDecoded(back.Find(entry.Path)!));
        Assert.Equal(original,System.IO.File.ReadAllBytes(path));
    }
    [Fact]
    public void OverlappingDifferentPricesRequireExplicitChoiceAndInvalidateAfterPriceChange()
    {
        var path=Fixture(false);var original=System.IO.File.ReadAllBytes(path);
        var conflict=Assert.Throws<UpgradeRuleConflictException>(()=>Compile(path));Assert.Single(conflict.Conflicts);
        var choices=conflict.Conflicts.ToDictionary(c=>c.Id,c=>c.Options.Last().Id);
        Assert.NotEmpty(Compile(path,choices));Assert.Equal(original,System.IO.File.ReadAllBytes(path));
        using var source=PackArchive.Open(path);var edits=new Dictionary<string,byte[]>{{ $"db/{Cost}/author",Table(Cost,D(("id","c1"),("treasury_cost",-101)),D(("id","c2"),("treasury_cost",-500))) }};
        PackWriter.WriteFrom(source,edits,File("changed.pack"));Assert.Throws<UpgradeRuleConflictException>(()=>Compile(File("changed.pack"),choices));
    }
    [Fact]
    public void MultiCardGroupsKeepMembershipPositionsAndUnrelatedFiles()
    {
        var path=Fixture(multi:true);var bytes=Compile(path);
        Assert.DoesNotContain($"db/{Junc}/author",bytes.Keys);Assert.DoesNotContain($"db/{Infos}/author",bytes.Keys);
        Assert.Equal(4,DbTable.Decode(bytes[$"db/{Routes}/author"],Routes,_schema).Rows.Count+bytes.Where(p=>p.Key.StartsWith($"db/{Routes}/")&&p.Key!=$"db/{Routes}/author").Sum(p=>DbTable.Decode(p.Value,Routes,_schema).Rows.Count));
    }
    [Fact]
    public void IdenticalRulesNeedNoRuntimeAndDeletedDuplicateRemovesAllDerivedFiles()
    {
        var path=Fixture(false,true);var bytes=Compile(path);
        Assert.DoesNotContain(bytes.Keys,p=>p.StartsWith("script/"));
        using(var source=PackArchive.Open(path))PackWriter.WriteFrom(source,bytes,File("compiled.pack"));
        using var logical=DuplicateUpgradeArchive.OpenView(File("compiled.pack"));
        var change=new Dictionary<string,byte[]>
        {
            [$"db/{Junc}/author"]=Table(Junc,D(("unit_group","a"),("unit","source")),D(("unit_group","b"),("unit","target"))),
            [$"db/{Routes}/author"]=Table(Routes,D(("upgrade_key","r1"),("base_unit_group","a"),("target_unit_group","b"),("resource_cost","c1")))
        };
        PackWriter.WriteFrom(logical,change,File("deleted.pack"));Assert.Empty(Compile(File("deleted.pack")));
    }
    [Fact]
    public void ExternallyChangedCompiledFilesNeverGetSilentlyOverwritten()
    {
        var path=Fixture();var bytes=Compile(path);
        using(var source=PackArchive.Open(path))PackWriter.WriteFrom(source,bytes,File("compiled.pack"));
        using(var source=PackArchive.Open(File("compiled.pack")))PackWriter.WriteFrom(source,new Dictionary<string,byte[]>{{Ui,Encoding.UTF8.GetBytes("<external-edit/>")}},File("external.pack"));
        using var view=DuplicateUpgradeArchive.OpenView(File("external.pack"));
        Assert.Equal("<external-edit/>",Encoding.UTF8.GetString(view.ReadDecoded(view.Find(Ui)!)));
        Assert.Empty(Compile(File("external.pack")));
    }
    [Fact]
    public void ExternalVisibilityEditsRemainInLogicalViewAndSurviveSaveReopen()
    {
        var path=VisibilityFixture();var bytes=Compile(path);
        using(var raw=PackArchive.Open(path))PackWriter.WriteFrom(raw,bytes,File("compiled.pack"));
        var xml=TabFactionVisibility.Apply(Encoding.UTF8.GetString(bytes[Ui]),new Dictionary<string,string[]>(),
            raceEdits:new Dictionary<string,string[]>{{"ORIG",["extra_race"]}});
        using(var raw=PackArchive.Open(File("compiled.pack")))PackWriter.WriteFrom(raw,new Dictionary<string,byte[]>{{Ui,Encoding.UTF8.GetBytes(xml)}},File("external.pack"));
        var untouched=System.IO.File.ReadAllBytes(File("external.pack"));var notes=new List<string>();
        using(var logical=DuplicateUpgradeArchive.OpenView(File("external.pack"),recoveryNotes:notes))
        {
            Assert.Single(notes);
            Assert.Equal(["extra_race"],TabFactionVisibility.ReadRaces(Encoding.UTF8.GetString(logical.ReadDecoded(logical.Find(Ui)!)))["ORIG"]);
            var again=Compile(File("external.pack"));PackWriter.WriteFrom(logical,again,File("resaved.pack"));
            using var reopened=DuplicateUpgradeArchive.OpenView(File("resaved.pack"));
            Assert.Equal(logical.ReadDecoded(logical.Find(Ui)!),reopened.ReadDecoded(reopened.Find(Ui)!));
        }
        Assert.Equal(untouched,System.IO.File.ReadAllBytes(File("external.pack")));
    }
    [Fact]
    public void ExternalUiLayoutAndVisibilityAreBothCurrentSource()
    {
        var path=VisibilityFixture();var bytes=Compile(path);var doc=XDocument.Parse(Encoding.UTF8.GetString(bytes[Ui]),LoadOptions.PreserveWhitespace);
        doc.Root!.SetAttributeValue("comment","external layout edit");
        var xml=TabFactionVisibility.Apply(doc.ToString(SaveOptions.DisableFormatting),new Dictionary<string,string[]>(),raceEdits:new Dictionary<string,string[]>{{"ORIG",["extra_race"]}});
        bytes[Ui]=Encoding.UTF8.GetBytes(xml);
        using(var raw=PackArchive.Open(path))PackWriter.WriteFrom(raw,bytes,File("unsafe.pack"));
        using var view=DuplicateUpgradeArchive.OpenView(File("unsafe.pack"));
        var current=Encoding.UTF8.GetString(view.ReadDecoded(view.Find(Ui)!));
        Assert.Equal("external layout edit",XDocument.Parse(current).Root!.Attribute("comment")!.Value);
        Assert.Equal(["extra_race"],TabFactionVisibility.ReadRaces(current)["ORIG"]);
    }
    private string VisibilityFixture()
    {
        var path=Fixture();using var raw=PackArchive.Open(path);
        var doc=XDocument.Parse(Encoding.UTF8.GetString(raw.ReadDecoded(raw.Find(Ui)!)),LoadOptions.PreserveWhitespace);
        foreach(var key in new[]{"ORIG","COPY"})doc.Root!.Add(new XElement("holder",new XAttribute("id","holder_tab_"+key),new XAttribute("this",key),
            new XElement("callback_with_context",new XAttribute("callback_id","ContextVisibilitySetter"),new XAttribute("context_function_id","HasUpgrades")),
            new XElement("property",new XAttribute("name","God"),new XAttribute("value",key))));
        doc.Root!.Add(new XElement("callback_with_context",new XAttribute("context_object_id","CcoCampaignUnit"),new XAttribute("context_function_id","FirstValidUpgradeCategoryForUnit(this, Category)")),
            new XElement("callback_with_context",new XAttribute("context_function_id","UnitGroupUpgradesTree(List.Filter(Category == SelectedCategory))")));
        var xml=TabFactionVisibility.Apply(doc.ToString(SaveOptions.DisableFormatting),new Dictionary<string,string[]>());
        PackWriter.WriteFrom(raw,new Dictionary<string,byte[]>{{Ui,Encoding.UTF8.GetBytes(xml)}},File("visible-source.pack"));return File("visible-source.pack");
    }
    private byte[] Rewrite(DbTable t,List<DbValue[]> rows)=>DbEncoder.Encode(new DbTable
    {
        TableName=t.TableName,Version=t.Version,Guid=t.Guid,MysteriousByte=t.MysteriousByte,
        Definition=t.Definition,Columns=t.Columns,Rows=[],RawRows=rows
    });
    [Fact]
    public void ExternalRouteAddsDeletesAndRanksDoNotRestoreInternalCostsOrLoseEdits()
    {
        var path=Fixture();var bytes=Compile(path);var p=$"db/{Routes}/author";
        var t=DbTable.Decode(bytes[p],Routes,_schema);var key=t.Definition.Fields.FindIndex(f=>f.Name=="upgrade_key");
        var rank=t.Definition.Fields.FindIndex(f=>f.Name=="required_rank");var cost=t.Definition.Fields.FindIndex(f=>f.Name=="resource_cost");
        var rows=t.RawRows.Where(r=>r[key].ToTsv()!="r2").Select(r=>r.ToArray()).ToList();rows.Single(r=>r[key].ToTsv()=="r1")[rank]=DbValue.Of(6L);
        using(var raw=PackArchive.Open(path))
        {
            var original=DbTable.Decode(raw.ReadDecoded(raw.Find(p)!),Routes,_schema).RawRows[0].ToArray();
            original[key]=DbValue.OfStr("new_route");original[cost]=DbValue.OfStr("c2");rows.Add(original);
            bytes[p]=Rewrite(t,rows);PackWriter.WriteFrom(raw,bytes,File("external.pack"));
        }
        using var view=DuplicateUpgradeArchive.OpenView(File("external.pack"));
        var result=TableFiles.ReadComposite(view,Routes,["upgrade_key"],_schema)!;
        var rk=result.Columns.FindIndex(c=>c.Name=="upgrade_key");var rc=result.Columns.FindIndex(c=>c.Name=="resource_cost");var rr=result.Columns.FindIndex(c=>c.Name=="required_rank");
        Assert.Equal(2,result.Rows.Count);Assert.DoesNotContain(result.Rows,r=>r[rk].ToTsv()=="r2");
        var first=result.Rows.Single(r=>r[rk].ToTsv()=="r1");Assert.Equal(6,first[rr].Int);Assert.Equal("c1",first[rc].ToTsv());
        Assert.Equal("c2",result.Rows.Single(r=>r[rk].ToTsv()=="new_route")[rc].ToTsv());
    }
    [Fact]
    public void ExternalCostAmountsUseBothColumnsOfCompositeKey()
    {
        var path=Fixture();var p=$"db/{Link}/zzzz_studio_edits";
        using(var raw=PackArchive.Open(path))PackWriter.WriteFrom(raw,new Dictionary<string,byte[]>{{p,Table(Link,
            D(("pooled_resource_factor","old_binding"),("resource_cost","c1"),("amount",-200),("context","absolute")),
            D(("pooled_resource_factor","old_binding"),("resource_cost","c2"),("amount",-400),("context","absolute")))}},File("costs.pack"));
        var bytes=Compile(File("costs.pack"));Assert.Contains(p,bytes.Keys);var t=DbTable.Decode(bytes[p],Link,_schema);
        var cost=t.Definition.Fields.FindIndex(f=>f.Name=="resource_cost");var amount=t.Definition.Fields.FindIndex(f=>f.Name=="amount");
        var rows=t.RawRows.Select(r=>r.ToArray()).ToList();rows.Single(r=>r[cost].ToTsv()=="c1")[amount]=DbValue.Of(-275L);rows.Single(r=>r[cost].ToTsv()=="c2")[amount]=DbValue.Of(-650L);
        bytes[p]=Rewrite(t,rows);using(var raw=PackArchive.Open(File("costs.pack")))PackWriter.WriteFrom(raw,bytes,File("external.pack"));
        using var view=DuplicateUpgradeArchive.OpenView(File("external.pack"));var result=TableFiles.ReadComposite(view,Link,["pooled_resource_factor","resource_cost"],_schema)!;
        var c=result.Columns.FindIndex(x=>x.Name=="resource_cost");var a=result.Columns.FindIndex(x=>x.Name=="amount");
        Assert.Equal(2,result.Rows.Count);Assert.Equal(-275,result.Rows.Single(r=>r[c].ToTsv()=="c1")[a].Int);Assert.Equal(-650,result.Rows.Single(r=>r[c].ToTsv()=="c2")[a].Int);
    }
    [Fact]
    public void UnusableInternalSnapshotDoesNotLockAValidPack()
    {
        var path=Fixture();var bytes=Compile(path);var manifest=JsonSerializer.Deserialize<DuplicateUpgradeArchive.Manifest>(bytes[DuplicateUpgradeArchive.ManifestPath])!;
        var state=manifest.Files.Values.First(s=>s.Compiled is not null);state.Compiled![^1]^=1;
        bytes[DuplicateUpgradeArchive.ManifestPath]=JsonSerializer.SerializeToUtf8Bytes(manifest);
        using(var raw=PackArchive.Open(path))PackWriter.WriteFrom(raw,bytes,File("corrupt.pack"));
        var before=System.IO.File.ReadAllBytes(File("corrupt.pack"));
        using var view=DuplicateUpgradeArchive.OpenView(File("corrupt.pack"));
        Assert.NotNull(view.Find(Ui));Assert.Equal(before,System.IO.File.ReadAllBytes(File("corrupt.pack")));
    }
    [Theory]
    [InlineData("promote")]
    [InlineData("rank")]
    [InlineData("delete")]
    public void OnlyExplicitAuthorCostsCanPromoteAnEditedGeneratedBridge(string edit)
    {
        var path=Fixture();var bytes=Compile(path);
        var entry=bytes.Where(p=>p.Key.StartsWith($"db/{Routes}/")).Select(p=>(p.Key,Table:DbTable.Decode(p.Value,Routes,_schema)))
            .First(p=>p.Table.RawRows.Any(r=>r[p.Table.Definition.Fields.FindIndex(f=>f.Name=="upgrade_key")].ToTsv().StartsWith("studio_du_bridge_")));
        var t=entry.Table;var key=t.Definition.Fields.FindIndex(f=>f.Name=="upgrade_key");var cost=t.Definition.Fields.FindIndex(f=>f.Name=="resource_cost");
        var rows=t.RawRows.Select(r=>r.ToArray()).ToList();var row=rows.First(r=>r[key].ToTsv().StartsWith("studio_du_bridge_"));var route=row[key].ToTsv();
        if(edit=="promote")row[cost]=DbValue.OfStr("c1");
        else if(edit=="rank")row[t.Definition.Fields.FindIndex(f=>f.Name=="required_rank")]=DbValue.Of(8L);
        else rows.Remove(row);
        bytes[entry.Key]=Rewrite(t,rows);using(var raw=PackArchive.Open(path))PackWriter.WriteFrom(raw,bytes,File("external.pack"));
        if(edit!="promote")
        {
            using var current=DuplicateUpgradeArchive.OpenView(File("external.pack"));using var disk=PackArchive.Open(File("external.pack"));
            foreach(var file in disk.VisibleEntries)Assert.Equal(disk.ReadDecoded(file),current.ReadDecoded(current.Find(file.Path)!));
            Assert.Empty(Compile(File("external.pack")));return;
        }
        using var view=DuplicateUpgradeArchive.OpenView(File("external.pack"));var merged=TableFiles.ReadComposite(view,Routes,["upgrade_key"],_schema)!;
        var k=merged.Columns.FindIndex(c=>c.Name=="upgrade_key");var c=merged.Columns.FindIndex(c=>c.Name=="resource_cost");
        Assert.Equal("c1",merged.Rows.Single(r=>r[k].ToTsv()==route)[c].ToTsv());
    }
    [Fact]
    public void ExternallyDeletedAuthorFileIsNeverResurrectedFromInternalNotes()
    {
        var path=Fixture();var bytes=Compile(path);using(var raw=PackArchive.Open(path))PackWriter.WriteFrom(raw,bytes,File("compiled.pack"));
        var p=$"db/{Routes}/author";
        using(var raw=PackArchive.Open(File("compiled.pack")))PackWriter.WriteFrom(raw,new Dictionary<string,byte[]>(),File("deleted-current.pack"),dropPaths:[p]);
        using var view=DuplicateUpgradeArchive.OpenView(File("deleted-current.pack"));Assert.Null(view.Find(p));
    }
    [Fact]
    public void RuleSelectionsParticipateInPersistenceAndClone()
    {
        var edits=new WarbandEdits();edits.DuplicateUpgradeChoices["conflict"]="r1";
        Assert.False(edits.IsEmpty);var clone=edits.Clone();clone.DuplicateUpgradeChoices["conflict"]="r2";
        Assert.Equal("r1",edits.DuplicateUpgradeChoices["conflict"]);
        var restored=JsonSerializer.Deserialize<WarbandEdits>(JsonSerializer.Serialize(edits),new JsonSerializerOptions{PreferredObjectCreationHandling=System.Text.Json.Serialization.JsonObjectCreationHandling.Populate});
        Assert.Equal("r1",restored!.DuplicateUpgradeChoices["conflict"]);
    }
    [Fact]
    public void TechnologyIsComparedByTechnologyKeyAndCopiedToEveryNativeCandidate()
    {
        var path=Fixture(false,true);const string tech="unit_upgrade_to_tech_requirements_tables";
        using(var source=PackArchive.Open(path))PackWriter.WriteFrom(source,new Dictionary<string,byte[]>{{ $"db/{tech}/author",Table(tech,D(("unit_upgrade","r1"),("technology","technology_a")),D(("unit_upgrade","r2"),("technology","technology_a"))) }},File("tech.pack"));
        var bytes=Compile(File("tech.pack"));
        using(var source=PackArchive.Open(File("tech.pack")))PackWriter.WriteFrom(source,bytes,File("tech_compiled.pack"));
        using var compiled=PackArchive.Open(File("tech_compiled.pack"));
        var rows=TableFiles.ReadComposite(compiled,tech,["unit_upgrade","technology"],_schema)!;
        Assert.Equal(4,rows.Rows.Count);Assert.All(rows.Rows,r=>Assert.Equal("technology_a",r[rows.Columns.FindIndex(c=>c.Name=="technology")].ToTsv()));
    }
    [Fact]
    public void PartlyOverlappingGroupsCannotSpreadOneUnitsFactionRuleToAnotherUnit()
    {
        var path=Fixture(multi:true);
        using(var source=PackArchive.Open(path))PackWriter.WriteFrom(source,new Dictionary<string,byte[]>{{ $"db/{Junc}/author",Table(Junc,D(("unit_group","a"),("unit","source")),D(("unit_group","a"),("unit","source2")),D(("unit_group","b"),("unit","target")),D(("unit_group","x"),("unit","source")),D(("unit_group","y"),("unit","target"))) }},File("partial.pack"));
        var error=Assert.Throws<InvalidDataException>(()=>Compile(File("partial.pack")));
        Assert.Contains("部分重叠",error.Message);
    }
    public void Dispose(){if(Directory.Exists(_root))Directory.Delete(_root,true);}
}
