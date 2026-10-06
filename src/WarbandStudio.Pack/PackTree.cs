using WarbandStudio.Rpfm;

namespace WarbandStudio.Pack;

/// <summary>文件树节点（文件夹或文件）。</summary>
public sealed class PackNode
{
    public required string Name { get; init; }
    /// <summary>pack 内的完整路径（文件夹就是路径前缀）。</summary>
    public required string Path { get; init; }
    public bool IsFolder { get; init; }
    public RFileInfo? File { get; init; }
    public List<PackNode> Children { get; } = [];

    /// <summary>自己或任一后代是战帮相关（决定默认展开/高亮）。</summary>
    public bool WarbandRelated { get; set; }
}

/// <summary>把 rpfm 给的文件清单拼成树：先建目录索引，再递归转成节点。</summary>
public static class PackTree
{
    private sealed class Dir
    {
        public string Name = "";
        public string Path = "";
        public readonly Dictionary<string, Dir> Dirs = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<RFileInfo> Files = [];
    }

    public static List<PackNode> Build(IEnumerable<RFileInfo> files)
    {
        var root = new Dir();
        foreach (var f in files)
        {
            var cur = root;
            var parts = f.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < parts.Length - 1; i++)
            {
                if (!cur.Dirs.TryGetValue(parts[i], out var next))
                {
                    next = new Dir
                    {
                        Name = parts[i],
                        Path = cur.Path.Length == 0 ? parts[i] : cur.Path + "/" + parts[i],
                    };
                    cur.Dirs[parts[i]] = next;
                }
                cur = next;
            }
            cur.Files.Add(f);
        }

        var nodes = Convert(root);
        MarkWarband(nodes);
        return nodes;
    }

    private static List<PackNode> Convert(Dir dir)
    {
        var list = new List<PackNode>(dir.Dirs.Count + dir.Files.Count);

        foreach (var d in dir.Dirs.Values)
        {
            var node = new PackNode { Name = d.Name, Path = d.Path, IsFolder = true };
            node.Children.AddRange(Convert(d));
            list.Add(node);
        }
        foreach (var f in dir.Files)
        {
            list.Add(new PackNode
            {
                Name = LeafName(f.Path),
                Path = f.Path,
                IsFolder = false,
                File = f,
            });
        }

        list.Sort((a, b) =>
        {
            if (a.IsFolder != b.IsFolder) return a.IsFolder ? -1 : 1;   // 文件夹优先
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
        return list;
    }

    private static string LeafName(string path)
    {
        var i = path.LastIndexOf('/');
        return i < 0 ? path : path[(i + 1)..];
    }

    /// <summary>自底向上标"战帮相关"。返回这棵子树里有没有战帮相关的东西。</summary>
    private static bool MarkWarband(List<PackNode> nodes)
    {
        var any = false;
        foreach (var n in nodes)
        {
            var kids = n.Children.Count > 0 && MarkWarband(n.Children);
            n.WarbandRelated = kids || PackSession.IsWarbandRelated(n.Path);
            any |= n.WarbandRelated;
        }
        return any;
    }
}
