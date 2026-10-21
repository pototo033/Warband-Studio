param([Parameter(Mandatory=$true)][string]$Directory,[Parameter(Mandatory=$true)][string]$Version)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath($Directory)
$paths=@('WarbandStudio.exe','WarbandStudio.dll','WarbandStudio.deps.json','WarbandStudio.runtimeconfig.json',
    'WarbandStudio.Core.dll','WarbandStudio.Pack.dll','WarbandStudio.Packfile.dll','WarbandStudio.Rpfm.dll','WarbandStudio.Editors.dll',
    'Web/canvas.html','schemas/schema_wh3.ron','update_repo.txt')
$files=[ordered]@{}
foreach($path in $paths) {
    $full=Join-Path $root $path
    if(!(Test-Path -LiteralPath $full -PathType Leaf)){throw "Missing release file: $path"}
    $files[$path]=(Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash
}
$manifest=@{Format=1;Version=$Version;Files=$files}|ConvertTo-Json -Depth 4
[IO.File]::WriteAllText((Join-Path $root 'schemas/installation-manifest.json'),$manifest,[Text.UTF8Encoding]::new($false))
