<#
  build.ps1 —— 打包 WarbandStudio（self-contained win-x64，目标机不用装 .NET）

  **版本命名（用户定的）**：
    · **没推送**（还没上传 GitHub Release）→ `v<已发布版本>.n`：v1.3.1、v1.3.2…
      这里的"已发布版本"= 上一次上传的那一版；同名目录已存在就自动 n+1。
    · **推送一次**（tools\publish_github.ps1 上传）→ **改名**成 `v<已发布版本+1>`：v1.3.2 → **v1.4**。
      上传脚本会自动用发布名再构建一次（exe 里编进去的版本号必须等于 Release 标签，
      不然"检查更新"会永远提示有新版），VERSION 也随之变成 v1.4，下一次本地构建就是 v1.4.1。
  `release/WarbandStudio_v<号>/` 目录**只保留最近 5 个**，更早的自动删掉（`$KeepAlways` 里的留档版除外）。

  用法：
    .\build.ps1                 用 VERSION 文件里的号；同名目录已存在就自动 n+1（没 n 就补 .1）
    .\build.ps1 -Version 1.3.4  指定版本号
    .\build.ps1 -DryRun         只打印打算做什么，不动手
    .\build.ps1 -NoPublish      只编译验证，不产出发布目录

  产物：release/WarbandStudio_v<号>/WarbandStudio.exe（双击就能跑）
#>
param(
    [string]$Version = "",
    [switch]$DryRun,
    [switch]$NoPublish
)

$ErrorActionPreference = "Stop"
$Root   = $PSScriptRoot
$RelDir = Join-Path $Root "release"
$Keep   = 5          # 当前 + 4 个旧版本

# 优先用工作区里的用户级 SDK（tools\dotnet），没有就退回 PATH 上的 dotnet
$Dotnet = Join-Path (Split-Path $Root) "dotnet\dotnet.exe"
if (-not (Test-Path $Dotnet)) { $Dotnet = "dotnet" }

# ── 版本号 ────────────────────────────────────────────────
if (-not $Version) {
    $Version = (Get-Content (Join-Path $Root "VERSION") -Raw).Trim()
}
$Target = Join-Path $RelDir "WarbandStudio_v$Version"
# 版本号用 [version] 解析（0.13 → 次版本 +1 = 0.14）。不要用 double：
# "0.10" 会被当成 0.1，递增和排序都会乱（0.9 反而比 0.13 大）
# 递增走**第三段**（v1.3.1 → v1.3.2）；只有两段（= 刚推送过的发布号）就先补成 .1
while (Test-Path $Target) {
    $v = [version]$Version
    $Version = if ($v.Build -ge 0) { "$($v.Major).$($v.Minor).$($v.Build + 1)" } else { "$($v.Major).$($v.Minor).1" }
    $Target = Join-Path $RelDir "WarbandStudio_v$Version"
    Write-Host "[=] 版本号被占用，改到 v$Version" -ForegroundColor Yellow
}
if (-not $DryRun) {
    Set-Content -Path (Join-Path $Root "VERSION") -Value $Version -Encoding ascii
}

Write-Host "[=] 构建 WarbandStudio v$Version（self-contained win-x64）" -ForegroundColor Cyan
Write-Host "    dotnet  : $Dotnet"
Write-Host "    产物    : $Target"

# ── 构建 ──────────────────────────────────────────────────
Push-Location $Root
try {
    if ($NoPublish) {
        if ($DryRun) { Write-Host "    [dry] 会跑 dotnet build" ; return }
        & $Dotnet build "WarbandStudio.slnx" -c Release -v q
        if ($LASTEXITCODE -ne 0) { throw "构建失败（exit $LASTEXITCODE）" }
        Write-Host "[✓] 编译通过（未产出发布目录）" -ForegroundColor Green
        return
    }

    if ($DryRun) {
        Write-Host "    [dry] 会跑 dotnet publish -c Release -r win-x64 --self-contained" 
    } else {
        # 版本号编进程序（启动时的"检查更新"要靠它比对）+ 仓库名带进 release（update_repo.txt）
        & $Dotnet publish "src/WarbandStudio.Ui/WarbandStudio.Ui.csproj" `
            -c Release -r win-x64 --self-contained true `
            -p:PublishSingleFile=false -p:DebugType=none `
            -p:Version=$Version `
            -o $Target -v q
        if ($LASTEXITCODE -ne 0) { throw "发布失败（exit $LASTEXITCODE）" }
        $repoFile = Join-Path $Root "GITHUB_REPO.txt"
    if (Test-Path $repoFile) {
        $repo = ((Get-Content $repoFile -Raw) -split "`n" | Where-Object { $_ -and -not $_.StartsWith("#") } | Select-Object -First 1)
        if ($repo) { Set-Content -Path (Join-Path $Target "update_repo.txt") -Value $repo.Trim() -Encoding ascii }
    }
    Write-Host "[✓] 发布完成：$Target" -ForegroundColor Green
    }

    # ── 保留最近 $Keep 个版本 ─────────────────────────────
    # 版本号现在可能是三段（v1.3.1 这种"未推送"号）→ 正则也要认三段，不然它们不进清理/排序名单
    $all = Get-ChildItem $RelDir -Directory -Filter "WarbandStudio_v*" |
           Where-Object { $_.Name -match '^WarbandStudio_v(\d+\.\d+(\.\d+)?)$' } |
           Sort-Object { [version]($_.Name -replace '^WarbandStudio_v', '') }
    # **留档版本不自动删**：v1.0 是正式版里程碑，永远保留（以后要加别的留档版本就往这个名单里加）
    $KeepAlways = @('WarbandStudio_v1.0')
    $stale = $all | Select-Object -SkipLast $Keep | Where-Object { $KeepAlways -notcontains $_.Name }
    foreach ($d in $stale) {
        if ($DryRun) { Write-Host "    [dry] 会删旧版本 $($d.Name)" }
        else {
            Remove-Item $d.FullName -Recurse -Force
            Write-Host "    [=] 删掉旧版本 $($d.Name)（只留最近 $Keep 个）"
        }
    }
    $kept = (Get-ChildItem $RelDir -Directory -Filter "WarbandStudio_v*" | Measure-Object).Count
    Write-Host "[i] release/ 里现在有 $kept 个版本" -ForegroundColor Cyan
}
finally { Pop-Location }
