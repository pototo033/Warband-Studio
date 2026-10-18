using System.Text;

namespace WarbandStudio.Pack;

/// <summary>先写同目录临时文件，再原子替换；失败时保留原文件。</summary>
public static class AtomicFile
{
    public static void Flush(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        stream.Flush(flushToDisk: true);
    }
    public static void Commit(string stagedPath, string destination)
    {
        if (!Path.GetPathRoot(Path.GetFullPath(stagedPath))!.Equals(Path.GetPathRoot(Path.GetFullPath(destination)), StringComparison.OrdinalIgnoreCase))
            throw new IOException("原子替换要求临时文件和目标位于同一卷。");
        if (File.Exists(destination)) File.Replace(stagedPath, destination, null);
        else File.Move(stagedPath, destination);
    }

    public static void WriteText(string destination, string text)
    {
        var temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(Encoding.UTF8.GetBytes(text));
                stream.Flush(flushToDisk: true);
            }
            Commit(temp, destination);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static void Copy(string source, string destination)
    {
        var staged = destination + ".copying." + Guid.NewGuid().ToString("N");
        try { File.Copy(source, staged); Flush(staged); Commit(staged, destination); }
        finally { if (File.Exists(staged)) File.Delete(staged); }
    }
}
