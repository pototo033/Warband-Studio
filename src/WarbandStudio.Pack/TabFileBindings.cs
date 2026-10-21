using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WarbandStudio.Packfile;

namespace WarbandStudio.Pack;

public static class TabFileBindings
{
    public const string MetadataPath="warband_studio/tab_files.json";
    public static string Validate(string name)
    {
        name=name.Trim();
        if(!Regex.IsMatch(name,@"^[\p{L}\p{N}_-]+$") || char.IsAsciiDigit(name[^1]))
            throw new ArgumentException("落表文件名只允许字母、数字、下划线、短横线，且不能以数字结尾；例如 EMP_n 或 EMP_III。");
        return name;
    }
    public static Dictionary<string,string> Read(PackArchive pack)
    {
        var entry=pack.Find(MetadataPath);
        if(entry is null) return new(StringComparer.OrdinalIgnoreCase);
        var saved=JsonSerializer.Deserialize<Dictionary<string,string>>(pack.ReadDecoded(entry))??throw new InvalidDataException("页签落表设置损坏。");
        return saved.ToDictionary(p=>p.Key,p=>Validate(p.Value),StringComparer.OrdinalIgnoreCase);
    }
    public static Dictionary<string,string> Effective(PackArchive pack,WarbandEdits edits)
    {
        var names=Read(pack);
        foreach(var (oldKey,newKey) in edits.TabRenames)
            if(names.Remove(oldKey,out var name)) names[newKey]=name;
        foreach(var (tab,name) in edits.TabFileNames) names[tab]=Validate(name);
        return names;
    }
    public static byte[] Encode(Dictionary<string,string> names)=>Encoding.UTF8.GetBytes(JsonSerializer.Serialize(names));
}

public sealed partial class TabResolver
{
    private readonly Dictionary<string,HashSet<string>> _ownerCache=new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string,string[]> _candidateCache=new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string,string> _pathCache=new(StringComparer.OrdinalIgnoreCase);
    private string CurrentTab(string tab)
    {
        foreach(var (oldKey,newKey) in _e.TabRenames)
            if(tab.Equals(oldKey,StringComparison.OrdinalIgnoreCase)) tab=newKey;
        return tab;
    }
    public HashSet<string> Owners(string table,string path)
    {
        if(_ownerCache.TryGetValue(path,out var cached)) return cached;
        var owners=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if(Bytes(path) is not { } bytes) return owners;
        var t=DbTable.Decode(bytes,table,_schema);
        string Cell(DbValue[] row,string field){var c=t.Columns.FindIndex(c=>c.Name==field);return c<0 ? "":row[c].ToTsv();}
        foreach(var row in t.Rows)
        {
            var tab=table==TabFileNaming.Infos ? Cell(row,"category") : "";
            if(table!=TabFileNaming.Infos)
            {
                var group=Cell(row,table switch{TabFileNaming.Groups or TabFileNaming.Junc=>"unit_group",TabFileNaming.Links=>"child_key",TabFileNaming.Routes=>"base_unit_group",_=>""});
                tab=InfoCats().GetValueOrDefault(group)??TabOf(group)??"";
            }
            owners.Add(tab.Length==0 ? "?":CurrentTab(tab));
        }
        return _ownerCache[path]=owners;
    }
    private static string Similar(string name)
    {
        var at=name.LastIndexOf("_Upgrade_",StringComparison.OrdinalIgnoreCase);
        if(at>=0) name=name[(at+9)..];
        name=Regex.Replace(name,"[_-]","").ToUpperInvariant();
        var m=Regex.Match(name,@"^(.*?)(\d+|VIII|VII|III|VI|IV|IX|II|V|I)$");
        if(m.Success)
        {
            var suffix=m.Groups[2].Value;
            var roman=new Dictionary<string,int>{{"I",1},{"II",2},{"III",3},{"IV",4},{"V",5},{"VI",6},{"VII",7},{"VIII",8},{"IX",9}};
            if(int.TryParse(suffix,out var n)||roman.TryGetValue(suffix,out n)) return m.Groups[1].Value+"#"+n;
        }
        return name;
    }
    public string[] Candidates(string table,string tab)
    {
        tab=CurrentTab(tab);
        var cacheKey=table+"\u0001"+tab;
        if(_candidateCache.TryGetValue(cacheKey,out var cached)) return cached;
        var paths=PathsOf(table).Select(p=>p.Replace('\\','/')).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var owned=paths.Where(p=>Owners(table,p).SetEquals([tab])).ToArray();
        var named=paths.Where(p=>Similar(p.Split('/')[^1])==Similar(tab) && Owners(table,p).All(o=>o=="?"||o.Equals(tab,StringComparison.OrdinalIgnoreCase))).ToArray();
        var preferred=owned.Intersect(named,StringComparer.OrdinalIgnoreCase).ToArray();
        return _candidateCache[cacheKey]=preferred.Length>0 ? preferred : owned.Length>0 ? owned : named;
    }
    private string ResolvePath(string table,string? tab)
    {
        var cacheKey=table+"\u0001"+tab;
        if(_pathCache.TryGetValue(cacheKey,out var path)) return path;
        return _pathCache[cacheKey]=ResolvePathCore(table,tab);
    }
    private string ResolvePathCore(string table,string? tab)
    {
        if(string.IsNullOrWhiteSpace(tab)) return Canonical(table,TabFileNaming.PathOf(table,_e.ProjectKey,tab));
        tab=CurrentTab(tab);
        if(TabFileBindings.Effective(_pack,_e).TryGetValue(tab,out var name))
        {
            var path=Canonical(table,$"db/{table}/{name}");
            if(PathsOf(table).Any(p=>p.Equals(path,StringComparison.OrdinalIgnoreCase)) && Owners(table,path).Any(o=>o!="?"&&!o.Equals(tab,StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException($"落表文件 {name} 已属于其他页签，请换一个文件名。");
            return path;
        }
        var candidates=Candidates(table,tab);
        if(candidates.Length==1) return candidates[0];
        if(candidates.Length>1) throw new InvalidDataException($"页签 {tab} 在 {table} 有多个文件候选：{string.Join("、",candidates.Select(p=>p.Split('/')[^1]))}。请在页签重命名中指定落表文件名。");
        // 同一页签另一张内容表的作者文件名也可用于尚未有行的表。
        var siblings=new[]{TabFileNaming.Infos,TabFileNaming.Groups,TabFileNaming.Junc,TabFileNaming.Routes,TabFileNaming.Links}
            .Where(t=>t!=table).SelectMany(t=>Candidates(t,tab)).Select(p=>p.Split('/')[^1]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if(siblings.Length==1) return Canonical(table,$"db/{table}/{siblings[0]}");
        if(siblings.Length>1) throw new InvalidDataException($"页签 {tab} 的已有文件名不唯一：{string.Join("、",siblings)}。请在页签重命名中指定落表文件名。");
        return Canonical(table,TabFileNaming.PathOf(table,_e.ProjectKey,tab));
    }
}
