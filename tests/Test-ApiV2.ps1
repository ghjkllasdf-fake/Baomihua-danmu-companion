#requires -Version 7.6
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Add-Type -Path @((Join-Path $root 'desktop\native\Core.cs'),(Join-Path $root 'desktop\native\Api.cs'))
$legacyFixture=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Test-ApiRequest.ps1'))
$fixture=[regex]::Match($legacyFixture,"(?s)Add-Type -TypeDefinition @'\r?\n(.*?)\r?\n'@")
if(-not $fixture.Success){ throw 'Fixture unavailable.' }
Add-Type -TypeDefinition $fixture.Groups[1].Value
$server=[DanmuTestApi]::new()
$client=[Popcorn.ApiClient]::new()
try {
    $invalid=[System.Text.Json.Nodes.JsonNode]::Parse('{"animes":{"invalid":"not-an-array"}}')
    $invalidRejected=$false
    try { $null=[Popcorn.ApiClient]::NormalizeResponse('search',$invalid) }catch{ $invalidRejected=$true }
    if(-not $invalidRejected){ throw 'Malformed external search data was accepted.' }
    $mixed=[System.Text.Json.Nodes.JsonNode]::Parse('{"animes":[{"animeId":1,"animeTitle":"safe"},{"animeId":2,"animeTitle":{"invalid":true}}]}')
    $safe=[Popcorn.ApiClient]::NormalizeResponse('search',$mixed)
    if($safe['animes'].Count -ne 1){ throw 'Malformed external list items were not removed.' }
    $result=$client.FetchAsync($server.Base,'search','Fixture & test',[Threading.CancellationToken]::None).GetAwaiter().GetResult()
    if($result['animes'][0]['animeTitle'].ToString() -ne ([string][char]0x4e09+[char]0x4f53)){ throw 'UTF-8 title corrupted.' }
    $result=$client.FetchAsync($server.Base,'comments','episode/one',[Threading.CancellationToken]::None).GetAwaiter().GetResult()
    $comments=[Popcorn.ApiClient]::ParseComments($result)
    if($comments.Count -ne 2 -or $comments[0].Text -ne 'first'){ throw 'Comment parse/filter/order incorrect.' }
    $rejected=$false
    try { $null=$client.FetchAsync($server.Base,'search','api-failure',[Threading.CancellationToken]::None).GetAwaiter().GetResult() }catch{ $rejected=$true; if($_.Exception.Message.Contains($server.Base)){ throw 'Error leaked API address.' } }
    if(-not $rejected){ throw 'API failure was accepted.' }
    $cts=[Threading.CancellationTokenSource]::new(); $cts.Cancel()
    $cancelled=$false
    try { $null=$client.FetchAsync($server.Base,'search','slow',$cts.Token).GetAwaiter().GetResult() }catch{ $cancelled=$true }
    if(-not $cancelled){ throw 'Cancellation failed.' }
    'PASS: PS7 native API real HTTP, UTF-8, comment parsing, redacted errors and cancellation'
}finally{ $client.Dispose(); $server.Dispose() }
