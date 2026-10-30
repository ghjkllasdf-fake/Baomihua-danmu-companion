#requires -Version 7.6
param([string]$Title='示例电视剧',[int]$Episode=17)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
if([string]::IsNullOrWhiteSpace($env:DANMU_API_BASE)){throw 'Set DANMU_API_BASE for this opt-in live service test; no built-in service is provided.'}
[void][IO.Directory]::CreateDirectory((Join-Path $root 'artifacts'))
$sources=@('desktop/native/Core.cs','desktop/native/Api.cs','desktop/native/SeriesIdentity.cs','desktop/native/AutomaticMatch.cs','tests/fixtures/AutomaticMatchLiveProof.cs')
Add-Type -Path @($sources | ForEach-Object {Join-Path $root $_})
$proof=[Popcorn.AutomaticMatchLiveProof]::Run($env:DANMU_API_BASE,$Title,$Episode).GetAwaiter().GetResult()
$proof | Set-Content -LiteralPath (Join-Path $root 'artifacts/automatic-match-live-service-20261028.json')
@($sources | ForEach-Object {$item=Join-Path $root $_;[pscustomobject]@{path=$_;sha256=(Get-FileHash -LiteralPath $item -Algorithm SHA256).Hash}}) |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'artifacts/automatic-match-live-service-sources-20261028.json')
$proof
