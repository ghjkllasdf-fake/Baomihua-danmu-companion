#requires -Version 7.6
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Add-Type -Path (Join-Path $root 'desktop/native/StartupRegistration.cs')
function Assert($condition,[string]$message){if(-not $condition){throw $message}}
$folder=Join-Path $root ('.cache/startup-registration-'+[Guid]::NewGuid().ToString('N'))
$app=Join-Path $folder '示例应用 (portable)'
$startup=Join-Path $folder 'isolated-startup'
[void][IO.Directory]::CreateDirectory((Join-Path $app '.deps/pwsh'))
[void][IO.Directory]::CreateDirectory((Join-Path $app 'desktop'))
[void][IO.Directory]::CreateDirectory((Join-Path $app 'assets'))
Copy-Item -LiteralPath (Join-Path $root 'assets/app.ico') -Destination (Join-Path $app 'assets/app.ico')
[IO.File]::WriteAllText((Join-Path $app '.deps/pwsh/pwsh.exe'),'controlled placeholder; never executed')
[IO.File]::WriteAllText((Join-Path $app 'desktop/Start.ps1'),'# controlled placeholder; never executed')
$registration=[Popcorn.StartupRegistration]::new($app,$startup)
Assert (-not $registration.Enabled -and -not(Test-Path -LiteralPath $startup)) 'Reading defaults must not create a shortcut or startup folder.'
Assert ($registration.SetEnabled($true) -and $registration.Enabled) 'Opt-in must create an effective startup shortcut.'
$shortcutPath=Join-Path $startup '爆米花弹幕伴侣.lnk'
$shell=New-Object -ComObject WScript.Shell
$shortcut=$null
try {
    $shortcut=$shell.CreateShortcut($shortcutPath)
    Assert ($shortcut.TargetPath -eq (Join-Path $app '.deps/pwsh/pwsh.exe')) 'Startup must target the bundled runtime, including paths with spaces and Chinese text.'
    Assert ($shortcut.Arguments -eq ('-NoLogo -NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File "'+(Join-Path $app 'desktop/Start.ps1')+'"')) 'Startup arguments must quote the fixed entry script and hide the console.'
    Assert ($shortcut.WorkingDirectory -eq $app -and $shortcut.WindowStyle -eq 7) 'Startup must use the portable app directory and avoid opening a console window.'
    Assert ($shortcut.IconLocation -eq ((Join-Path $app 'assets/app.ico')+',0')) 'Startup must use the application icon instead of the PowerShell icon.'
} finally {
    if($shortcut){[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut)}
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)
}
Assert ($registration.SetEnabled($true)) 'Re-enabling must be idempotent.'
Assert (@(Get-ChildItem -LiteralPath $startup -File).Count -eq 1) 'Saving startup must not leave temporary shortcuts.'
$other=[Popcorn.StartupRegistration]::new((Join-Path $folder 'another-version'),$startup)
Assert (-not $other.Enabled) 'Another folder must not claim this folder starts at login.'
Assert (-not $registration.SetEnabled($false) -and -not(Test-Path -LiteralPath $shortcutPath)) 'Opt-out must remove the application shortcut.'
Assert (-not $registration.SetEnabled($false)) 'Repeated opt-out must remain safe.'
$incompleteStartup=Join-Path $folder 'incomplete-startup'
$incomplete=[Popcorn.StartupRegistration]::new((Join-Path $folder 'incomplete'),$incompleteStartup)
$rejected=$false
try{$null=$incomplete.SetEnabled($true)}catch{$rejected=$true;Assert (-not $_.Exception.Message.Contains($folder)) 'Failure must not reveal the private path.'}
Assert ($rejected -and -not(Test-Path -LiteralPath $incompleteStartup)) 'Incomplete packages must not register an unusable startup entry.'
$blocked=Join-Path $folder 'blocked'
[IO.File]::WriteAllText($blocked,'occupied')
$rejected=$false
try{$null=([Popcorn.StartupRegistration]::new($app,$blocked)).SetEnabled($true)}catch{$rejected=$true;Assert (-not $_.Exception.Message.Contains($folder)) 'Storage failure must be sanitized.'}
Assert $rejected 'An inaccessible startup destination must report failure.'
'PASS: opt-in/out, actual isolated Windows shortcut, quoted paths, portable runtime, idempotency, incomplete package and sanitized storage failure; user Startup folder untouched.'
