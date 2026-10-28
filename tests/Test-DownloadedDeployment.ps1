#requires -Version 7.6
param([ValidateSet('All','Launcher','Probe')][string]$Group='All')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$runtime=Join-Path $root '.deps\pwsh\pwsh.exe'
$folder=Join-Path $root ('.cache\downloaded deployment '+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory((Join-Path $folder 'desktop'))
[void][IO.Directory]::CreateDirectory((Join-Path $folder '.deps'))
# Reuse the full runtime without copying or changing it. Keep this isolated fixture for diagnosis.
$null=New-Item -ItemType Junction -Path (Join-Path $folder '.deps\pwsh') -Target (Split-Path $runtime -Parent)
$launcher=Join-Path $folder '启动弹幕助手新版.bat'
[IO.File]::WriteAllBytes($launcher,[IO.File]::ReadAllBytes((Join-Path $root '启动弹幕助手新版.bat')))
$marker=Join-Path $folder 'started.txt'
$entry=Join-Path $folder 'desktop\Start.ps1'
[IO.File]::WriteAllText($entry,('[IO.File]::WriteAllText('''+$marker.Replace("'","''")+''',"started")'))
Set-Content -LiteralPath $entry -Stream Zone.Identifier -Value "[ZoneTransfer]`r`nZoneId=3"
$priorPolicy=$env:PSExecutionPolicyPreference
if($Group -in @('All','Launcher')){
try {
    $env:PSExecutionPolicyPreference='RemoteSigned'
    $shell=Start-Process -FilePath $env:ComSpec -ArgumentList @('/d','/c',('""'+$launcher+'""')) -WindowStyle Hidden -PassThru
    try {
        if(-not $shell.WaitForExit(10000)){ throw 'Isolated launcher did not finish.' }
        $watch=[Diagnostics.Stopwatch]::StartNew()
        while(-not [IO.File]::Exists($marker) -and $watch.ElapsedMilliseconds -lt 8000){ Start-Sleep -Milliseconds 50 }
        if(-not [IO.File]::Exists($marker)){ throw 'Downloaded deployment: the actual BAT did not execute its Internet-marked entry point under RemoteSigned.' }
    } finally { $shell.Dispose() }
} finally { $env:PSExecutionPolicyPreference=$priorPolicy }
'PASS: actual BAT starts a downloaded script under RemoteSigned without changing system policy.'
}

if($Group -in @('All','Probe')){
$assembly=Join-Path $folder 'Fixture.dll'
$paths=@('Core.cs','PlaybackModels.cs','ProbeTransport.cs','ProbeFrameReader.cs','ProbeJob.cs','ProbeGate.cs','ProbeProcess.cs') | ForEach-Object {Join-Path $root ('desktop\native\'+$_)}
$paths+=Join-Path $PSScriptRoot 'fixtures\ProbeProcessTests.cs'
Add-Type -Path $paths -OutputAssembly $assembly
Add-Type -LiteralPath $assembly
$probeScript=Join-Path $folder 'Downloaded Probe.ps1'
[IO.File]::WriteAllBytes($probeScript,[IO.File]::ReadAllBytes((Join-Path $PSScriptRoot 'fixtures\ProbeCli.ps1')))
Set-Content -LiteralPath $probeScript -Stream Zone.Identifier -Value "[ZoneTransfer]`r`nZoneId=3"
$null=[Popcorn.ProbeProcessTests]::DownloadedScript($runtime,$probeScript,$assembly).GetAwaiter().GetResult()
'PASS: actual owned probe executes downloaded script through its gate, returns a valid frame and is reaped.'
}
