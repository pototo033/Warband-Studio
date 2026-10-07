# publish_github.ps1 —— **手动**发布某个版本到 GitHub Release（不自动、由你挑版本）
#
#   用法：powershell ./tools/publish_github.ps1 -Version 1.4.1 [-Notes "更新说明…"] [-DryRun]
#
#   **版本命名（用户定的，2026-10-07 起）**：**直接发布给定的版本号** —— v1.4.1 就发 v1.4.1
#   （旧规则"未推送 v1.x.n → 推送改名 v1.x+1"已废弃）。发布号必须等于 exe 里编进去的版本号
#   （build.ps1 构建时会写死），否则装了这一版的人"检查更新"会误报有新版。
#   发布说明：优先用 release/notes_v<版本>.md（存在就用 --notes-file，配合根目录 CHANGELOG.md），
#   否则用 -Notes；都没有就只写标题。
#   这个脚本做的事：
#     ① 发布名 = 给定版本号（v 前缀可有可无）；
#     ② 该版本目录不在就先构建（`build.ps1 -Version <发布名>`）；
#     ③ 打两个包：<发布名>_full.zip（完整包，首次下载用）、<发布名>_app.zip（只含程序本体，给老用户）；
#     ④ 用 gh CLI 建 Release 并上传（没有 gh 就打印出可直接复制的命令）。
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

# ① 发布名 = **直接用地给的版本号**（2026-10-07 用户拍板：v1.4.1 就发 v1.4.1；旧的"推送改名 v1.x+1"规则废弃）
$Release = ([string]$Version).Trim().TrimStart('v', 'V')
$Src = Join-Path $RelDir "WarbandStudio_v$Release"
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
# 首次启动生成的 packs/ 不是发布物（程序首次运行时自己会重建）——先挪走再打包，
# 不然 full 包会带上本机的源包白胖十几 MB（v1.4.1 打包实测 133 → 150 MB）
$packsDir = Join-Path $Src "packs"
$packsParked = $null
# 暂存位置必须在 $Src **外面**：放里面会被一起打进包（v1.4.1 第一次打包踩过：143 应 133 MB）
if (Test-Path $packsDir) { $packsParked = Join-Path $RelDir "_packs_parking"; Move-Item $packsDir $packsParked -Force }
try {
    Compress-Archive -Path $Src -DestinationPath $fullZip -Force
} finally {
    if ($packsParked -and (Test-Path $packsParked)) { Move-Item $packsParked $packsDir -Force }
}
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
# gh 认证兜底：优先 $env:GH_TOKEN（用户级环境变量已配）；没有就现从 git 凭据管理器取一个
# （GCM 的 token 缺 read:org，`gh auth login` 会拒；但发 Release 的 repo 权限够 —— 2026-10-07 实测。
#  注意 PowerShell 5.1 的管道给 git 传 stdin 不可靠，用临时文件 + cmd 重定向）
if (-not $env:GH_TOKEN -and $gh) {
    $inFile = [IO.Path]::GetTempFileName()
    try {
        [IO.File]::WriteAllText($inFile, "protocol=https`nhost=github.com`n`n", [Text.Encoding]::ASCII)
        $cred = & cmd /c "git credential fill < `"$inFile`"" 2>$null
        $tk = ($cred | Where-Object { $_ -like "password=*" } | Select-Object -First 1) -replace "^password=", ""
        if ($tk) { $env:GH_TOKEN = $tk; Write-Host "[i] 已从 git 凭据管理器取到 GitHub token（本次上传用，不落盘）" -ForegroundColor Cyan }
        else { Write-Host "[!] 没有 GH_TOKEN、git 凭据里也没有 —— gh 会报未认证" -ForegroundColor Yellow }
    } finally { Remove-Item $inFile -Force -ErrorAction SilentlyContinue }
}
# 发布说明：优先用 release/notes_v<版本>.md（配合根目录 CHANGELOG.md），没有就用 -Notes / 默认标题
$notesFile = Join-Path $RelDir "notes_v$Release.md"
$useNotesFile = (-not $Notes) -and (Test-Path $notesFile)
if ($useNotesFile) { Write-Host "[i] 发布说明用文件：$notesFile" -ForegroundColor Cyan }
$notesArg = if ($useNotesFile) { "--notes-file `"$notesFile`"" }
            elseif ($Notes) { "--notes `"$Notes`"" }
            else { "--notes `"WarbandStudio $tag`"" }
$cmd = "gh release create $tag `"$fullZip`" `"$appZip`" --repo $repo --title `"WarbandStudio $tag`" $notesArg"
if ($gh) {
    if ($useNotesFile) {
        & $gh release create $tag $fullZip $appZip --repo $repo --title "WarbandStudio $tag" --notes-file $notesFile
    } else {
        if (-not $Notes) { $Notes = "WarbandStudio $tag" }
        & $gh release create $tag $fullZip $appZip --repo $repo --title "WarbandStudio $tag" --notes $Notes
    }
    if ($LASTEXITCODE -ne 0) { throw "gh 上传失败（exit $LASTEXITCODE）；也可以手动跑下面这条：`n$cmd" }
    Write-Host "[✓] 已发布：https://github.com/$repo/releases/tag/$tag" -ForegroundColor Green
} else {
    Write-Host "[i] 没找到 gh —— 把 gh.exe 放到 <工作区>\tools\gh\（或 winget install GitHub.cli），或手动执行：" -ForegroundColor Yellow
    Write-Host "    $cmd"
}
