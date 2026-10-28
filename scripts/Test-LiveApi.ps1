#requires -Version 7.6
param([string]$Keyword='三体',[string]$TitlePattern='三体(2023)*tencent*')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Add-Type -Path @((Join-Path $root 'desktop\native\Core.cs'),(Join-Path $root 'desktop\native\Api.cs'))
if([string]::IsNullOrWhiteSpace($env:DANMU_API_BASE)){throw 'Set DANMU_API_BASE for this opt-in live service test; no built-in service is provided.'}
$client=[Popcorn.ApiClient]::new()
$stop=[Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(90))
$watch=[Diagnostics.Stopwatch]::StartNew()
try {
    $api=[Popcorn.ApiClient]::NormalizeBase($env:DANMU_API_BASE)
    $search=$client.FetchAsync($api,'search',$Keyword,$stop.Token).GetAwaiter().GetResult()
    $choices=@($search['animes'] | Where-Object { $_['animeTitle'].ToString() -like $TitlePattern })
    if(-not $choices.Count){ throw 'The requested source was not returned.' }
    $episodes=$client.FetchAsync($api,'episodes',$choices[0]['animeId'].ToString(),$stop.Token).GetAwaiter().GetResult()
    $list=$episodes['bangumi']['episodes']
    if(-not $list.Count){ throw 'The source had no episodes.' }
    $body=$client.FetchAsync($api,'comments',$list[0]['episodeId'].ToString(),$stop.Token).GetAwaiter().GetResult()
    $comments=[Popcorn.ApiClient]::ParseComments($body)
    if(-not $comments.Length){ throw 'The chosen episode returned no usable comments.' }
    $last=-1.0
    foreach($comment in $comments){
        if($comment.TimeMs -lt $last -or [string]::IsNullOrWhiteSpace($comment.Text)){ throw 'Parsed comments were not valid and sorted.' }
        $last=$comment.TimeMs
    }
    [pscustomobject]@{
        success=$true;scope='actual configured API only; no workbench/player input or rendering'
        keyword=$Keyword;searchResults=$search['animes'].Count;matchedSources=$choices.Count
        episodes=$list.Count;comments=$comments.Length;elapsedMs=[Math]::Round($watch.Elapsed.TotalMilliseconds)
        apiAddressLogged=$false;rawResponseSaved=$false
    }|ConvertTo-Json
} catch {
    # Do not let external service text or the configured endpoint reach retained logs.
    throw 'Configured API search/episodes/comments verification failed; endpoint and response details omitted.'
} finally {
    $stop.Cancel(); $stop.Dispose(); $client.Dispose()
}
