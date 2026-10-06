using System.Text.Json;

namespace WarbandStudio.Rpfm;

/// <summary>ExtractPackedFiles 的返回值：落地目录 + 实际导出的文件列表。</summary>
public sealed record ExtractResult(string DestPath, IReadOnlyList<string> Files);

/// <summary>
/// 按 5.0.6 源码里的 docs/server/ws-commands.md 封装的常用命令。
/// 只包了工具真正要用的那些；其余用 <see cref="RpfmClient.SendAsync"/> 直接发。
/// </summary>
public sealed partial class RpfmClient
{
    // ── 会话 / 游戏 ──────────────────────────────────────────────

    /// <summary>选游戏；rebuild=true 时会顺带重建依赖缓存（首次会慢，之后走缓存）。</summary>
    public Task<JsonElement> SetGameAsync(string gameKey = RpfmEnums.GameWarhammer3,
                                          bool rebuild = false,
                                          CancellationToken ct = default) =>
        SendAsync(RpfmProtocol.Tuple("SetGameSelected", gameKey, rebuild), ct);

    public Task<JsonElement> GenerateDependenciesCacheAsync(CancellationToken ct = default) =>
        SendAsync("GenerateDependenciesCache", ct);

    /// <summary>打开全部原版 CA pack，合并成一个只读容器（原版 db 的来源）。</summary>
    public Task<JsonElement> LoadAllCaPacksAsync(CancellationToken ct = default) =>
        SendAsync("LoadAllCAPackFiles", ct);

    // ── Pack ─────────────────────────────────────────────────────

    public Task<JsonElement> ListOpenPacksAsync(CancellationToken ct = default) =>
        SendAsync("ListOpenPacks", ct);

    /// <summary>打开一个 .pack（多个路径会合并进同一容器），返回 pack key + 元数据。</summary>
    public async Task<(string PackKey, ContainerInfo Info)> OpenPackAsync(
        string packPath, CancellationToken ct = default)
    {
        var data = await SendAsync(RpfmProtocol.Newtype("OpenPackFiles", new[] { packPath }), ct);
        var t = data.GetProperty("StringContainerInfo");
        var key = t[0].GetString() ?? throw new RpfmException("OpenPackFiles 没返回 pack key。");
        var info = t[1].Deserialize<ContainerInfo>(RpfmProtocol.Json) ?? new ContainerInfo();
        return (key, info);
    }

    public Task<JsonElement> ClosePackAsync(string packKey, CancellationToken ct = default) =>
        SendAsync(RpfmProtocol.Newtype("ClosePack", packKey), ct);

    public Task<JsonElement> CloseAllPacksAsync(CancellationToken ct = default) =>
        SendAsync("CloseAllPacks", ct);

    public async Task<ContainerInfo> SavePackAsync(string packKey, CancellationToken ct = default)
    {
        var data = await SendAsync(RpfmProtocol.Newtype("SavePack", packKey), ct);
        return data.GetProperty("ContainerInfo").Deserialize<ContainerInfo>(RpfmProtocol.Json)
               ?? new ContainerInfo();
    }

    public async Task<ContainerInfo> SavePackAsAsync(string packKey, string destPath,
                                                     CancellationToken ct = default)
    {
        var data = await SendAsync(RpfmProtocol.Tuple("SavePackAs", packKey, destPath), ct);
        return data.GetProperty("ContainerInfo").Deserialize<ContainerInfo>(RpfmProtocol.Json)
               ?? new ContainerInfo();
    }

    // ── 文件 ─────────────────────────────────────────────────────

    /// <summary>文件树（左侧栏的数据源）：容器元数据 + 全部文件元数据。</summary>
    public async Task<(ContainerInfo Info, List<RFileInfo> Files)> GetTreeAsync(
        string packKey, CancellationToken ct = default)
    {
        var data = await SendAsync(RpfmProtocol.Newtype("GetPackFileDataForTreeView", packKey), ct);
        var t = data.GetProperty("ContainerInfoVecRFileInfo");
        var info = t[0].Deserialize<ContainerInfo>(RpfmProtocol.Json) ?? new ContainerInfo();
        var files = t[1].Deserialize<List<RFileInfo>>(RpfmProtocol.Json) ?? new();
        return (info, files);
    }

    public Task<JsonElement> GetPackedFilesInfoAsync(string packKey, IEnumerable<string> paths,
                                                     CancellationToken ct = default) =>
        SendAsync(RpfmProtocol.Tuple("GetPackedFilesInfo", packKey, paths.ToArray()), ct);

    /// <summary>把 pack 内文件导出到磁盘；asTsv=true 时表会写成 TSV（双表头，与 4.7.4 一致）。</summary>
    public async Task<ExtractResult> ExtractAsync(string packKey, IEnumerable<string> files,
                                                  string destPath, bool asTsv = false,
                                                  CancellationToken ct = default)
    {
        var bySource = new Dictionary<string, object>
        {
            [RpfmEnums.PackFile] = files.Select(RpfmProtocol.PathFile).ToArray(),
        };
        var data = await SendAsync(
            RpfmProtocol.Tuple("ExtractPackedFiles", packKey, bySource, destPath, asTsv), ct);
        var t = data.GetProperty("StringVecPathBuf");
        var dest = t[0].GetString() ?? destPath;
        var list = t[1].Deserialize<List<string>>(RpfmProtocol.Json) ?? new();
        return new ExtractResult(dest, list);
    }

    /// <summary>单张表导出成 TSV（比整包 Extract 轻）。</summary>
    public Task<JsonElement> ExportTsvAsync(string packKey, string internalPath, string destTsv,
                                            string source = RpfmEnums.PackFile,
                                            CancellationToken ct = default) =>
        SendAsync(RpfmProtocol.Tuple("ExportTSV", packKey, internalPath, destTsv, source), ct);

    /// <summary>把磁盘上的 TSV 编译回 pack 内的表。</summary>
    public Task<JsonElement> ImportTsvAsync(string packKey, string internalPath, string tsvPath,
                                            CancellationToken ct = default) =>
        SendAsync(RpfmProtocol.Tuple("ImportTSV", packKey, internalPath, tsvPath), ct);

    /// <summary>解码一个文件（表 → DB/TableInMemory、文本 → Text…）。响应是带类型标签的变体。</summary>
    public Task<JsonElement> DecodeFileAsync(string packKey, string internalPath,
                                             string source = RpfmEnums.PackFile,
                                             CancellationToken ct = default) =>
        SendAsync(RpfmProtocol.Tuple("DecodePackedFile", packKey, internalPath, source), ct);

    public async Task<byte[]> GetRawDataAsync(string packKey, string internalPath,
                                              CancellationToken ct = default)
    {
        var data = await SendAsync(RpfmProtocol.Tuple("GetPackedFileRawData", packKey, internalPath), ct);
        return data.GetProperty("VecU8").Deserialize<byte[]>(RpfmProtocol.Json) ?? [];
    }

    // ── Schema ───────────────────────────────────────────────────

    public async Task<bool> IsSchemaLoadedAsync(CancellationToken ct = default)
    {
        var data = await SendAsync("IsSchemaLoaded", ct);
        return data.GetProperty("Bool").GetBoolean();
    }

    /// <summary>按表名拿全部版本的 Definition（表编辑器的列定义）。</summary>
    public Task<JsonElement> DefinitionsByTableNameAsync(string tableName,
                                                         CancellationToken ct = default) =>
        SendAsync(RpfmProtocol.Newtype("DefinitionsByTableName", tableName), ct);

    /// <summary>把 Definition 处理成"实际列"（bitwise 展开 / enum 转换 / 颜色三列合并）。</summary>
    public Task<JsonElement> FieldsProcessedAsync(JsonElement definition,
                                                  CancellationToken ct = default) =>
        SendAsync(RpfmProtocol.Newtype("FieldsProcessed", definition), ct);

    // ── 搜索 / 诊断 ──────────────────────────────────────────────

    /// <summary>跨表引用搜索；返回的每条带 (数据源, pack key, 路径, 列名, 列号, 行号)。</summary>
    public async Task<List<ReferenceHit>> SearchReferencesAsync(
        string packKey,
        IReadOnlyDictionary<string, IEnumerable<string>> tableColumns,
        string searchValue,
        CancellationToken ct = default)
    {
        var cols = tableColumns.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
        var data = await SendAsync(
            RpfmProtocol.Tuple("SearchReferences", packKey, cols, searchValue), ct);
        var rows = data.GetProperty("VecDataSourceStringStringStringUsizeUsize");
        var hits = new List<ReferenceHit>(rows.GetArrayLength());
        foreach (var r in rows.EnumerateArray())
        {
            hits.Add(new ReferenceHit(
                r[0].GetString() ?? "", r[1].GetString() ?? "", r[2].GetString() ?? "",
                r[3].GetString() ?? "", r[4].GetInt64(), r[5].GetInt64()));
        }
        return hits;
    }

    /// <summary>整套诊断（坏引用 / 非法枚举 / 空 key…），扫所有打开的 pack。</summary>
    public Task<JsonElement> DiagnosticsCheckAsync(IEnumerable<string>? ignored = null,
                                                   bool checkAk = false,
                                                   CancellationToken ct = default) =>
        SendAsync(RpfmProtocol.Tuple("DiagnosticsCheck",
                                     (ignored ?? []).ToArray(), checkAk), ct);
}
