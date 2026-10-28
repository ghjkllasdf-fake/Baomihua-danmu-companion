#requires -Version 7.6
$root=Split-Path $PSScriptRoot -Parent
& (Join-Path $root '.deps\pwsh\pwsh.exe') -NoProfile -STA -File (Join-Path $root 'tests\Test-ApiV2.ps1')
if($LASTEXITCODE -ne 0){ throw 'PS7 API regression failed.' }
