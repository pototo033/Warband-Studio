using WarbandStudio.Pack;
using WarbandStudio.Packfile;

namespace WarbandStudio.Probe;

/// <summary>
/// 把 db 表**文件名结尾的数字换成字母**（RPFM 体检「Table name ends in number」——那个"只有做它的人不崩、
/// 别人一进就崩"的怪问题；1=A、2=B、…、9=I、0=J，连续多个数字整段换）。
/// 纯改名：内容一字不动。改名只在"同一张表多文件"时影响加载顺序，所以**目标名被占用就跳过**并报告，让人来定。
///
/// 用法：probe fix-table-names &lt;pack&gt; [out.pack] [--dry-run]
///   · 不给 out 就写回原文件（先 .prefixnames.bak 备份，写 .saving 再替换）。
/// </summary>
public static class FixTableNames
{
    public static int Run(string[] a)
    {
        string? src = null, dest = null;
        var dry = false;
        for (var i = 1; i < a.Length; i++)
        {
            if (a[i] == "--dry-run") dry = true;
            else if (src is null) src = Path.GetFullPath(a[i]);
            else if (dest is null) dest = Path.GetFullPath(a[i]);
            else { Console.Error.WriteLine("多余的参数：" + a[i]); return 1; }
        }
        if (src is null) { Console.Error.WriteLine("用法: probe fix-table-names <pack> [out.pack] [--dry-run]"); return 1; }
        var inPlace = dest is null;
        var outPath = inPlace ? src + ".saving" : dest;

        var repl = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var drops = new List<string>();
        var renamed = new List<(string Old, string New)>();

        using (var pack = PackArchive.Open(src))
        {
            var byPath = new HashSet<string>(
                pack.VisibleEntries.Select(e => e.Path.Replace('\\', '/')), StringComparer.OrdinalIgnoreCase);
            foreach (var entry in pack.VisibleEntries)
            {
                var path = entry.Path.Replace('\\', '/');
                if (!path.StartsWith("db/", StringComparison.OrdinalIgnoreCase)) continue;
                var slash = path.LastIndexOf('/');
                if (slash < 0) continue;
                var name = path[(slash + 1)..];
                var fixedName = TabFileNaming.NoTrailingDigits(name);
                if (fixedName.Equals(name, StringComparison.Ordinal)) continue;
                var newPath = path[..(slash + 1)] + fixedName;
                if (byPath.Contains(newPath))
                {
                    Console.WriteLine($"[!] 跳过 {path}：目标名 {newPath} 已被占用（改名会动加载顺序，人工决定）");
                    continue;
                }
                repl[newPath] = pack.ReadDecoded(entry);
                drops.Add(path);
                renamed.Add((path, newPath));
            }
            if (renamed.Count == 0) { Console.WriteLine("[i] 没有需要改名的文件"); return 0; }

            foreach (var (o, n2) in renamed) Console.WriteLine($"[改名] {o}  →  {n2}");
            if (dry)
            {
                Console.WriteLine($"[i] --dry-run：{renamed.Count} 个文件会改名（没有落盘）");
                return 0;
            }
            PackWriter.WriteFrom(pack, repl, outPath, dropPaths: drops);
        }

        if (inPlace)
        {
            var bak = src + ".prefixnames.bak";
            if (!File.Exists(bak)) File.Copy(src, bak);
            File.Move(outPath, src, overwrite: true);
            Console.WriteLine($"[i] 备份：{Path.GetFileName(bak)}");
        }

        // 读回校验：新名字在、老名字没了、解码字节一致（新条目写出时不压缩，解出来必须一模一样）
        var bad = 0;
        using (var check = PackArchive.Open(inPlace ? src : outPath))
        {
            foreach (var (o, n2) in renamed)
            {
                var ne = check.Find(n2);
                if (ne is null) { Console.Error.WriteLine($"[!] 新条目不在：{n2}"); bad++; continue; }
                if (check.Find(o) is not null) { Console.Error.WriteLine($"[!] 旧条目还在：{o}"); bad++; }
                else if (!check.ReadDecoded(ne).AsSpan().SequenceEqual(repl[n2])) { Console.Error.WriteLine($"[!] 内容不一致：{n2}"); bad++; }
            }
        }
        Console.WriteLine(bad == 0
            ? $"[✓] 改名完成：{renamed.Count} 个文件（{(inPlace ? src : outPath)}）"
            : $"[!] 读回校验发现 {bad} 个问题");
        return bad == 0 ? 0 : 8;
    }
}
