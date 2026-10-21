using WarbandStudio.Packfile;

namespace WarbandStudio.Pack;

public static class TabFileBindingOperations
{
    private static readonly string[] Tables=[TabFileNaming.Groups,TabFileNaming.Junc,TabFileNaming.Infos,TabFileNaming.Links,TabFileNaming.Routes];
    public static void NormalizeRecovered(PackArchive pack,WarbandEdits edits,Schema schema,List<string> notes)
    {
        if(edits.TableReplacements.Count==0) return;
        using var original=PackArchive.Open(pack.FilePath);
        foreach(var (path,bytes) in edits.TableReplacements.ToArray())
        {
            var parts=path.Replace('\\','/').Split('/');
            if(parts.Length!=3||parts[0]!="db"||!Tables.Contains(parts[1])||original.Find(path) is not null||edits.RemoveFiles.Contains(path,StringComparer.OrdinalIgnoreCase)) continue;
            var resolver=new TabResolver(original,edits,schema,edits.TableReplacements);
            var owners=resolver.Owners(parts[1],path);
            if(owners.Count!=1||owners.Contains("?")) continue;
            if(TabFileBindings.Effective(original,edits).TryGetValue(owners.Single(),out var chosen)
                && chosen.Equals(parts[2],StringComparison.OrdinalIgnoreCase)) continue;
            var targets=resolver.Candidates(parts[1],owners.Single()).Where(p=>original.Find(p) is not null).ToArray();
            if(targets.Length!=1) continue;
            var target=targets[0];
            var previous=edits.TableReplacements.GetValueOrDefault(target)??original.ReadDecoded(original.Find(target)!);
            edits.TableReplacements[target]=Merge(previous,bytes,parts[1],schema,true);
            edits.TableReplacements.Remove(path);
            notes.Add($"旧修改缓存归并：{path} → {target}（组合键合并，保留待保存值）。");
        }
    }
    public static void ApplyNames(PackArchive pack,WarbandEdits edits,Schema schema,Dictionary<string,byte[]> repl,List<string> notes)
    {
        foreach(var (tab,name) in edits.TabFileNames)
        {
            TabFileBindings.Validate(name);
            var resolver=new TabResolver(pack,edits,schema,repl);
            foreach(var table in Tables)
            {
                var target=resolver.PathForTab(table,tab); // 同名但属于其他页签则拒绝。
                var paths=TableFiles.EntriesFor(pack,table).Select(e=>e.Path).Concat(repl.Keys.Where(p=>p.StartsWith($"db/{table}/",StringComparison.OrdinalIgnoreCase)))
                    .Distinct(StringComparer.OrdinalIgnoreCase).Where(p=>!edits.RemoveFiles.Contains(p,StringComparer.OrdinalIgnoreCase)).ToArray();
                foreach(var path in paths)
                {
                    if(path.Equals(target,StringComparison.OrdinalIgnoreCase)||!resolver.Owners(table,path).SetEquals([tab])) continue;
                    byte[]? Read(string p)=>repl.GetValueOrDefault(p)??(pack.Find(p) is { } entry ? pack.ReadDecoded(entry):null);
                    var bytes=Read(path)!;
                    repl[target]=Read(target) is { } existing ? Merge(existing,bytes,table,schema,false):bytes;
                    repl.Remove(path);edits.TableReplacements.Remove(path);
                    if(!edits.RemoveFiles.Contains(path,StringComparer.OrdinalIgnoreCase)) edits.RemoveFiles.Add(path);
                    notes.Add($"页签独占文件改名：{path} → {target}（共享文件保留）。");
                }
            }
        }
    }
    private static byte[] Merge(byte[] destination,byte[] source,string table,Schema schema,bool pendingWins)
    {
        var a=DbTable.Decode(destination,table,schema);var b=DbTable.Decode(source,table,schema);
        if(a.Version!=b.Version || !a.Definition.Fields.Select(f=>(f.Name,f.Type)).SequenceEqual(b.Definition.Fields.Select(f=>(f.Name,f.Type))))
            throw new InvalidDataException($"{table} 的候选文件定义不同，无法安全归并。");
        var keys=table switch
        {
            TabFileNaming.Groups=>new[]{"unit_group"},TabFileNaming.Junc=>["unit","unit_group"],TabFileNaming.Infos=>["unit_upgrade_group"],
            TabFileNaming.Routes=>["upgrade_key"],TabFileNaming.Links=>["child_key","parent_key"],_=>throw new InvalidDataException(table)
        };
        var columns=keys.Select(k=>a.Definition.Fields.FindIndex(f=>f.Name==k)).ToArray();
        if(columns.Any(c=>c<0)) throw new InvalidDataException("缺少行身份列："+table);
        var rows=new List<DbValue[]>();var at=new Dictionary<string,int>(StringComparer.Ordinal);
        string Key(DbValue[] r)=>string.Join("\u0001",columns.Select(c=>r[c].ToTsv()));
        byte[] Encode(List<DbValue[]> raw)=>DbEncoder.Encode(new DbTable{TableName=table,Version=a.Version,Guid=a.Guid,MysteriousByte=a.MysteriousByte,Definition=a.Definition,Columns=a.Columns,Rows=raw,RawRows=raw});
        foreach(var row in a.RawRows.Concat(b.RawRows))
        {
            var key=Key(row);
            if(at.TryGetValue(key,out var i))
            {
                if(!pendingWins && !Encode([rows[i]]).SequenceEqual(Encode([row]))) throw new InvalidDataException($"{table} 的同键行内容不同，文件改名已停止：{key}。请先处理冲突。");
                rows[i]=row;
            }
            else {at[key]=rows.Count;rows.Add(row);}
        }
        return Encode(rows);
    }
}
