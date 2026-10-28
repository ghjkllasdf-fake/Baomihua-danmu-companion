#requires -Version 7.6
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
# The harness inserts the actual host handlers; storage, renderer and player are in-memory fakes.
. (Join-Path $root 'tests\Test-DesktopState.ps1') -Group Settings
$desktop=[Popcorn.DesktopHarness]::new()
try{
    $desktop.Message('contract-hello','hello',@{})
    $initial=$desktop.Reply('contract-hello').GetProperty('data').Clone()
    $settings=[Popcorn.DanmakuSettings]::new(); $settings.FontSize=40
    $desktop.Message('contract-save','update-settings',@{ settings=$settings })
    $saved=$desktop.Reply('contract-save').Clone()
    $desktop.Message('contract-read','get-settings',@{})
    $read=$desktop.Reply('contract-read').GetProperty('data').Clone()
    $snapshots=[System.Text.Json.JsonElement[]]@($initial,$saved,$read)
    $folder=Join-Path $root '.cache\tests'; [void][IO.Directory]::CreateDirectory($folder)
    $path=Join-Path $folder 'native-settings-snapshots.json'
    $json=[System.Text.Json.JsonSerializer]::Serialize($snapshots,$snapshots.GetType(),[System.Text.Json.JsonSerializerOptions]::new())
    [IO.File]::WriteAllText($path,$json)
    & node (Join-Path $root 'tests\Check-SettingsContract.mjs') $path
    if($LASTEXITCODE -ne 0){ throw 'Native/UI settings contract failed.' }
}finally{ $desktop.Dispose() }
