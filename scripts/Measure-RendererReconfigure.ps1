#requires -Version 7.6
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$folder=Join-Path $root ('.cache\renderer-reconfigure-'+[guid]::NewGuid().ToString('N'))
foreach($part in @('scripts','tests\fixtures','ui\e2e','artifacts')){[void][IO.Directory]::CreateDirectory((Join-Path $folder $part))}
Copy-Item -LiteralPath (Join-Path $root 'desktop') -Destination $folder -Recurse
Copy-Item -LiteralPath (Join-Path $root 'assets') -Destination $folder -Recurse
[void][IO.Directory]::CreateDirectory((Join-Path $folder 'tests'))
Copy-Item -LiteralPath (Join-Path $root 'tests/Import-NativeTypes.ps1') -Destination (Join-Path $folder 'tests')
Copy-Item -LiteralPath (Join-Path $root 'tests/New-NativeTestApplication.ps1') -Destination (Join-Path $folder 'tests')
foreach($part in @('src','dist')){Copy-Item -LiteralPath (Join-Path $root ('ui\'+$part)) -Destination (Join-Path $folder 'ui') -Recurse}
Copy-Item (Join-Path $root 'ui\package.json') (Join-Path $folder 'ui')
foreach($name in @('SettingsLatency.mjs','Test-SettingsLatency.ps1')){Copy-Item (Join-Path $root ('scripts\'+$name)) (Join-Path $folder 'scripts')}
foreach($name in @('Run-SettingsLatencyHost.ps1','Test-SettingsLatencyEvidence.ps1','Test-SettingsLatencyOwnership.ps1','settings-latency.test.mjs')){
    Copy-Item (Join-Path $root ('tests\'+$name)) (Join-Path $folder 'tests')
}
Copy-Item (Join-Path $root 'tests\fixtures\SettingsLatencyObserver.cs') (Join-Path $folder 'tests\fixtures')
Copy-Item (Join-Path $root 'ui\e2e\settings-latency.mjs') (Join-Path $folder 'ui\e2e')
$null=New-Item -ItemType Junction -Path (Join-Path $folder '.deps') -Target (Join-Path $root '.deps')
$null=New-Item -ItemType Junction -Path (Join-Path $folder 'ui\node_modules') -Target (Join-Path $root 'ui\node_modules')
$target=Join-Path $folder 'desktop\native\Overlay.cs'
$source=[IO.File]::ReadAllText($target).Replace("`r`n","`n")
$edits=@(
    @('geometry=text.BuildGeometry(new Point(0,0));','geometry=RenderCostTrace.Measure("geometry",()=>text.BuildGeometry(new Point(0,0)));'),
    @('bitmap.Render(visual); return bitmap;','RenderCostTrace.Measure("bitmap",()=>bitmap.Render(visual)); return bitmap;')
)
if($source.Contains('public void StepFrame()')){
    $start="public void StepFrame()`n    {`n        if(stepping) return;`n        stepping=true;"
    $finish="} catch {CancelFrame();throw;}`n        finally {stepping=false;}"
    $edits+=@(@($start,$start+"`n        RenderCostTrace.Begin();"),@($finish,"} catch {CancelFrame();throw;}`n        finally {RenderCostTrace.End(active.Count);stepping=false;}"))
} else {
    $edits+=@(@("public void Frame()`n    {","public void Frame()`n    {`n        RenderCostTrace.Begin();"),
        @('MaxActive=Math.Max(MaxActive,active.Count);','MaxActive=Math.Max(MaxActive,active.Count); RenderCostTrace.End(active.Count);'))
}
foreach($edit in $edits){
    if(($source.Split($edit[0]).Count-1) -ne 1){throw ('Renderer timing boundary changed: '+$edit[0])}
    $source=$source.Replace($edit[0],$edit[1])
}
[IO.File]::WriteAllText($target,$source)
$tracePath=Join-Path $folder 'artifacts\render-cost.jsonl'
$helper=@'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
namespace Popcorn;
internal static class RenderCostTrace {
    private static double now=>Stopwatch.GetTimestamp()*1000.0/Stopwatch.Frequency;
    private static readonly StreamWriter log=new(new FileStream(@"TRACE_PATH",FileMode.CreateNew,FileAccess.Write,FileShare.Read)){AutoFlush=true};
    private static double started,geometryMs,bitmapMs;
    private static int geometries,bitmaps;
    public static void Begin(){started=now;geometryMs=bitmapMs=0;geometries=bitmaps=0;}
    public static T Measure<T>(string stage,Func<T> action){double begin=now;try{return action();}finally{Record(stage,now-begin);}}
    public static void Measure(string stage,Action action){double begin=now;try{action();}finally{Record(stage,now-begin);}}
    private static void Record(string stage,double elapsed){if(stage=="geometry"){geometryMs+=elapsed;geometries++;}else{bitmapMs+=elapsed;bitmaps++;}}
    public static void End(int active){double finished=now;log.WriteLine(JsonSerializer.Serialize(new{nativeMs=finished,frameMs=finished-started,geometryMs,bitmapMs,geometries,bitmaps,active}));}
}
'@
[IO.File]::WriteAllText((Join-Path $folder 'desktop\native\RenderCostTrace.cs'),$helper.Replace('TRACE_PATH',$tracePath.Replace('"','""')))
[pscustomobject]@{folder=$folder;trace=$tracePath;scope='Disposable exact-source copy with geometry/bitmap/frame timing; not production performance acceptance'}|
    ConvertTo-Json|Set-Content (Join-Path $root 'artifacts\renderer-reconfigure-latest-20261028.json')
& (Join-Path $root '.deps\pwsh\pwsh.exe') -NoProfile -ExecutionPolicy Bypass -STA -File (Join-Path $folder 'scripts\Test-SettingsLatency.ps1') -PlaybackLoad *> (Join-Path $folder 'artifacts\run.log')
$runExit=$LASTEXITCODE
[pscustomobject]@{folder=$folder;exitCode=$runExit;traceExists=(Test-Path -LiteralPath $tracePath);scope='Diagnostic only; a settings-gate failure is retained, not a passed acceptance'}|ConvertTo-Json
