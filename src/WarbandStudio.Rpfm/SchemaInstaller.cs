using System.Text.Json;

namespace WarbandStudio.Rpfm;

/// <summary>schema 自举的结果。</summary>
public enum SchemaInstallResult
{
    /// <summary>配置目录里已经有 schema —— 直接复用（别人的 RPFM 装过就算数）。</summary>
    AlreadyPresent,
    /// <summary>本来没有，已从随包文件补了一份。</summary>
    Installed,
    /// <summary>配置目录里没有，随包文件也找不到（发布时漏带）。</summary>
    BundledMissing,
    /// <summary>补进去了，但 server 仍然说没加载 —— 文件坏了或版本不对。</summary>
    NotLoaded,
}

public sealed record SchemaReport(
    SchemaInstallResult Result, string SchemasFolder, string SchemaFile,
    bool Loaded, string Message);

/// <summary>
/// schema 自举：让**没装过 RPFM 的机器**也能解表。
///
/// 背景（查过 5.0.6 源码确认）：
///   - rpfm_server 只能从固定配置目录读 schema：
///     <c>%APPDATA%\FrodoWazEre\rpfm\config\schemas\&lt;游戏&gt;.ron</c>；
///   - 没有任何"从指定路径加载 schema"的命令（只有联网的 UpdateSchemas）；
///   - 配置目录里没有该文件时，SetGameSelected 不报错，只是 schema 静默为 None，
///     之后解表就报 "There is no Schema for the Game Selected"。
///
/// 所以策略是：**随包带一份，缺了才补，绝不覆盖**。
/// 用户机器上已经有（比如他自己装了 RPFM）就用他那份，免得把新版覆盖成旧版。
/// 补完后重新 SetGameSelected 触发 server 重新载入。
/// </summary>
public static class SchemaInstaller
{
    /// <summary>warhammer_3 → schema_wh3.ron（见 rpfm_lib/src/games/supported_games.rs）。</summary>
    public static string SchemaFileNameFor(string gameKey) => gameKey switch
    {
        RpfmEnums.GameWarhammer3 => "schema_wh3.ron",
        _ => throw new RpfmException($"暂不支持的游戏键：{gameKey}"),
    };

    /// <summary>server 的 schema 目录（Command::SchemasPath）。</summary>
    public static async Task<string?> GetSchemasFolderAsync(RpfmClient client, CancellationToken ct = default)
    {
        var data = await client.SendAsync("SchemasPath", ct);
        return data.TryGetProperty("PathBuf", out var p) ? p.GetString() : null;
    }

    /// <summary>随包 schema 的目录（exe 旁边的 schemas\）。</summary>
    public static string BundledFolder => Path.Combine(AppContext.BaseDirectory, "schemas");

    /// <summary>
    /// 确保 server 有该游戏的 schema 可用。已有的不动，缺的从 <paramref name="bundledFolder"/>
    /// 补一份，然后重新选游戏触发载入。返回报告（含最终是否真的加载成功）。
    /// </summary>
    public static async Task<SchemaReport> EnsureAsync(
        RpfmClient client,
        string gameKey = RpfmEnums.GameWarhammer3,
        string? bundledFolder = null,
        CancellationToken ct = default)
    {
        var file = SchemaFileNameFor(gameKey);
        var folder = await GetSchemasFolderAsync(client, ct)
                     ?? throw new RpfmException("拿不到 server 的 schema 目录（SchemasPath 没返回 PathBuf）。");
        var target = Path.Combine(folder, file);

        // 已有 → 直接复用，绝不动它
        if (File.Exists(target))
        {
            var ok = await ReloadAsync(client, gameKey, ct);
            return new SchemaReport(SchemaInstallResult.AlreadyPresent, folder, target, ok,
                ok ? "已有 schema，直接复用。" : "有 schema 文件但没加载成功，可能版本不匹配。");
        }

        // 没有 → 从随包文件补
        var bundled = Path.Combine(bundledFolder ?? BundledFolder, file);
        if (!File.Exists(bundled))
            return new SchemaReport(SchemaInstallResult.BundledMissing, folder, target, false,
                $"配置目录里没有，随包也没找到：{bundled}");

        Directory.CreateDirectory(folder);
        File.Copy(bundled, target, overwrite: false);
        var loaded = await ReloadAsync(client, gameKey, ct);
        return new SchemaReport(
            loaded ? SchemaInstallResult.Installed : SchemaInstallResult.NotLoaded,
            folder, target, loaded,
            loaded
                ? $"已从随包补上 schema（{new FileInfo(target).Length / 1048576.0:F1} MB）。"
                : "补了 schema 但 server 仍说没加载 —— 检查文件完整性。");
    }

    /// <summary>重新选游戏让 server 重读 schema，并等它加载完（最多约 15 秒）。</summary>
    private static async Task<bool> ReloadAsync(RpfmClient client, string gameKey, CancellationToken ct)
    {
        await client.SetGameAsync(gameKey, rebuild: false, ct);
        for (var i = 0; i < 75; i++)
        {
            if (await client.IsSchemaLoadedAsync(ct)) return true;
            await Task.Delay(200, ct);
        }
        return false;
    }

    /// <summary>
    /// 联网更新 schema（官方路径 UpdateSchemas，从 rpfm-schemas 仓库拉）。
    /// 离线环境会抛错，界面上给"更新失败就继续用现有版本"的提示。
    /// </summary>
    public static async Task<bool> UpdateFromNetworkAsync(RpfmClient client, CancellationToken ct = default)
    {
        await client.SendAsync("UpdateSchemas", ct);
        return await client.IsSchemaLoadedAsync(ct);
    }
}
