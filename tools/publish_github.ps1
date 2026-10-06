# publish_github.ps1 —— **手动**发布某个版本到 GitHub Release（不自动、由你挑版本）
#
#   用法：powershell ./tools/publish_github.ps1 -Version 1.1 [-Notes "更新说明…"] [-DryRun]
#
#   做三件事：
#     ① 找到 release/WarbandStudio_v<版本>/（先跑 build.ps1 生成）；
#     ② 打两个包：<版本>_full.zip（完整包，首次下载用）、<版本>_app.zip（只含程序本体，约 2MB，给老用户）；
#     ③ 用 gh CLI 建 Release 并上传（没装 gh 就打印出可直接复制的命令）。
#
#   注意：仓库名读根目录 GITHUB_REPO.txt（owner/repo），要 **Public** 别人才能匿名下载。

param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Notes = "",
    [switch]$DryRun
)
$ErrorActionPreference = "Stop"
$Root = Split-Path $PSScriptRoot -Parent
$RelDir = Join-Path $Root "release"
$Src = Join-Path $RelDir "WarbandStudio_v$Version"

if (-not (Test-Path $Src)) { throw "没有这个版本目录：$Src（先跑 build.ps1 生成它）" }

# 仓库名
$repo = ""
$repoFile = Join-Path $Root "GITHUB_REPO.txt"
if (Test-Path $repoFile) {
    $repo = ((Get-Content $repoFile -Raw) -split "`n" | Where-Object { $_ -and -not $_.StartsWith("#") } | Select-Object -First 1)
    if ($repo) { $repo = $repo.Trim() }
}
if (-not $repo) { throw "还没配仓库：在 $repoFile 里填 owner/repo（第一行非 # 内容）" }

$fullZip = Join-Path $RelDir "WarbandStudio_v${Version}_full.zip"
$appZip = Join-Path $RelDir "WarbandStudio_v${Version}_app.zip"
$tag = "v$Version"

Write-Host "[=] 发布 $tag → $repo" -ForegroundColor Cyan
Write-Host "    完整包：$fullZip"
Write-Host "    程序包：$appZip（只含 exe + WarbandStudio.*.dll + Web/ + update_repo.txt）"
if ($DryRun) { Write-Host "    [dry] 不打包、不上传" ; return }

# ① 完整包
if (Test-Path $fullZip) { Remove-Item $fullZip -Force }
Compress-Archive -Path $Src -DestinationPath $fullZip -Force
Write-Host ("[✓] 完整包：{0:N1} MB" -f ((Get-Item $fullZip).Length / 1MB)) -ForegroundColor Green

# ② 程序包（只收"会随版本变"的那几样；rpfm/ 与 bundled/ 不变，不重复下）
$appTmp = Join-Path $RelDir "_app_pack"
if (Test-Path $appTmp) { Remove-Item $appTmp -Recurse -Force }
New-Item -ItemType Directory -Path $appTmp | Out-Null
foreach ($f in @("WarbandStudio.exe", "WarbandStudio.dll", "WarbandStudio.deps.json", "WarbandStudio.runtimeconfig.json",
                 "WarbandStudio.Core.dll", "WarbandStudio.Pack.dll", "WarbandStudio.Packfile.dll", "WarbandStudio.Rpfm.dll",
                 "WarbandStudio.Editors.dll", "update_repo.txt")) {
    $p = Join-Path $Src $f
    if (Test-Path $p) { Copy-Item $p $appTmp }
}
Copy-Item (Join-Path $Src "Web") $appTmp -Recurse
Compress-Archive -Path (Join-Path $appTmp "*") -DestinationPath $appZip -Force
Remove-Item $appTmp -Recurse -Force
Write-Host ("[✓] 程序包：{0:N1} MB" -f ((Get-Item $appZip).Length / 1MB)) -ForegroundColor Green

# ③ 上传（gh CLI）
$gh = Get-Command gh -ErrorAction SilentlyContinue
$cmd = "gh release create $tag `"$fullZip`" `"$appZip`" --repo $repo --title `"WarbandStudio $tag`" --notes `"$Notes`""
if ($gh) {
    if (-not $Notes) { $Notes = "WarbandStudio $tag" }
    & gh release create $tag $fullZip $appZip --repo $repo --title "WarbandStudio $tag" --notes $Notes
    if ($LASTEXITCODE -ne 0) { throw "gh 上传失败（exit $LASTEXITCODE）；也可以手动跑下面这条：`n$cmd" }
    Write-Host "[✓] 已发布：https://github.com/$repo/releases/tag/$tag" -ForegroundColor Green
} else {
    Write-Host "[i] 没找到 gh CLI —— 装一个（winget install GitHub.cli 然后 gh auth login），或手动执行：" -ForegroundColor Yellow
    Write-Host "    $cmd"
}
