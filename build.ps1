<#
  build.ps1 —— 打包 WarbandStudio（self-contained win-x64，目标机不用装 .NET）

  版本约定：从 v0.1 起步，每次构建出一个 `release/WarbandStudio_v<号>/` 目录，
  **只保留最近 5 个**（当前 + 4 个旧版本），更早的自动删掉。

  用法：
    .\build.ps1                 用 VERSION 文件里的号；同名目录已存在就自动加 0.1
    .\build.ps1 -Version 0.3    指定版本号
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
while (Test-Path $Target) {
    $v = [version]$Version
    $Version = "$($v.Major).$($v.Minor + 1)"
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
    $all = Get-ChildItem $RelDir -Directory -Filter "WarbandStudio_v*" |
           Where-Object { $_.Name -match '^WarbandStudio_v(\d+\.\d+)$' } |
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
