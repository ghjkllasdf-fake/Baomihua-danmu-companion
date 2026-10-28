#requires -Version 7.6
param([string]$TargetAppId='')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$sdk=Join-Path $root '.deps\windows-sdk\lib\net8.0'
Add-Type -Path (Join-Path $sdk 'WinRT.Runtime.dll')
Add-Type -Path (Join-Path $sdk 'Microsoft.Windows.SDK.NET.dll')
$managerType=[Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager]
$methods=@([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
    $_.Name -eq 'AsTask' -and $_.IsGenericMethodDefinition -and $_.GetGenericArguments().Length -eq 1 -and
    $_.GetParameters().Length -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
})
if($methods.Count -ne 1){ throw 'Expected one AsTask<T>(IAsyncOperation<T>) overload.' }
$operation=$managerType::RequestAsync()
$task=$methods[0].MakeGenericMethod($managerType).Invoke($null,@($operation))
$manager=$task.WaitAsync([TimeSpan]::FromSeconds(5)).GetAwaiter().GetResult()
$sessions=@($manager.GetSessions())
if(-not $TargetAppId){
    [pscustomobject]@{SessionCount=$sessions.Count;SourceAppIds=@($sessions | ForEach-Object SourceAppUserModelId | Sort-Object -Unique)} | ConvertTo-Json
    return
}
$target=@($sessions | Where-Object SourceAppUserModelId -CEQ $TargetAppId)
if($target.Count -ne 1){ throw 'An unambiguous target session was not found.' }
$info=$target[0].GetPlaybackInfo()
$timeline=$target[0].GetTimelineProperties()
$mediaType=[Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties]
$mediaTask=$methods[0].MakeGenericMethod($mediaType).Invoke($null,@($target[0].TryGetMediaPropertiesAsync()))
$media=$mediaTask.WaitAsync([TimeSpan]::FromSeconds(5)).GetAwaiter().GetResult()
[pscustomobject]@{SourceAppId=$TargetAppId;Title=$media.Title;Status=$info.PlaybackStatus.ToString();Rate=$info.PlaybackRate;PositionMs=$timeline.Position.TotalMilliseconds;DurationMs=$timeline.EndTime.TotalMilliseconds;Updated=$timeline.LastUpdatedTime} | ConvertTo-Json
