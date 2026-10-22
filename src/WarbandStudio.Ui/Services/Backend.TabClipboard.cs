using System.IO;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using WarbandStudio.Pack;
using WarbandStudio.Packfile;

namespace WarbandStudio.Ui.Services;

public sealed partial class Backend
{
    private const string CopyTech="unit_upgrade_to_tech_requirements_tables",CopyBuildings="unit_upgrade_to_building_level_requirements_tables";
    private sealed record PageCopy(string Pack,string Category,Dictionary<string,byte[]> Tables,int Groups,int Routes,int Skipped);
    private PageCopy? _pageCopy;
    private static string[] PageKeys(string table)=>table switch
    {
        TabFileNaming.Groups=>["unit_group"],TabFileNaming.Junc=>["unit","unit_group"],TabFileNaming.Infos=>["unit_upgrade_group"],
        TabFileNaming.Links=>["child_key","parent_key"],TabFileNaming.Routes=>["upgrade_key"],
        CopyTech=>["unit_upgrade","technology"],CopyBuildings=>["unit_upgrade","building_level"],_=>["category"]
    };
    private static int PageColumn(DbTable table,string name)=>table.Definition.Fields.FindIndex(f=>f.Name==name) is var c && c>=0 ? c : throw new InvalidDataException($"{table.TableName} 缺少 {name}");
    private static string PageCell(DbTable table,DbValue[] row,string name)=>row[PageColumn(table,name)].ToTsv();
    private static byte[] PageBytes(DbTable template,List<DbValue[]> rows,bool fresh=false)=>DbEncoder.Encode(new DbTable
    {
        TableName=template.TableName,Version=template.Version,Guid=fresh ? Guid.NewGuid().ToString():template.Guid,
        MysteriousByte=template.MysteriousByte,Definition=template.Definition,Columns=template.Columns,Rows=rows,RawRows=rows
    });
    private JsonNode? PageClipboardJson()=>_pageCopy is { } copy ? new JsonObject
    {
        ["category"]=copy.Category,["groups"]=copy.Groups,["routes"]=copy.Routes,["skipped"]=copy.Skipped,
        ["canPaste"]=string.Equals(copy.Pack,_pack?.PackPath,StringComparison.OrdinalIgnoreCase)
    }:null;

    public string CopyTab(string category)
    {
        lock(StateSync)
        {
            RequireEditablePack(_pack?.PackPath??"");
            var (bytes,snapshot)=ProjectTables();
            using var view=PackArchive.OpenView(_pack!.PackPath!,bytes,snapshot.RemoveFiles);
            var schema=GetSchema();
            DbTable? Read(string table)
            {
                var notes=new List<string>();var result=TableFiles.ReadComposite(view,table,PageKeys(table),schema,notes);
                if(notes.Count>0)throw new InvalidDataException("复制已停止，不能跳过部分源文件："+string.Join("；",notes));
                if(result is not null)foreach(var key in PageKeys(table))PageColumn(result,key);
                return result;
            }
            var infos=Read(TabFileNaming.Infos)??throw new InvalidOperationException("当前页没有兵组坐标。");
            var groupKeys=infos.RawRows.Where(r=>PageCell(infos,r,"category").Equals(category,StringComparison.OrdinalIgnoreCase))
                .Select(r=>PageCell(infos,r,"unit_upgrade_group")).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if(groupKeys.Count==0) throw new InvalidOperationException("当前页没有可复制的兵组。");
            var groups=Read(TabFileNaming.Groups);
            if(groups is null || !groupKeys.IsSubsetOf(groups.RawRows.Select(r=>PageCell(groups,r,"unit_group"))))
            {
                using var vanilla=VanillaDbPackPath is { } path && File.Exists(path) ? PackArchive.Open(path):null;
                var baseGroups=vanilla is null ? null:TableFiles.ReadComposite(vanilla,TabFileNaming.Groups,PageKeys(TabFileNaming.Groups),schema);
                if(baseGroups is not null && groups is not null && !baseGroups.Definition.Fields.Select(f=>(f.Name,f.Type)).SequenceEqual(groups.Definition.Fields.Select(f=>(f.Name,f.Type))))
                    throw new InvalidDataException("源包与原版兵组定义不同，复制已停止。");
                groups=TableFiles.MergeComposite(baseGroups,groups,PageKeys(TabFileNaming.Groups));
            }
            if(groups is null || !groupKeys.IsSubsetOf(groups.RawRows.Select(r=>PageCell(groups,r,"unit_group"))))
                throw new InvalidDataException("当前页引用了缺失的兵组，复制已停止。");
            var routes=Read(TabFileNaming.Routes);
            bool Internal(DbTable t,DbValue[] r,string a,string b)=>groupKeys.Contains(PageCell(t,r,a))&&groupKeys.Contains(PageCell(t,r,b));
            var routeKeys=routes?.RawRows.Where(r=>Internal(routes,r,"base_unit_group","target_unit_group"))
                .Select(r=>PageCell(routes,r,"upgrade_key")).ToHashSet(StringComparer.OrdinalIgnoreCase)??[];
            var skipped=routes?.RawRows.Count(r=>groupKeys.Contains(PageCell(routes,r,"base_unit_group"))!=groupKeys.Contains(PageCell(routes,r,"target_unit_group")))??0;
            var tables=new Dictionary<string,byte[]>();
            void Capture(DbTable? t,Func<DbValue[],bool> select)
            {
                if(t is null)return;var rows=t.RawRows.Where(select).Select(r=>r.ToArray()).ToList();
                if(rows.Count>0)tables[t.TableName]=PageBytes(t,rows);
            }
            Capture(groups,r=>groupKeys.Contains(PageCell(groups,r,"unit_group")));
            Capture(infos,r=>groupKeys.Contains(PageCell(infos,r,"unit_upgrade_group")));
            var junc=Read(TabFileNaming.Junc);Capture(junc,r=>groupKeys.Contains(PageCell(junc!,r,"unit_group")));
            Capture(routes,r=>routeKeys.Contains(PageCell(routes!,r,"upgrade_key")));
            var links=Read(TabFileNaming.Links);Capture(links,r=>Internal(links!,r,"child_key","parent_key"));
            foreach(var table in new[]{CopyTech,CopyBuildings})
            {var t=Read(table);Capture(t,r=>routeKeys.Contains(PageCell(t!,r,"unit_upgrade")));}
            _pageCopy=new(_pack.PackPath!,category,tables,groupKeys.Count,routeKeys.Count,skipped);
            InvalidateCanvas();
            return $"已复制 {category}：{groupKeys.Count} 个组、{routeKeys.Count} 条页内升级；跳过 {skipped} 条跨页路线。到目标页签右键粘贴；不复制或补齐派系授权。";
        }
    }

    public string PasteTab(string category)=>Edit(()=>
    {
        RequireEditablePack(_pack?.PackPath??"");
        var copy=_pageCopy??throw new InvalidOperationException("请先复制一个页签。");
        if(!copy.Pack.Equals(_pack!.PackPath,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("请在复制来源的同一个 Pack 内粘贴。");
        var (bytes,snapshot)=ProjectTables();var schema=GetSchema();
        using var view=PackArchive.OpenView(_pack.PackPath!,bytes,snapshot.RemoveFiles);
        var cats=TableFiles.ReadComposite(view,TabFileNaming.Cats,["category"],schema);
        if(cats is null || !cats.RawRows.Any(r=>PageCell(cats,r,"category").Equals(category,StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("目标页签不存在，请先创建或打开目标页签。");
        var source=copy.Tables.ToDictionary(p=>p.Key,p=>DbTable.Decode(p.Value,p.Key,schema));
        HashSet<string> Taken(string table,string column,IEnumerable<string> removed)
        {
            var taken=new HashSet<string>(removed,StringComparer.OrdinalIgnoreCase);
            foreach(var archive in new[]{_pack.Archive!,view})
                foreach(var entry in TableFiles.EntriesFor(archive,table))
                {var t=DbTable.Decode(archive.ReadDecoded(entry),table,schema);foreach(var r in t.RawRows)taken.Add(PageCell(t,r,column));}
            return taken;
        }
        var takenGroups=Taken(TabFileNaming.Groups,"unit_group",snapshot.RemoveGroup);
        var takenRoutes=Taken(TabFileNaming.Routes,"upgrade_key",snapshot.RemoveRoute);
        string Unique(string old,HashSet<string> taken)
        {
            var stem=Regex.Replace($"{_pack.ProjectKey??ProjectKey}_{category}_copy_{old}","[^A-Za-z0-9_]","_");
            if(stem.Length>170)stem=stem[..170];var key=stem;var n=2;while(!taken.Add(key))key=stem+"_"+n++;return key;
        }
        var groups=source[TabFileNaming.Groups];
        var groupMap=groups.RawRows.Select(r=>PageCell(groups,r,"unit_group")).ToDictionary(k=>k,k=>Unique(k,takenGroups),StringComparer.OrdinalIgnoreCase);
        var routeMap=source.TryGetValue(TabFileNaming.Routes,out var routes) ? routes.RawRows.Select(r=>PageCell(routes,r,"upgrade_key"))
            .ToDictionary(k=>k,k=>Unique(k,takenRoutes),StringComparer.OrdinalIgnoreCase):new(StringComparer.OrdinalIgnoreCase);
        var resolver=new TabResolver(view,snapshot,schema,bytes);
        foreach(var (table,t) in source)
        {
            var rows=t.RawRows.Select(r=>r.ToArray()).ToList();
            foreach(var row in rows)
            {
                void Map(string field,Dictionary<string,string> map){var c=PageColumn(t,field);row[c]=DbValue.OfStr(map[row[c].ToTsv()]);}
                switch(table)
                {
                    case TabFileNaming.Groups:case TabFileNaming.Junc:Map("unit_group",groupMap);break;
                    case TabFileNaming.Infos:Map("unit_upgrade_group",groupMap);row[PageColumn(t,"category")]=DbValue.OfStr(category);break;
                    case TabFileNaming.Links:Map("child_key",groupMap);Map("parent_key",groupMap);break;
                    case TabFileNaming.Routes:Map("upgrade_key",routeMap);Map("base_unit_group",groupMap);Map("target_unit_group",groupMap);break;
                    default:Map("unit_upgrade",routeMap);break;
                }
            }
            var path=table is CopyTech or CopyBuildings ? $"db/{table}/{WarbandAmender.EditFileName}":resolver.PathForTab(table,category);
            var existing=bytes.GetValueOrDefault(path)??(view.Find(path) is { } entry ? view.ReadDecoded(entry):null);
            if(existing is not null)
            {
                var dest=DbTable.Decode(existing,table,schema);
                if(!dest.Definition.Fields.Select(f=>(f.Name,f.Type)).SequenceEqual(t.Definition.Fields.Select(f=>(f.Name,f.Type))))
                    throw new InvalidDataException($"{table} 的字段版本不一致，粘贴已停止。");
                bytes[path]=PageBytes(dest,dest.RawRows.Concat(rows).ToList());
            }
            else bytes[path]=PageBytes(t,rows,true);
        }
        // 仅复制七类页内数据；不调用加兵、军事组/专属授权或精英解锁流程。
        CommitTables(bytes,snapshot);
        return $"已粘贴 {copy.Category} → {category}：{groupMap.Count} 个独立兵组、{routeMap.Count} 条独立升级；位置与连线参数保留，派系授权不变。";
    });
}
