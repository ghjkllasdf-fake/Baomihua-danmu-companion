#requires -Version 7.6
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
& (Join-Path $root '.deps\pwsh\pwsh.exe') -NoProfile -File (Join-Path $root 'tests\Test-PlaybackSnapshot.ps1') -WriteContractFixture
if($LASTEXITCODE -ne 0){ throw 'Native snapshot fixture generation failed.' }
& node (Join-Path $root 'tests\Check-PlaybackContract.mjs') (Join-Path $root '.cache\tests\native-playback-snapshots.json')
if($LASTEXITCODE -ne 0){ throw 'Native/UI playback snapshot contract failed.' }
