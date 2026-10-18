param([Parameter(Mandatory=$true)][string]$PlanPath)
$ErrorActionPreference = 'Stop'
$cache = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($PlanPath))
$log = Join-Path $cache 'install.log'
$plan = $null
$validated = $false
$applied = New-Object 'System.Collections.Generic.List[object]'
function Within([string]$root, [string]$relative) {
    $boundary = [IO.Path]::GetFullPath($root).TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
    $path = [IO.Path]::GetFullPath((Join-Path $boundary $relative))
    if (!$path.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) { throw "Update path outside root: $relative" }
    $cursor = $path
    while ($cursor -and $cursor.Length -ge $boundary.TrimEnd('\').Length) {
        if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Update path is a link: $cursor" }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
    return $path
}
function Note([string]$text) { Add-Content -LiteralPath $log -Value ((Get-Date -Format o) + ' ' + $text) -Encoding UTF8 }
function Hash([string]$path) {
    $stream = [IO.File]::OpenRead($path)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-','') }
    finally { $stream.Dispose(); $algorithm.Dispose() }
}
function Result([bool]$success, [string]$message) {
    $data = @{ Success=$success; Version=$plan.Version; Message=$message; LogPath=$log; CacheDirectory=$cache; At=(Get-Date -Format o) }
    $temporary = $plan.ResultPath + '.writing'
    [IO.File]::WriteAllText($temporary, ($data | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporary -Destination $plan.ResultPath -Force
}
try {
    $plan = Get-Content -LiteralPath $PlanPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($plan.PayloadDirectory -ne (Within $cache 'payload') -or $plan.BackupDirectory -ne (Within $cache 'backup')) { throw 'Invalid staging directories' }
    $expectedResult = Join-Path ([IO.Path]::GetDirectoryName($cache)) 'last-result.json'
    if ([IO.Path]::GetFullPath($plan.ResultPath) -ne $expectedResult) { throw 'Invalid result path' }
    $validated = $true
    $exe = Within $plan.InstallDirectory 'WarbandStudio.exe'
    if ($plan.ParentPid -gt 0) {
        $parent = Get-Process -Id $plan.ParentPid -ErrorAction SilentlyContinue
        if ($parent -and !$parent.WaitForExit(60000)) { throw 'Application did not exit; installation cancelled' }
    }
    Note ('Installing ' + $plan.Version)
    # 在改安装目录之前完整验证全部暂存文件并备份原文件。
    $seen = @{}
    foreach ($item in $plan.Files) {
        if ($seen.ContainsKey($item.Path)) { throw 'Duplicate update path' }; $seen[$item.Path] = $true
        $source = Within $plan.PayloadDirectory $item.Path
        $target = Within $plan.InstallDirectory $item.Path
        $backup = Within $plan.BackupDirectory $item.Path
        if ((Hash $source) -ne $item.Sha256) { throw ('Staged file hash mismatch: ' + $item.Path) }
        if ([IO.File]::Exists($target)) {
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($backup)) | Out-Null
            [IO.File]::Copy($target, $backup, $true)
        }
    }
    foreach ($item in $plan.Files) {
        $source = Within $plan.PayloadDirectory $item.Path
        $target = Within $plan.InstallDirectory $item.Path
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
        $applied.Add($item)
        [IO.File]::Copy($source, $target, $true)
        if ((Hash $target) -ne $item.Sha256) { throw ('Installed file hash mismatch: ' + $item.Path) }
    }
    Result $true 'Update installed'
    if ($plan.Restart) {
        $restarted = Start-Process -FilePath $exe -WorkingDirectory $plan.InstallDirectory -WindowStyle Normal -PassThru
        Start-Sleep -Milliseconds 2500
        if ($restarted.HasExited) { throw 'Updated application exited immediately' }
    }
    Note 'Installation completed'
    # 只删已验证位于本次缓存目录下的下载、暂存与备份，保留安装日志/结果便于追踪。
    try { foreach ($name in @('application.zip','payload','backup')) {
        $target = Within $cache $name
        if (Test-Path -LiteralPath $target) {
            if (Get-ChildItem -LiteralPath $target -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint } | Select-Object -First 1) { throw 'Cleanup path contains a link' }
            Remove-Item -LiteralPath $target -Recurse -Force
        }
    } } catch { Note ('Cleanup warning: ' + $_.Exception.Message) }
    exit 0
} catch {
    $failure = $_.Exception.Message
    Note ('Update failed: ' + $failure)
    $rollbackComplete = $true
    for ($index = $applied.Count - 1; $index -ge 0; $index--) {
        $item = $applied[$index]
        try {
            $target = Within $plan.InstallDirectory $item.Path
            $backup = Within $plan.BackupDirectory $item.Path
            if ([IO.File]::Exists($backup)) {
                # 被占用而未写入的文件通常仍是原文，避免对它做无意义的失败恢复。
                if (![IO.File]::Exists($target) -or (Hash $target) -ne (Hash $backup)) { [IO.File]::Copy($backup, $target, $true) }
                if ((Hash $target) -ne (Hash $backup)) { throw ('Rollback hash mismatch: ' + $item.Path) }
            }
            elseif ([IO.File]::Exists($target)) { [IO.File]::Delete($target) }
        } catch { $rollbackComplete = $false; Note ('Rollback warning: ' + $_.Exception.Message) }
    }
    if ($plan -and $validated) {
        if (!$rollbackComplete) { $failure += '; rollback incomplete, retain backup and inspect install.log' }
        try { Result $false $failure } catch { Note ('Cannot write result: ' + $_.Exception.Message) }
        # 原程序仍在运行则不重复启动；仅在已退出后恢复启动旧版。
        if ($plan.Restart -and $rollbackComplete -and !(Get-Process -Id $plan.ParentPid -ErrorAction SilentlyContinue)) {
            try { Start-Process -FilePath (Within $plan.InstallDirectory 'WarbandStudio.exe') -WorkingDirectory $plan.InstallDirectory -WindowStyle Normal | Out-Null }
            catch { Note ('Restart failed: ' + $_.Exception.Message) }
        }
    }
    exit 1
}
