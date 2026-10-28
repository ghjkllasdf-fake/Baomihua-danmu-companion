#requires -Version 7.6
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
. (Join-Path $root 'desktop\Host.ps1') -Check
function Assert([bool]$value,[string]$message){ if(-not $value){ throw $message } }
$describe=[Popcorn.DesktopWindow].GetMethod('DescribeInitializationFailure',[Reflection.BindingFlags]'Static,NonPublic')
Assert ($null -ne $describe) 'The desktop needs an actionable WebView2 startup recovery message.'
$source=[IO.File]::ReadAllText((Join-Path $root 'desktop\native\Desktop.cs'))
Assert ($source.Contains('Failure=DescribeInitializationFailure(ex);')) 'The actual initialization catch must use the recovery message.'

# Override only this test process; never uninstall or alter the installed Runtime.
# The pinned SDK XML documents WEBVIEW2_BROWSER_EXECUTABLE_FOLDER as an override.
$run=Join-Path $root ('.cache\runtime-recovery-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($run)
$before=[Environment]::GetEnvironmentVariable('WEBVIEW2_BROWSER_EXECUTABLE_FOLDER','Process')
$missing=Join-Path $run 'absent-runtime'
try {
    [Environment]::SetEnvironmentVariable('WEBVIEW2_BROWSER_EXECUTABLE_FOLDER',$missing,'Process')
    [Microsoft.Web.WebView2.Core.CoreWebView2Environment]::SetLoaderDllFolderPath((Join-Path $root '.deps\webview2\runtimes\win-x64\native'))
    $failure=$null
    try {
        $task=[Microsoft.Web.WebView2.Core.CoreWebView2Environment]::CreateAsync($null,(Join-Path $run 'profile'))
        $null=$task.WaitAsync([TimeSpan]::FromSeconds(10)).GetAwaiter().GetResult()
    } catch {
        $failure=$_.Exception
        # Strip only PowerShell's invocation wrapper. The SDK's own typed exception
        # may itself wrap FileNotFoundException and must reach the native classifier.
        if($failure -is [Management.Automation.MethodInvocationException]){ $failure=$failure.InnerException }
    }
    $failureDescription=if($null -eq $failure){'no exception'}else{$failure.GetType().FullName+' / '+$failure.HResult+' / '+$failure.Message}
    Assert ($failure -is [Microsoft.Web.WebView2.Core.WebView2RuntimeNotFoundException]) ('An absent fixed Runtime must reproduce the real SDK runtime-not-found failure; observed '+$failureDescription)
    $message=[string]$describe.Invoke($null,@($failure))
    foreach($part in @('Microsoft Edge WebView2 Runtime','微软官方网站','Evergreen','x64','https://developer.microsoft.com/microsoft-edge/webview2/','重新打开')){
        Assert ($message.Contains($part)) ('Missing Runtime recovery must explain: '+$part)
    }
    Assert ($message.Contains('保留的旧版文件夹') -and $message.Contains('启动弹幕助手.bat')) 'Missing Runtime recovery must identify the retained legacy folder and entry.'
    Assert (-not $message.Contains($missing)) 'Runtime recovery must not expose the test/profile path.'
    $generic=[string]$describe.Invoke($null,@([IO.IOException]::new('controlled generic failure')))
    Assert ($generic.Contains('controlled generic failure') -and $generic.Contains('检查文件.bat')) 'Other startup failures need their original detail and package recovery.'
    Assert (-not $generic.Contains('Evergreen')) 'Other startup failures must not be mislabeled as missing Runtime.'
    [pscustomobject]@{exceptionType=$failure.GetType().FullName;runtimeMessage=$message;genericMessage=$generic;scope='Actual SDK missing fixed Runtime; no visible dialog or installed Runtime removal.'} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run 'result.json')
} finally {
    [Environment]::SetEnvironmentVariable('WEBVIEW2_BROWSER_EXECUTABLE_FOLDER',$before,'Process')
}
'PASS: actual missing WebView2 Runtime has actionable official recovery; other failures keep package guidance. No installation or desktop input.'
