using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WarbandStudio.Ui.Services;

/// <summary>独立诊断快照；不创建更新恢复指针，不保存 Pack，不消费撤销记录。</summary>
public static class DiagnosticBundle
{
    public static void Export(string destination, RecoverySession snapshot, string logPath, string context, string? schemaPath)
    {
        // 修改的只是深拷贝，素材路径转换不能污染仍在使用的编辑集。
        var state = JsonSerializer.Deserialize<RecoverySession>(JsonSerializer.Serialize(snapshot, UpdateRecovery.Json), UpdateRecovery.Json)!;
        var full = Path.GetFullPath(destination);
        if (!full.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("诊断文件必须使用 .zip 扩展名。");
        if (state.Packs.Any(p => Path.GetFullPath(p.OriginalPath).Equals(full, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("诊断包不能覆盖来源 Pack。");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temp = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        state.Files.Clear();
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create))
            {
                void Text(string path, string text)
                {
                    var bytes = Encoding.UTF8.GetBytes(text);
                    using var stream = zip.CreateEntry(path, CompressionLevel.Optimal).Open(); stream.Write(bytes);
                    state.Files[path] = Convert.ToHexString(SHA256.HashData(bytes));
                }
                void Copy(string source, string relative, bool growingLog = false)
                {
                    if (Path.GetFullPath(source).Equals(full, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("诊断包不能覆盖正在引用的来源文件：" + source);
                    using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    var length = input.Length;
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    using var entry = zip.CreateEntry(relative, CompressionLevel.Optimal).Open();
                    var buffer = new byte[128 * 1024]; long left = length;
                    while (left > 0)
                    {
                        var read = input.Read(buffer, 0, (int)Math.Min(left, buffer.Length));
                        if (read == 0) throw new IOException("导出期间文件缩短：" + source);
                        entry.Write(buffer, 0, read); hash.AppendData(buffer, 0, read); left -= read;
                    }
                    var digest = Convert.ToHexString(hash.GetHashAndReset());
                    if (!growingLog && UpdateRecovery.Hash(source) != digest) throw new IOException("导出期间来源改变，请重试：" + source);
                    state.Files[relative] = digest;
                }
                var assets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                string? Asset(string? source)
                {
                    if (string.IsNullOrWhiteSpace(source)) return source;
                    if (!File.Exists(source))
                    {
                        if (Path.IsPathRooted(source)) throw new FileNotFoundException("待保存素材不存在，诊断快照不完整：", source);
                        return source;
                    }
                    var path = Path.GetFullPath(source);
                    if (!assets.TryGetValue(path, out var relative))
                    {
                        relative = "assets/" + assets.Count.ToString("D6") + Path.GetExtension(path);
                        Copy(path, relative); assets.Add(path, relative);
                    }
                    return relative;
                }
                for (var i = 0; i < state.Packs.Count; i++)
                {
                    var pack = state.Packs[i]; pack.Baseline = $"packs/{i:D6}.pack";
                    Copy(pack.OriginalPath, pack.Baseline);
                    foreach (var edits in new[] { pack.Edits }.Concat(pack.Undo).Concat(pack.Redo))
                    {
                        for (var n = 0; n < edits.FileReplacements.Count; n++)
                        { var x = edits.FileReplacements[n]; edits.FileReplacements[n] = (x.Target, Asset(x.Source)!); }
                        for (var n = 0; n < edits.NewTabs.Count; n++)
                        { var x = edits.NewTabs[n]; edits.NewTabs[n] = (x.Key, x.Donor, Asset(x.BgSource), Asset(x.BtnSource)); }
                    }
                }
                if (File.Exists(logPath)) Copy(logPath, "logs/app.log", growingLog: true);
                else Text("logs/app.log", "导出时 app.log 不存在。\n");
                Text("context.txt", context);
                if (schemaPath is not null) Copy(schemaPath, "schema/schema_wh3.ron");
                Text("README.txt", "WarbandStudio 编辑诊断包（不是可直接加载的游戏 MOD）\n" +
                    "manifest.json 保存当前编辑、撤销/重做、原始路径、项目 key 和各文件 SHA256；packs/ 是原始底本，未提交编辑仍在清单中。\n" +
                    "外部兵种/声明依赖包仅作引用，游戏原版 db.pack 不打入 ZIP；请使用 context.txt 中相同游戏版本和附带 schema。\n" +
                    "开发者复现：解压到独立目录，DiagnosticBundle.ReadExtracted 校验并还原素材路径；先用 Backend.RestoreUpdatePack 加载 IsUnitPack 引用，再加载 ActivePack 的 Baseline 副本。\n" +
                    "不能把底本当成已保存结果；保存失败也能导出本诊断包，不会改动用户的原始 Pack。\n" +
                    "日志和清单包含本机路径及 MOD 内容；发送前可先查看。\n");
                var manifest = JsonSerializer.Serialize(state, new JsonSerializerOptions(UpdateRecovery.Json) { WriteIndented = true });
                using var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open(), new UTF8Encoding(false)); writer.Write(manifest);
            }
            // 成功关闭 ZIP 后才替换用户选择的文件，异常不留下半个诊断包。
            File.Move(temp, full, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static RecoverySession ReadExtracted(string directory)
    {
        var state = JsonSerializer.Deserialize<RecoverySession>(File.ReadAllBytes(UpdatePackage.Under(directory, "manifest.json")), UpdateRecovery.Json)
            ?? throw new InvalidDataException("诊断清单为空。");
        if (state.Format != 1) throw new InvalidDataException("不支持的诊断格式。");
        foreach (var (path, hash) in state.Files)
            if (UpdateRecovery.Hash(UpdatePackage.Under(directory, path)) != hash) throw new InvalidDataException("诊断文件校验失败：" + path);
        string? Asset(string? path) => path is not null && state.Files.ContainsKey(path) ? UpdatePackage.Under(directory, path) : path;
        foreach (var pack in state.Packs)
        {
            if (!state.Files.ContainsKey(pack.Baseline)) throw new InvalidDataException("诊断包缺少底本。");
            foreach (var edits in new[] { pack.Edits }.Concat(pack.Undo).Concat(pack.Redo))
            {
                for (var n = 0; n < edits.FileReplacements.Count; n++)
                { var x = edits.FileReplacements[n]; edits.FileReplacements[n] = (x.Target, Asset(x.Source)!); }
                for (var n = 0; n < edits.NewTabs.Count; n++)
                { var x = edits.NewTabs[n]; edits.NewTabs[n] = (x.Key, x.Donor, Asset(x.BgSource), Asset(x.BtnSource)); }
            }
        }
        return state;
    }
}
