#requires -Version 7.6
param(
    [ValidatePattern('^\d+\.\d+\.\d+-preview\.[1-9]\d*$')][string]$Version='0.1.0-preview.15',
    [string]$OutputDirectory
)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
if(-not $IsWindows -or -not [Environment]::Is64BitProcess){ throw 'Windows x64 is required.' }
if(-not $OutputDirectory){ $OutputDirectory=Join-Path $root 'artifacts\releases' }
$output=Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) ('DanmuCompanion-'+$Version+'-win-x64.zip')
if((Test-Path -LiteralPath $output) -or (Test-Path -LiteralPath ($output+'.sha256'))){ throw 'Preview destination already exists; choose a new version or output directory.' }
if((Get-FileHash -LiteralPath (Join-Path $root 'licenses/Windows-SDK.rtf')).Hash -ne 'DD07EB178E00C6BBA4148457FC00FF77CD4887EB521D504186FE59C9EC8BBE62'){ throw 'SDK license differs from the recorded original.' }
$package=Get-Content -LiteralPath (Join-Path $root 'ui\package.json') -Raw | ConvertFrom-Json
if(-not $Version.StartsWith($package.version+'-preview.',[StringComparison]::Ordinal)){ throw 'Preview version must match the UI package version.' }
& npm.cmd --prefix (Join-Path $root 'ui') run build
if($LASTEXITCODE -ne 0){ throw 'Current UI source did not build; no preview will be created.' }
Add-Type -Path (Join-Path $PSScriptRoot 'PackageArchive.cs')
$inputs=[Collections.Generic.List[Popcorn.Release.PackageInput]]::new()
function Add-ProjectFile([string]$source,[string]$destination){
    $inputs.Add([Popcorn.Release.PackageInput]::File($destination,(Join-Path $root $source)))
}
foreach($name in @('Host.ps1','Start.ps1','ProbeWorker.ps1')){ Add-ProjectFile ('desktop/'+$name) ('desktop/'+$name) }
foreach($file in Get-ChildItem -LiteralPath (Join-Path $root 'desktop\native') -Filter '*.cs' -File){
    Add-ProjectFile ('desktop/native/'+$file.Name) ('desktop/native/'+$file.Name)
}
foreach($file in Get-ChildItem -LiteralPath (Join-Path $root 'ui\dist') -Recurse -File){
    $relative=[IO.Path]::GetRelativePath($root,$file.FullName).Replace('\','/')
    Add-ProjectFile $relative $relative
}
Add-ProjectFile '启动弹幕助手新版.bat' '启动弹幕助手新版.bat'
Add-ProjectFile 'desktop/Verify-Package.bat' '检查文件.bat'
Add-ProjectFile 'scripts/Verify-Package.ps1' 'Verify-Package.ps1'
foreach($icon in @('app.ico','tray.ico')){Add-ProjectFile ('assets/'+$icon) ('assets/'+$icon)}
Add-ProjectFile 'docs/THIRD-PARTY-NOTICES.md' 'THIRD-PARTY-NOTICES.md'
Add-ProjectFile 'LICENSE' 'LICENSE'
Add-ProjectFile 'licenses/Windows-SDK.rtf' 'licenses/WindowsSDK/Windows-SDK.rtf'
$guide=[IO.File]::ReadAllText((Join-Path $root 'docs/PREVIEW-USER-GUIDE.md')).Replace('{{VERSION}}',$Version)
$inputs.Add([Popcorn.Release.PackageInput]::Text('README.md',$guide))
foreach($component in @(@('react','React'),@('react-dom','ReactDOM'),@('scheduler','Scheduler'))){
    Add-ProjectFile ('ui/node_modules/'+$component[0]+'/LICENSE') ('licenses/'+$component[1]+'/LICENSE.txt')
}
$runtimeArchive=Join-Path $root '.deps\PowerShell-7.6.6-win-x64.zip'
$webArchive=Join-Path $root '.deps\webview2.zip'
$mediaArchive=Join-Path $root '.deps\windows-sdk.zip'
$runtimeHash='02FE458BE20493FBDF43F61EA20610B811EE6C738AB1676C61B9CFCD1A33C860'
$webHash='90778DEB1B77E36292828B4B5833C9255C27533F40E0DDD55891AE718944EDC4'
$mediaHash='2EFF36ACCB9D76466D36E27E7199D375C662818D2C1376E65125D48BA4BD1C96'
foreach($dependency in @(@($runtimeArchive,$runtimeHash),@($webArchive,$webHash),@($mediaArchive,$mediaHash))){
    if((Get-FileHash -LiteralPath $dependency[0] -Algorithm SHA256).Hash -ne $dependency[1]){ throw 'A pinned dependency archive checksum does not match.' }
}
$runtime=[IO.Compression.ZipFile]::OpenRead($runtimeArchive)
try {
    foreach($entry in $runtime.Entries){
        if($entry.Name){ $inputs.Add([Popcorn.Release.PackageInput]::Zip(('.deps/pwsh/'+$entry.FullName),$runtimeArchive,$entry.FullName,$runtimeHash)) }
    }
} finally { $runtime.Dispose() }
$provenance=[ordered]@{Version='7.6.6';Sha256=$runtimeHash;Source='https://github.com/PowerShell/PowerShell/releases/download/v7.6.6/PowerShell-7.6.6-win-x64.zip'}|ConvertTo-Json -Compress
$inputs.Add([Popcorn.Release.PackageInput]::Text('.deps/pwsh/danmu-runtime.json',$provenance))
foreach($member in @('lib_manual/netcoreapp3.0/Microsoft.Web.WebView2.Core.dll','runtimes/win-x64/native/WebView2Loader.dll')){
    $inputs.Add([Popcorn.Release.PackageInput]::Zip(('.deps/webview2/'+$member),$webArchive,$member,$webHash))
}
foreach($member in @('LICENSE.txt','NOTICE.txt','Microsoft.Web.WebView2.nuspec')){
    $inputs.Add([Popcorn.Release.PackageInput]::Zip(('licenses/WebView2/'+$member),$webArchive,$member,$webHash))
}
foreach($member in @('lib/net8.0/WinRT.Runtime.dll','lib/net8.0/Microsoft.Windows.SDK.NET.dll')){
    $inputs.Add([Popcorn.Release.PackageInput]::Zip(('.deps/windows-sdk/'+$member),$mediaArchive,$member,$mediaHash))
}
$inputs.Add([Popcorn.Release.PackageInput]::Zip('licenses/WindowsSDK/Microsoft.Windows.SDK.NET.Ref.nuspec',$mediaArchive,'Microsoft.Windows.SDK.NET.Ref.nuspec',$mediaHash))
$needles=[Collections.Generic.List[string]]::new()
$legacy=Join-Path $root 'DanmuCompanion.ps1'
if(Test-Path -LiteralPath $legacy){
    $match=[regex]::Match([IO.File]::ReadAllText($legacy),'\$script:DefaultApiBase\s*=\s*''([^'']+)''')
    if($match.Success -and $match.Groups[1].Value){
        $needles.Add($match.Groups[1].Value)
        $uri=$null
        if([Uri]::TryCreate($match.Groups[1].Value,[UriKind]::Absolute,[ref]$uri)){
            foreach($part in $uri.Segments){ $value=[Uri]::UnescapeDataString($part).Trim('/'); if($value.Length -ge 12){ $needles.Add($value) } }
        }
    }
}
$staging=Join-Path $root ('.cache\preview-package-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($staging)
$stagedZip=Join-Path $staging ([IO.Path]::GetFileName($output))
$result=[Popcorn.Release.PackageArchive]::Create($root,$stagedZip,$Version,$inputs.ToArray(),$needles.ToArray())
$unpacked=Join-Path $staging 'unpacked'
[IO.Compression.ZipFile]::ExtractToDirectory($stagedZip,$unpacked)
function Invoke-PackagedCheck([string]$script,[string]$name,[switch]$HostCheck){
    $runtime=Join-Path $unpacked '.deps\pwsh\pwsh.exe'
    $info=[Diagnostics.ProcessStartInfo]::new($runtime)
    $info.UseShellExecute=$false; $info.CreateNoWindow=$true; $info.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
    $info.WorkingDirectory=$unpacked; $info.RedirectStandardOutput=$true; $info.RedirectStandardError=$true
    foreach($argument in @('-NoLogo','-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-STA','-File',$script)){ $info.ArgumentList.Add($argument) }
    if($HostCheck){ $info.ArgumentList.Add('-Check') }
    $info.Environment.Clear()
    foreach($key in @('SystemRoot','WINDIR','TEMP','TMP','USERPROFILE','LOCALAPPDATA','APPDATA','ProgramFiles','ProgramW6432')){
        $value=[Environment]::GetEnvironmentVariable($key); if($null -ne $value){ $info.Environment[$key]=$value }
    }
    $info.Environment['PATH']=Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::Windows)) 'System32'
    $info.Environment['PSModulePath']=Join-Path (Split-Path $runtime -Parent) 'Modules'
    $process=[Diagnostics.Process]::new(); $process.StartInfo=$info
    try {
        if(-not $process.Start()){ throw 'Packaged check could not start.' }
        $stdout=$process.StandardOutput.ReadToEndAsync(); $stderr=$process.StandardError.ReadToEndAsync()
        if(-not $process.WaitForExit(45000)){
            $process.Kill()
            if(-not $process.WaitForExit(5000)){ throw 'Owned packaged-check process exit is unconfirmed.' }
            throw 'Packaged check exceeded its deadline.'
        }
        [IO.File]::WriteAllText((Join-Path $staging ($name+'.out')),$stdout.GetAwaiter().GetResult())
        [IO.File]::WriteAllText((Join-Path $staging ($name+'.err')),$stderr.GetAwaiter().GetResult())
        if($process.ExitCode -ne 0){ throw ('Packaged check failed; inspect retained '+$name+' logs in '+$staging) }
    } finally { $process.Dispose() }
}
Invoke-PackagedCheck (Join-Path $unpacked 'Verify-Package.ps1') 'integrity'
Invoke-PackagedCheck (Join-Path $unpacked 'desktop\Host.ps1') 'host' -HostCheck
[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($output))
[IO.File]::Move($stagedZip,$output)
$hash=(Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant()
$hashStream=[IO.File]::Open($output+'.sha256',[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
$hashWriter=[IO.StreamWriter]::new($hashStream,[Text.UTF8Encoding]::new($false))
try { $hashWriter.WriteLine($hash+'  '+[IO.Path]::GetFileName($output)) } finally { $hashWriter.Dispose() }
[pscustomobject]@{package=$output;version=$result.Version;files=$result.Files;uncompressedBytes=$result.Bytes;sha256=$hash;validationDirectory=$staging;stage='preview; final acceptance incomplete'}
