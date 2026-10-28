#requires -Version 7.6
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$runtime=Join-Path $root '.deps\pwsh\pwsh.exe'
$launcher=[IO.File]::ReadAllText((Join-Path $root '启动弹幕助手新版.bat'))
function Assert($condition,[string]$message){ if(-not $condition){ throw $message } }
Assert ($launcher.Contains('if not exist "%PWSH%"')) 'Missing portable runtime must have a visible launcher failure path.'
Assert (-not ($launcher -match '(?i)powershell\.exe|\.codex\\|\.codex/')) 'The new launcher must not fall back to PS5 or depend on a private tool runtime.'
$preflight=[regex]::Match($launcher,'(?m)^"%PWSH%" -NoLogo -NoProfile -NonInteractive -Command "(?<code>[^"\r\n]+)"')
Assert $preflight.Success 'The hidden launcher needs an explicit runtime preflight before starting the application.'
$code=$preflight.Groups['code'].Value
& $runtime -NoProfile -NonInteractive -Command $code
Assert ($LASTEXITCODE -eq 0) 'The installed portable PS7.6 x64 runtime must pass launcher preflight.'
& $runtime -NoProfile -STA -File (Join-Path $root 'desktop\Host.ps1') -Check
Assert ($LASTEXITCODE -eq 0) 'The actual compiled Vite bundle and installed dependencies must pass startup validation.'
& powershell.exe -NoProfile -NonInteractive -Command $code
Assert ($LASTEXITCODE -ne 0) 'A PS5 executable placed at the portable runtime path must be rejected before the hidden application starts.'
foreach($part in @('[Environment]::Is64BitProcess',"[Environment]::OSVersion.Platform -eq 'Win32NT'")){
    Assert ($code.Contains($part)) 'Runtime preflight must check Windows and x64.'
    & $runtime -NoProfile -NonInteractive -Command ($code.Replace($part,'$false'))
    Assert ($LASTEXITCODE -ne 0) 'The actual preflight must reject an unsupported platform/architecture.'
}
$cache=Join-Path $root ('.cache\startup-'+[Guid]::NewGuid().ToString('N'))
$desktop=Join-Path $cache 'desktop'
[void][IO.Directory]::CreateDirectory($desktop)
$hostPath=Join-Path $desktop 'Host.ps1'
[IO.File]::WriteAllText($hostPath,[IO.File]::ReadAllText((Join-Path $root 'desktop\Host.ps1')))
function RejectHost([string]$expected){
    $output=(& $runtime -NoProfile -STA -File $hostPath -Check 2>&1 | Out-String)
    Assert ($LASTEXITCODE -ne 0) 'An incomplete deployment was accepted.'
    Assert ($output.Contains($expected)) ('Startup did not identify the missing component: '+$expected)
}
RejectHost 'React assets missing'
$dist=Join-Path $cache 'ui\dist'
[void][IO.Directory]::CreateDirectory($dist)
[IO.File]::WriteAllText((Join-Path $dist 'index.html'),'<html><script type="module" src="/assets/missing.js"></script></html>')
RejectHost 'React asset missing'
[void][IO.Directory]::CreateDirectory((Join-Path $dist 'assets'))
[IO.File]::WriteAllText((Join-Path $dist 'assets\missing.js'),'export {};')
RejectHost 'Probe runtime missing'
$probeRuntime=Join-Path $cache '.deps\pwsh'; [void][IO.Directory]::CreateDirectory($probeRuntime)
[IO.File]::WriteAllText((Join-Path $probeRuntime 'pwsh.exe'),'fixture-only, never executed')
RejectHost 'Probe worker missing'
[IO.File]::WriteAllText((Join-Path $desktop 'ProbeWorker.ps1'),'# fixture-only, never executed')
RejectHost 'WebView2 SDK missing'
[IO.File]::WriteAllText((Join-Path $dist 'index.html'),'<html><script type="module" src="./assets/missing.js"></script></html>')
RejectHost 'WebView2 SDK missing'
$output=(& powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File (Join-Path $root 'desktop\Host.ps1') -Check 2>&1 | Out-String)
Assert ($LASTEXITCODE -ne 0 -and $output.Contains('7.6')) 'The PS7 application must not run under Windows PowerShell 5.1.'
$output=(& $runtime -NoProfile -STA -File (Join-Path $root 'desktop\Host.ps1') -Check -DataProfile '../outside' 2>&1 | Out-String)
Assert ($LASTEXITCODE -ne 0) 'An escaping data profile name must be rejected.'
'PASS: launcher/runtime, PS5 rejection, Windows/x64 guards, missing assets/dependencies and profile path boundary'
