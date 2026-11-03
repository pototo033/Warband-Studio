using System.Text;
using System.Text.Json;
using WarbandStudio.Pack;
using WarbandStudio.Packfile;
using WarbandStudio.Rpfm;

namespace WarbandStudio.Probe;

public static class DuplicateUpgrades
{
    public static int AttachBaseline(string[] args)
    {
        if (args.Length != 4) throw new ArgumentException("duplicate-upgrades-baseline <edited.pack> <verified-baseline.pack> <new-output.pack>");
        var dest = Path.GetFullPath(args[3]);
        if (File.Exists(dest) || dest.Equals(Path.GetFullPath(args[1]), StringComparison.OrdinalIgnoreCase))
            throw new IOException("恢复基准输出必须是新文件，不能覆盖输入。");
        using var raw = PackArchive.Open(args[1]);
        using var baseline = PackArchive.Open(args[2]);
        var manifest = DuplicateUpgradeArchive.Read(raw) ?? throw new InvalidDataException("输入缺少兼容清单。");
        var trusted = DuplicateUpgradeArchive.Read(baseline) ?? throw new InvalidDataException("基准缺少兼容清单。");
        if (manifest.Namespace != trusted.Namespace) throw new InvalidDataException("兼容身份不一致，不能使用此基准。");
        var attached = 0;
        foreach (var (path, state) in manifest.Files.ToArray())
        {
            var entry = baseline.Find(path) ?? throw new InvalidDataException("基准缺少文件：" + path);
            var bytes = baseline.ReadDecoded(entry);
            if (DuplicateUpgradeArchive.Hash(bytes) != state.CompiledHash || !trusted.Files.TryGetValue(path, out var prior) ||
                !(state.Original is null ? prior.Original is null : prior.Original is not null && state.Original.SequenceEqual(prior.Original)))
                throw new InvalidDataException("基准与原编译快照不一致：" + path);
            if (path.StartsWith("db/", StringComparison.OrdinalIgnoreCase) && state.Compiled is null)
            {
                manifest.Files[path] = state with { Compiled = bytes };
                attached++;
            }
        }
        PackWriter.WriteFrom(raw, new Dictionary<string, byte[]> { [DuplicateUpgradeArchive.ManifestPath] = JsonSerializer.SerializeToUtf8Bytes(manifest) }, dest);
        using var output = PackArchive.Open(dest);
        if (!raw.VisibleEntries.Select(e => e.Path).ToHashSet().SetEquals(output.VisibleEntries.Select(e => e.Path)))
            throw new InvalidDataException("补充基准时改变了文件集合。");
        foreach (var entry in raw.VisibleEntries.Where(e => e.Path != DuplicateUpgradeArchive.ManifestPath))
            if (!raw.ReadDecoded(entry).SequenceEqual(output.ReadDecoded(output.Find(entry.Path)!)))
                throw new InvalidDataException("补充基准改变了用户文件：" + entry.Path);
        var notes = new List<string>();
        using var view = DuplicateUpgradeArchive.OpenView(dest, recoveryNotes: notes);
        foreach (var note in notes) Console.WriteLine(note);
        Console.WriteLine($"PASS {attached} 个经过哈希与作者快照验证的DB基准；所有游戏文件逐字节不变；恢复 {notes.Count} 个外部编辑文件；SHA256={DuplicateUpgradeArchive.Hash(File.ReadAllBytes(dest))}");
        return 0;
    }

    public static int Run(string[] args)
    {
        if (args.Length < 4) throw new ArgumentException("duplicate-upgrades <输入.pack> <原版db.pack> <输出.pack> [选择.json]");
        if (Path.GetFullPath(args[1]).Equals(Path.GetFullPath(args[3]),StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("探针输出不得覆盖输入。");
        var schema=WarbandStudio.Packfile.Schema.Load(RpfmCli.LocateSchema()!);
        using var input=DuplicateUpgradeArchive.OpenView(args[1]); using var vanilla=PackArchive.Open(args[2]);
        var edits=new WarbandEdits { ProjectKey="Yukino" };
        using var raw=PackArchive.Open(args[1]);
        var choices=DuplicateUpgradeArchive.Read(raw)?.Choices ?? new Dictionary<string,string>();
        if(args.Length>4) foreach(var (k,v) in JsonSerializer.Deserialize<Dictionary<string,string>>(File.ReadAllText(args[4]))!) choices[k]=v;
        var notes=new List<string>();
        try
        {
            var bytes=DuplicateUpgradeCompiler.Compile(input,vanilla,schema,edits,choices,notes);
            PackWriter.WriteFrom(input,bytes,args[3]);
            foreach(var (file,content) in bytes.Where(p=>p.Key.StartsWith("script/",StringComparison.Ordinal)&&p.Key.EndsWith(".lua",StringComparison.Ordinal)))
                File.WriteAllBytes(args[3]+"."+Path.GetFileName(file),content);
            using var back=DuplicateUpgradeArchive.OpenView(args[3]);
            if(!back.VisibleEntries.Select(e=>e.Path).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(input.VisibleEntries.Select(e=>e.Path))) throw new Exception("逻辑视图文件集合不一致");
            foreach(var entry in input.VisibleEntries) if(!back.ReadDecoded(back.Find(entry.Path)!).SequenceEqual(input.ReadDecoded(entry))) throw new Exception("逻辑回读不一致："+entry.Path);
            foreach(var note in notes) Console.WriteLine(note);
            Console.WriteLine($"PASS：{bytes.Count} 个编译文件；逻辑视图逐文件字节一致；原包未改。");
            return 0;
        }
        catch(UpgradeRuleConflictException error)
        {
            File.WriteAllText(args[3]+".conflicts.json",JsonSerializer.Serialize(error.Conflicts,new JsonSerializerOptions{WriteIndented=true}),Encoding.UTF8);
            Console.WriteLine($"需要选择 {error.Conflicts.Count} 项冲突；未写包；详情：{args[3]}.conflicts.json");return 2;
        }
    }
}
