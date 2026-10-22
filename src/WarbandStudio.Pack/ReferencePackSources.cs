using WarbandStudio.Packfile;

namespace WarbandStudio.Pack;

public static class ReferencePackSources
{
    // 显式加载的兵种包与声明依赖使用同一套来源，避免加兵与保存判定不一致。
    public static IReadOnlyList<string> Resolve(string current, string? vanilla, IEnumerable<string> unitPacks)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFullPath(current) };
        if (vanilla is not null) seen.Add(Path.GetFullPath(vanilla));
        var result = new List<string>();
        var queue = new Queue<string>(); queue.Enqueue(current);
        void Add(string path)
        {
            path = Path.GetFullPath(path);
            if (!seen.Add(path)) return;
            if (!File.Exists(path)) throw new FileNotFoundException("找不到已加载的引用包：" + path);
            result.Add(path); queue.Enqueue(path);
        }
        foreach (var path in unitPacks) Add(path);
        while (queue.TryDequeue(out var path))
        {
            using var pack = PackArchive.Open(path);
            foreach (var dependency in pack.Dependencies)
            {
                var name = Path.GetFileName(dependency.Replace('\\', '/'));
                var candidates = new[] { Path.Combine(Path.GetDirectoryName(path)!, name),
                    Path.Combine(Path.GetDirectoryName(vanilla ?? current)!, name) };
                var found = candidates.FirstOrDefault(File.Exists);
                if (found is not null) Add(found);
            }
        }
        return result;
    }
}
