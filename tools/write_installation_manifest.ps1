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
$components=[ordered]@{}
$other=@(Get-ChildItem -LiteralPath $root -File | Where-Object { $_.Extension -in @('.dll','.exe','.json') -and !$files.Contains($_.Name) })
foreach($folder in @('rpfm','bundled','runtimes')+@(Get-ChildItem -LiteralPath $root -Directory|Where-Object {$_.Name -match '^[a-z]{2}(-[A-Za-z]{2,4})?$'}|ForEach-Object {$_.Name})) {
    $path=Join-Path $root $folder
    if(Test-Path -LiteralPath $path){$other+=Get-ChildItem -LiteralPath $path -File -Recurse}
}
foreach($file in $other|Sort-Object FullName) {
    if($file.Attributes -band [IO.FileAttributes]::ReparsePoint){throw "Component path is a link: $($file.FullName)"}
    $relative=$file.FullName.Substring($root.TrimEnd('\','/').Length+1).Replace('\','/')
    $components[$relative]=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
}
if(!$components.Contains('rpfm/rpfm_cli.exe')){throw 'Missing bundled RPFM component'}
$manifest=@{Format=1;Version=$Version;Files=$files;Components=$components}|ConvertTo-Json -Depth 4
[IO.File]::WriteAllText((Join-Path $root 'schemas/installation-manifest.json'),$manifest,[Text.UTF8Encoding]::new($false))
