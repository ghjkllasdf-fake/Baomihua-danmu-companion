#requires -Version 7.6
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Add-Type -Path (Join-Path $root 'desktop/native/AppearancePreferences.cs') -ReferencedAssemblies @(Get-ChildItem (Join-Path $PSHOME 'ref') -Filter '*.dll'|ForEach-Object FullName)
function Assert($condition,[string]$message){if(-not $condition){throw $message}}
$folder=Join-Path $root ('.cache/appearance-test-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($folder)
$path=Join-Path $folder 'appearance.json'
$missing=[Popcorn.AppearancePreferences]::Load($path)
Assert ($missing.Settings.Skin -ceq 'fluent' -and -not $missing.Settings.ReduceTransparency -and $missing.Notice -eq '') 'Missing preferences must preserve A as default.'
foreach($skin in @('fluent','glass','winui','studio')){
    [Popcorn.AppearancePreferences]::Save($path,[Popcorn.AppearanceSettings]::new($skin,$true))
    $read=[Popcorn.AppearancePreferences]::Load($path)
    Assert ($read.Settings.Skin -ceq $skin -and $read.Settings.ReduceTransparency -and $read.Notice -eq '') 'Every bundled skin must roundtrip.'
}
$before=[IO.File]::ReadAllText($path)
try{[Popcorn.AppearancePreferences]::Save($path,[Popcorn.AppearanceSettings]::new('../../external.css',$false));throw 'Invalid skin accepted.'}catch{if($_.Exception.Message -like '*Invalid skin accepted*'){throw}}
Assert ([IO.File]::ReadAllText($path) -ceq $before) 'Invalid IDs must not overwrite confirmed preferences.'
$locked=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
try{$failed=$false;try{[Popcorn.AppearancePreferences]::Save($path,[Popcorn.AppearanceSettings]::new('glass',$false))}catch{$failed=$true};Assert $failed 'A locked destination must report save failure.'}finally{$locked.Dispose()}
Assert ([IO.File]::ReadAllText($path) -ceq $before) 'Failed replacement must preserve previous bytes.'
Assert (@(Get-ChildItem -LiteralPath $folder -Filter '*.tmp').Count -eq 0) 'Temporary files must be cleaned after failed saves.'
foreach($text in @('{','{"schemaVersion":2,"settings":{"skin":"glass","reduceTransparency":false}}','{"schemaVersion":1,"settings":{"skin":"external","reduceTransparency":false}}','{"schemaVersion":1,"settings":{"skin":"glass","reduceTransparency":false,"apiBase":"private"}}','{"schemaVersion":1,"settings":{"skin":"fluent","skin":"glass","reduceTransparency":false}}')){
    [IO.File]::WriteAllText($path,$text)
    $read=[Popcorn.AppearancePreferences]::Load($path)
    Assert ($read.Settings.Skin -ceq 'fluent' -and $read.Notice -ne '') 'Corrupt, future, unknown or duplicate configuration must fall back with a generic notice.'
    Assert ([IO.File]::ReadAllText($path) -ceq $text) 'Reads must preserve the original file.'
}
[IO.File]::WriteAllBytes($path,[byte[]]::new(4097))
Assert ([Popcorn.AppearancePreferences]::Load($path).Notice -ne '') 'Oversized configuration must be rejected.'
'PASS: skin-only atomic preferences, four skins, invalid/duplicate/future/size rejection, failed-save preservation and temporary-file cleanup.'
