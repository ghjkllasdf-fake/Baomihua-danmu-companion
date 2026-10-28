#requires -Version 7.6
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Add-Type -Path @((Join-Path $root 'desktop\native\Core.cs'),(Join-Path $root 'desktop\native\Preferences.cs'))
$folder=Join-Path $root ('.cache\preference-tests-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($folder)
$path=Join-Path $folder 'preferences.json'
function Assert($condition,[string]$message){ if(-not $condition){ throw $message } }
function Put([string]$json){ [IO.File]::WriteAllText($path,$json) }
function ExpectFallback([string]$json){
    Put $json
    $before=[Convert]::ToBase64String([IO.File]::ReadAllBytes($path))
    $result=[Popcorn.SettingsPreferences]::Load($path)
    Assert ($result.Settings.FontSize -eq 28 -and $result.Notice.Length -gt 0) 'Invalid preference data must return defaults with a warning.'
    Assert (-not $result.Notice.Contains($folder) -and -not $result.Notice.Contains('credential-marker')) 'Preference warnings must not expose paths or file content.'
    Assert ($before -eq [Convert]::ToBase64String([IO.File]::ReadAllBytes($path))) 'A failed read must preserve the original file byte for byte.'
}
$failures=[Collections.Generic.List[string]]::new()
function Scenario([string]$name,[scriptblock]$body){
    try { & $body; "PASS: $name" } catch { $failures.Add($name+': '+$_.Exception.Message) }
}
$defaults=[Popcorn.SettingsPreferences]::Load($path)
Assert ($defaults.Settings.FontSize -eq 28 -and $defaults.Notice -eq '') 'A missing preference file should use defaults without a corruption warning.'
Put '{"fontSize":40,"scaleWithWindow":false,"keywords":"测试词"}'
$original=[IO.File]::ReadAllBytes($path)
$saved=[Popcorn.SettingsPreferences]::Load($path)
Assert ($saved.Settings.FontSize -eq 40 -and -not $saved.Settings.ScaleWithWindow -and $saved.Settings.Keywords -eq '测试词' -and $saved.Settings.Opacity -eq 0.85 -and $saved.Notice -eq '') 'Existing partial style files should preserve their values and use defaults for added settings.'
Assert ([Convert]::ToBase64String([IO.File]::ReadAllBytes($path)) -eq [Convert]::ToBase64String($original)) 'Reading existing preferences must not rewrite them.'
Put '{"fontSize":'
$broken=[Popcorn.SettingsPreferences]::Load($path)
Assert ($broken.Settings.FontSize -eq 28 -and $broken.Notice.Length -gt 0) 'Malformed JSON must return defaults with a warning.'
Scenario 'non-object preferences are invalid rather than silently accepted' {
    foreach($json in @('null','[]','"credential-marker"','1','true')){ ExpectFallback $json }
}
Scenario 'oversized preference input is rejected without modifying the source' {
    $prefix='{"fontSize":40}'
    Put ($prefix+(' '*(65536-$prefix.Length)))
    $boundary=[Popcorn.SettingsPreferences]::Load($path)
    Assert ($boundary.Settings.FontSize -eq 40 -and $boundary.Notice -eq '') 'A valid file exactly at the byte limit must remain readable.'
    ExpectFallback ('{"fontSize":40}'+(' '*65536))
}
Scenario 'duplicate setting names are rejected case-insensitively' {
    foreach($json in @('{"fontSize":40,"fontSize":44}','{"fontSize":40,"FontSize":44}')){ ExpectFallback $json }
}
Scenario 'invalid UTF-8 bytes are not silently replaced inside settings' {
    [IO.File]::WriteAllBytes($path,([Text.Encoding]::UTF8.GetBytes('{"keywords":"')+[byte[]]@(255)+[Text.Encoding]::UTF8.GetBytes('"}')))
    $invalid=[Popcorn.SettingsPreferences]::Load($path)
    Assert ($invalid.Settings.Keywords -eq '' -and $invalid.Notice.Length -gt 0) 'Invalid UTF-8 must not be normalized into a seemingly valid saved keyword.'
}
Scenario 'unknown style data and invalid values are not migrated' {
    foreach($json in @('{"apiBase":"credential-marker"}','{"keywords":null}','{"fontSize":2}','{"density":31}')){ ExpectFallback $json }
}
Scenario 'blocked or inaccessible preference paths return a sanitized warning' {
    $inaccessible=[Popcorn.SettingsPreferences]::Load($folder)
    Assert ($inaccessible.Settings.FontSize -eq 28 -and $inaccessible.Notice.Length -gt 0 -and -not $inaccessible.Notice.Contains($folder)) 'An inaccessible path must not look like a missing preference file or leak its path.'
    Put '{"fontSize":40}'
    $lock=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    try {
        $blocked=[Popcorn.SettingsPreferences]::Load($path)
        Assert ($blocked.Settings.FontSize -eq 28 -and $blocked.Notice.Length -gt 0) 'A locked preference file should fall back instead of escaping an exception.'
    } finally { $lock.Dispose() }
}
Scenario 'current envelope loads and legacy UTF-8 BOMs remain supported' {
    Put '{"schemaVersion":1,"settings":{"fontSize":40,"keywords":"测试词"}}'
    $current=[Popcorn.SettingsPreferences]::Load($path)
    Assert ($current.Settings.FontSize -eq 40 -and $current.Settings.Keywords -eq '测试词' -and $current.Notice -eq '') 'The current preference envelope must preserve style data.'
    [IO.File]::WriteAllBytes($path,([byte[]]@(239,187,191)+[Text.Encoding]::UTF8.GetBytes('{"fontSize":44}')))
    $bom=[Popcorn.SettingsPreferences]::Load($path)
    Assert ($bom.Settings.FontSize -eq 44 -and $bom.Notice -eq '') 'Legacy UTF-8 BOM files must still load.'
}
Scenario 'future or malformed envelopes never silently reuse embedded settings' {
    Put '{"schemaVersion":2,"settings":{"fontSize":40}}'
    $future=[Popcorn.SettingsPreferences]::Load($path)
    Assert ($future.Settings.FontSize -eq 28 -and $future.Notice.Contains('版本')) 'An unsupported future format must return defaults with an explicit version warning.'
    foreach($json in @('{"schemaVersion":null,"settings":{}}','{"schemaVersion":"1","settings":{}}','{"schemaVersion":true,"settings":{}}','{"schemaVersion":1.5,"settings":{}}','{"schemaVersion":0,"settings":{}}','{"schemaVersion":1}','{"schemaVersion":1,"settings":null}','{"schemaVersion":1,"schemaVersion":1,"settings":{}}','{"schemaVersion":1,"settings":{},"apiBase":"credential-marker"}')){ ExpectFallback $json }
    Put '{"schemaVersion":2,"settings":{"futureStyle":40},"futureMetadata":true}'
    $futureShape=[Popcorn.SettingsPreferences]::Load($path)
    Assert ($futureShape.Settings.FontSize -eq 28 -and $futureShape.Notice.Contains('版本')) 'Future formats must be identified before trying to interpret their unknown schema.'
}
Scenario 'new saves contain only the version and style whitelist and can be reopened' {
    $settings=[Popcorn.DanmakuSettings]::new(); $settings.FontSize=44; $settings.Keywords='测试词'
    $serialized=[Popcorn.SettingsPreferences]::Serialize($settings)
    $envelope=ConvertFrom-Json -InputObject $serialized
    Assert ((@($envelope.PSObject.Properties.Name | Sort-Object) -join ',') -eq 'schemaVersion,settings' -and $envelope.schemaVersion -eq 1) 'Current preferences must use a versioned style-only envelope.'
    Assert (@($envelope.settings.PSObject.Properties).Count -eq 14 -and -not $serialized.Contains('apiBase')) 'Serialized preferences contain unexpected fields.'
    Put $serialized
    $roundtrip=[Popcorn.SettingsPreferences]::Load($path)
    Assert ($roundtrip.Settings.FontSize -eq 44 -and $roundtrip.Settings.Keywords -eq '测试词' -and $roundtrip.Notice -eq '') 'Versioned preferences must roundtrip.'
}
if($failures.Count){ throw ($failures -join "`n") }
'PASS: preference compatibility, bounded corruption recovery and style-only persistence without starting any window.'
