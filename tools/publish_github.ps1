# publish_github.ps1 —— **手动**发布某个版本到 GitHub Release（不自动、由你挑版本）
#
#   用法：powershell ./tools/publish_github.ps1 -Version 1.3.2 [-Notes "更新说明…"] [-DryRun]
#
#   **版本命名（用户定的）**：
#     · 没推送时版本叫 `v1.x.n`（v1.3.1、v1.3.2…，x = 上一次上传的版本号）；
#     · **推送一次 = 改名成 `v1.x+1`**（v1.3.2 → **v1.4**）。
#   所以这个脚本做的事是：
#     ① 算出发布名（三段号 → 去掉 .n、次版本 +1；两段号就直接用）；
#     ② **用发布名构建一次**（`build.ps1 -Version <发布名>`，目录已存在就跳过）—— exe 里编进去的版本号
#        必须等于 Release 标签，否则装了这一版的人"检查更新"会永远提示有新版（1.3.2 < 1.4）；
#     ③ 打两个包：<发布名>_full.zip（完整包，首次下载用）、<发布名>_app.zip（只含程序本体，给老用户）；
#     ④ 用 gh CLI 建 Release 并上传（没装 gh 就打印出可直接复制的命令）。
#
#   注意：仓库名读根目录 GITHUB_REPO.txt（owner/repo），要 **Public** 别人才能匿名下载。

param(
    [Parameter(Mandatory = $true)][string]$Version,   # 本地这版（v1.3.2 这种未推送号，或已经是发布号）
    [string]$Notes = "",
    [switch]$DryRun
)
$ErrorActionPreference = "Stop"
$Root = Split-Path $PSScriptRoot -Parent
$RelDir = Join-Path $Root "release"

# ① 发布名：三段 → 去掉第三段、次版本 +1（v1.3.2 → v1.4）；两段 → 就是它自己
$v = [version]$Version
$Release = if ($v.Build -ge 0) { "$($v.Major).$($v.Minor + 1)" } else { "$($v.Major).$($v.Minor)" }
$Src = Join-Path $RelDir "WarbandStudio_v$Release"

if ($Release -ne $Version) {
    Write-Host "[=] 推送 = 改名：v$Version → **v$Release**" -ForegroundColor Cyan
}
if (-not (Test-Path $Src) -and -not $DryRun) {
    Write-Host "[=] release/WarbandStudio_v$Release 还没有 → 用发布名构建一次（exe 里编进 $Release）" -ForegroundColor Cyan
    & (Join-Path $Root "build.ps1") -Version $Release
    if ($LASTEXITCODE -ne 0) { throw "构建失败（exit $LASTEXITCODE）" }
}
if ($DryRun -and -not (Test-Path $Src)) {
    Write-Host "    [dry] 会跑 build.ps1 -Version $Release（exe 里的版本号要等于发布标签）"
}

# 仓库名
$repo = ""
$repoFile = Join-Path $Root "GITHUB_REPO.txt"
if (Test-Path $repoFile) {
    $repo = ((Get-Content $repoFile -Raw) -split "`n" | Where-Object { $_ -and -not $_.StartsWith("#") } | Select-Object -First 1)
    if ($repo) { $repo = $repo.Trim() }
}
if (-not $repo) { throw "还没配仓库：在 $repoFile 里填 owner/repo（第一行非 # 内容）" }

$fullZip = Join-Path $RelDir "WarbandStudio_v${Release}_full.zip"
$appZip = Join-Path $RelDir "WarbandStudio_v${Release}_app.zip"
$tag = "v$Release"

Write-Host "[=] 发布 $tag → $repo" -ForegroundColor Cyan
Write-Host "    完整包：$fullZip"
Write-Host "    程序包：$appZip（只含 exe + WarbandStudio.*.dll + Web/ + update_repo.txt）"
if ($DryRun) { Write-Host "    [dry] 不打包、不上传" ; return }

# ② 完整包
if (Test-Path $fullZip) { Remove-Item $fullZip -Force }
Compress-Archive -Path $Src -DestinationPath $fullZip -Force
Write-Host ("[✓] 完整包：{0:N1} MB" -f ((Get-Item $fullZip).Length / 1MB)) -ForegroundColor Green

# ③ 程序包（只收"会随版本变"的那几样；rpfm/ 与 bundled/ 不变，不重复下）
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

# ④ 上传（gh CLI）
# gh：优先用工作区里的（<工作区>\tools\gh\gh.exe，和 build.ps1 找 dotnet 一个路子），没有就退回 PATH
$gh = Join-Path (Split-Path $Root) "gh\gh.exe"
if (-not (Test-Path $gh)) { $gh = (Get-Command gh -ErrorAction SilentlyContinue).Source }
# gh 是 Go 程序，**不读 Windows 系统代理** —— 把系统代理转成 HTTPS_PROXY（Clash 这类）；不然 github.com 直连超时
if (-not $env:HTTPS_PROXY) {
    $ps = Get-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings" -ErrorAction SilentlyContinue
    if ($ps -and $ps.ProxyEnable -eq 1 -and $ps.ProxyServer) {
        $env:HTTPS_PROXY = "http://$($ps.ProxyServer)"
        Write-Host "[i] 已套用系统代理：$env:HTTPS_PROXY" -ForegroundColor Cyan
    }
}
$cmd = "gh release create $tag `"$fullZip`" `"$appZip`" --repo $repo --title `"WarbandStudio $tag`" --notes `"$Notes`""
if ($gh) {
    if (-not $Notes) { $Notes = "WarbandStudio $tag" }
    & $gh release create $tag $fullZip $appZip --repo $repo --title "WarbandStudio $tag" --notes $Notes
    if ($LASTEXITCODE -ne 0) { throw "gh 上传失败（exit $LASTEXITCODE）；也可以手动跑下面这条：`n$cmd" }
    Write-Host "[✓] 已发布：https://github.com/$repo/releases/tag/$tag" -ForegroundColor Green
} else {
    Write-Host "[i] 没找到 gh —— 把 gh.exe 放到 <工作区>\tools\gh\（或 winget install GitHub.cli），或手动执行：" -ForegroundColor Yellow
    Write-Host "    $cmd"
}
