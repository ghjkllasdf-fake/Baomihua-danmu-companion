#requires -Version 7.6
param(
    [Parameter(Mandatory)][string]$OutputPath,
    [ValidateRange(5,30)][int]$Seconds=15,
    [switch]$Worker
)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$OutputPath=[IO.Path]::GetFullPath($OutputPath)
if(-not $Worker){
    if(Test-Path -LiteralPath $OutputPath){ throw 'Choose a new diagnostic output file.' }
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($OutputPath))
    $paths=@('Core.cs','PlaybackModels.cs','SeriesIdentity.cs','LibrarySeasonReader.cs','CachedSeasonReader.cs','ProbeTransport.cs','ProbeOperation.cs','PlaybackEventSignal.cs','MediaSession.cs','Windows.cs','PlayerControlCache.cs','UiaPlayerControls.cs','PlayerSampler.cs','ProbeCadence.cs') |
        ForEach-Object {Join-Path $root ('desktop\native\'+$_)}
    $paths+=@($PSCommandPath)
    $before=@($paths | ForEach-Object {[pscustomobject]@{path=[IO.Path]::GetRelativePath($root,$_);sha256=(Get-FileHash -LiteralPath $_).Hash}})
    $info=[Diagnostics.ProcessStartInfo]::new((Join-Path $root '.deps\pwsh\pwsh.exe'))
    $info.UseShellExecute=$false; $info.CreateNoWindow=$true; $info.WindowStyle='Hidden'
    $info.RedirectStandardOutput=$true; $info.RedirectStandardError=$true
    foreach($argument in @('-NoProfile','-ExecutionPolicy','Bypass','-MTA','-File',$PSCommandPath,'-OutputPath',$OutputPath,'-Seconds',$Seconds,'-Worker')){ $info.ArgumentList.Add([string]$argument) }
    $info.Environment.Clear()
    foreach($key in @('SystemRoot','WINDIR','TEMP','TMP','USERPROFILE','LOCALAPPDATA','APPDATA','ProgramFiles','ProgramW6432')){
        $value=[Environment]::GetEnvironmentVariable($key); if($null -ne $value){ $info.Environment[$key]=$value }
    }
    $info.Environment['PATH']=Join-Path $env:SystemRoot 'System32'
    $process=[Diagnostics.Process]::new(); $process.StartInfo=$info
    try {
        if(-not $process.Start()){ throw 'Owned timing diagnostic did not start.' }
        [pscustomobject]@{pid=$process.Id;startTicks=$process.StartTime.ToUniversalTime().Ticks}|ConvertTo-Json|Set-Content ($OutputPath+'.owner.json')
        $stdout=$process.StandardOutput.ReadToEndAsync(); $stderr=$process.StandardError.ReadToEndAsync()
        $forced=-not $process.WaitForExit(($Seconds+15)*1000)
        if($forced){ $process.Kill(); if(-not $process.WaitForExit(5000)){ throw 'Owned timing diagnostic exit is unconfirmed.' } }
        [IO.File]::WriteAllText($OutputPath+'.out',$stdout.GetAwaiter().GetResult())
        [IO.File]::WriteAllText($OutputPath+'.err',$stderr.GetAwaiter().GetResult())
        $after=@($paths | ForEach-Object {[pscustomobject]@{path=[IO.Path]::GetRelativePath($root,$_);sha256=(Get-FileHash -LiteralPath $_).Hash}})
        $stable=($before|ConvertTo-Json -Compress) -ceq ($after|ConvertTo-Json -Compress)
        [pscustomobject]@{forcedStop=$forced;exitCode=$process.ExitCode;sourceStable=$stable;before=$before;after=$after}|ConvertTo-Json -Depth 4|Set-Content ($OutputPath+'.sources.json')
        if($forced -or $process.ExitCode -ne 0 -or -not $stable){ throw ('Probe timing diagnostic incomplete; inspect '+$OutputPath) }
    } finally { $process.Dispose() }
    'Completed read-only actual sampler timing; this standalone process is not the supervised-worker or presentation acceptance gate.'
    return
}
if([Threading.Thread]::CurrentThread.GetApartmentState() -ne 'MTA'){ throw 'Timing worker requires MTA.' }
$sdk=Join-Path $root '.deps\windows-sdk\lib\net8.0'
foreach($name in @('WindowsBase','UIAutomationClient','UIAutomationTypes')){ Add-Type -AssemblyName $name }
Add-Type -LiteralPath (Join-Path $sdk 'WinRT.Runtime.dll')
Add-Type -LiteralPath (Join-Path $sdk 'Microsoft.Windows.SDK.NET.dll')
$references=@(Get-ChildItem -LiteralPath (Join-Path $PSHOME 'ref') -Filter '*.dll'|ForEach-Object FullName)
$references+=@(Get-ChildItem -LiteralPath $PSHOME -Filter '*.dll'|Where-Object {$_.Name -match '^(WindowsBase|UIAutomation|System\.Windows)'}|ForEach-Object FullName)
$paths=@('Core.cs','PlaybackModels.cs','SeriesIdentity.cs','LibrarySeasonReader.cs','CachedSeasonReader.cs','ProbeTransport.cs','ProbeOperation.cs','PlaybackEventSignal.cs','MediaSession.cs','Windows.cs','PlayerControlCache.cs','UiaPlayerControls.cs','PlayerSampler.cs','ProbeCadence.cs') |
    ForEach-Object {Join-Path $root ('desktop\native\'+$_)}
$fixture=@'
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows.Automation;
namespace Popcorn;
public static class ProbeTimingDiagnostic {
    public static void Run(string path,int seconds) {
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(seconds+3));
        using var log=new StreamWriter(new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read)) {AutoFlush=true};
        double discoverMs=0,readMs=0; int discoveries=0,reads=0;
        var perId=new Dictionary<string,double>();
        var controls=new PlayerControlCache<AutomationElement>(window=>{
            double began=Monotonic.Now; try {discoveries++;return UiaPlayerControls.Discover(window,stop.Token);}
            finally {discoverMs+=Monotonic.Now-began;}
        },element=>{
            double began=Monotonic.Now;
            var value=UiaPlayerControls.Read(element);
            double elapsed=Monotonic.Now-began; readMs+=elapsed; reads++;
            string key=string.IsNullOrEmpty(value.Id) ? "(unnamed)" : value.Id;
            perId[key]=perId.GetValueOrDefault(key)+elapsed; return value;
        },()=>Monotonic.Now,stop.Token);
        using var sampler=new PlayerSampler(stop.Token,controls);
        double started=Monotonic.Now;
        log.WriteLine(JsonSerializer.Serialize(new{type="started",nativeMs=started,pid=Environment.ProcessId,
            scope="Actual sampler/cache/UIA callbacks; standalone MTA, no owned production Job, no player input."}));
        while(Monotonic.Now-started<seconds*1000) {
            discoverMs=readMs=0; discoveries=reads=0; perId.Clear();
            double began=Monotonic.Now;
            try {
                var sample=sampler.Poll(); double finished=Monotonic.Now;
                log.WriteLine(JsonSerializer.Serialize(new{type="poll",nativeMs=finished,elapsedMs=finished-began,discoverMs,readMs,
                    otherMs=finished-began-discoverMs-readMs,discoveries,reads,perId,source=sample.Source,playing=sample.Playing,
                    positionMs=sample.PositionMs,proofAgeMs=finished-sample.StampMs}));
                sampler.WaitForNext(ProbeCadence.DelayMilliseconds(began,finished));
            } catch(Exception error) {log.WriteLine(JsonSerializer.Serialize(new{type="failure",nativeMs=Monotonic.Now,exception=error.GetType().Name}));throw;}
        }
        log.WriteLine(JsonSerializer.Serialize(new{type="closed",nativeMs=Monotonic.Now}));
    }
}
'@
$fixturePath=$OutputPath+'.fixture.cs'
$fixtureStream=[IO.File]::Open($fixturePath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
$fixtureWriter=[IO.StreamWriter]::new($fixtureStream)
try { $fixtureWriter.Write($fixture) } finally { $fixtureWriter.Dispose() }
Add-Type -LiteralPath @($paths+$fixturePath) -ReferencedAssemblies ($references|Select-Object -Unique)
[Popcorn.ProbeTimingDiagnostic]::Run($OutputPath,$Seconds)
