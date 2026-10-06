using System.Text.Json;
using System.Text.Json.Serialization;

namespace WarbandStudio.Rpfm;

/// <summary>
/// rpfm_server WebSocket 协议的序列化约定。
///
/// 依据 5.0.6 源码里的 docs/server/ws-protocol.md：
///   - 消息封套统一是 {"id": n, "data": &lt;命令或响应&gt;}，响应用同一个 id 回来；
///   - Rust serde 变体规则：单元变体 = "NewPack"，newtype = {"ClosePack": "key"}，
///     元组变体 = {"SavePackAs": [key, path]}；
///   - 载荷里的字段名是 snake_case（file_path / pfh_version / table_name…），
///     所以 DTO 统一走 SnakeCaseLower 命名策略。
/// </summary>
public static class RpfmProtocol
{
    public const string DefaultUrl = "ws://127.0.0.1:45127/ws";
    public const int DefaultPort = 45127;

    /// <summary>载荷 DTO 的序列化设置（字段 snake_case、未知字段忽略）。</summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>单元变体命令：直接发字符串，如 "ListOpenPacks"。</summary>
    public static object Unit(string variant) => variant;

    /// <summary>newtype 变体命令：{"ClosePack": "my_mod.pack"}。</summary>
    public static object Newtype(string variant, object? value) =>
        new Dictionary<string, object?> { [variant] = value };

    /// <summary>元组变体命令：{"SavePackAs": ["my_mod.pack", "/path/out.pack"]}。</summary>
    public static object Tuple(string variant, params object?[] args) =>
        new Dictionary<string, object?> { [variant] = args };

    /// <summary>pack 内路径：{ "File": "db/units_tables/data" } / { "Folder": "db" }。</summary>
    public static object PathFile(string path) =>
        new Dictionary<string, object> { ["File"] = path };

    public static object PathFolder(string path) =>
        new Dictionary<string, object> { ["Folder"] = path };
}
