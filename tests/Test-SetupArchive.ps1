#requires -Version 7.6
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$folder=Join-Path $root ('.cache\setup archive '+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory((Join-Path $folder 'scripts'))
[void][IO.Directory]::CreateDirectory((Join-Path $folder '.deps'))
$runtime=Join-Path $root '.deps\pwsh'
$null=New-Item -ItemType Junction -Path (Join-Path $folder '.deps\pwsh') -Target $runtime
Copy-Item -LiteralPath (Join-Path $root 'scripts\Setup-Desktop.ps1') -Destination (Join-Path $folder 'scripts')
$webArchive=Join-Path $root '.deps\webview2.zip'
$mediaArchive=Join-Path $root '.deps\windows-sdk.zip'
if((Get-FileHash -LiteralPath $webArchive).Hash -ne '90778DEB1B77E36292828B4B5833C9255C27533F40E0DDD55891AE718944EDC4'){ throw 'WebView archive differs from the migrated pinned package.' }
if((Get-FileHash -LiteralPath $mediaArchive).Hash -ne '2EFF36ACCB9D76466D36E27E7199D375C662818D2C1376E65125D48BA4BD1C96'){ throw 'Windows SDK archive differs from the pinned package.' }
$taskSetupRequests=[Collections.Generic.List[string]]::new()
# Replace only network I/O; execute the actual Setup and its archive extraction unchanged.
function Invoke-WebRequest {
    param([string]$Uri,[string]$OutFile)
    $taskSetupRequests.Add($Uri)
    if($Uri -ceq 'https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/1.0.4314.35/microsoft.web.webview2.1.0.4314.35.nupkg'){
        [IO.File]::Copy($webArchive,$OutFile)
    } elseif($Uri -ceq 'https://api.nuget.org/v3-flatcontainer/microsoft.windows.sdk.net.ref/10.0.26100.84/microsoft.windows.sdk.net.ref.10.0.26100.84.nupkg'){
        [IO.File]::Copy($mediaArchive,$OutFile)
    } else { throw 'Unexpected network request in the offline Setup fixture.' }
}
& (Join-Path $folder 'scripts\Setup-Desktop.ps1')
if($taskSetupRequests.Count -ne 2){ throw 'Setup did not retrieve both SDK archives in the isolated deployment.' }
$core=Join-Path $folder '.deps\webview2\lib_manual\netcoreapp3.0\Microsoft.Web.WebView2.Core.dll'
if(-not (Test-Path -LiteralPath $core) -or (Get-FileHash -LiteralPath $core).Hash -ne 'ACDD517B951829F763D1255F32DF1E6A936CB8623A7EBFF6B8B24172DEDE6BD6'){
    throw 'Actual Setup did not reproduce the modern WebView2 Core DLL from the package.'
}
Copy-Item -LiteralPath (Join-Path $root 'desktop') -Destination $folder -Recurse
[void][IO.Directory]::CreateDirectory((Join-Path $folder 'tests'))
Copy-Item -LiteralPath (Join-Path $root 'tests/Import-NativeTypes.ps1') -Destination (Join-Path $folder 'tests')
Copy-Item -LiteralPath (Join-Path $root 'tests/New-NativeTestApplication.ps1') -Destination (Join-Path $folder 'tests')
[void][IO.Directory]::CreateDirectory((Join-Path $folder 'ui'))
Copy-Item -LiteralPath (Join-Path $root 'ui\dist') -Destination (Join-Path $folder 'ui') -Recurse
& (Join-Path $runtime 'pwsh.exe') -NoProfile -STA -File (Join-Path $folder 'tests/Import-NativeTypes.ps1')
if($LASTEXITCODE -ne 0){ throw 'The actual Host rejected the cold-extracted SDKs.' }
'PASS: unchanged Setup creates lib_manual and both SDKs; actual Host compiles with cold-extracted DLLs. Runtime reused, network mocked.'
