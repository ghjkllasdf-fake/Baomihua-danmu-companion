#requires -Version 7.6
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
& npm.cmd --prefix (Join-Path $root 'ui') run build
if($LASTEXITCODE -ne 0){ throw 'React build failed.' }
& (Join-Path $root '.deps\pwsh\pwsh.exe') -NoProfile -STA -File (Join-Path $root 'desktop\Host.ps1') -Check
if($LASTEXITCODE -ne 0){ throw 'Native build failed.' }
