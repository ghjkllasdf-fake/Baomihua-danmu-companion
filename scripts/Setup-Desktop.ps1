#requires -Version 7.6
param([string]$PowerShellVersion = '7.6.6', [string]$WebViewVersion = '1.0.4314.35')
$ErrorActionPreference = 'Stop'
if (-not $IsWindows -or -not [Environment]::Is64BitProcess) { throw 'Windows x64 is required.' }
if($PowerShellVersion -ne '7.6.6' -or $WebViewVersion -ne '1.0.4314.35'){
    throw 'Dependency versions are pinned. Review checksums before changing them.'
}
$root = Split-Path $PSScriptRoot -Parent
$deps = Join-Path $root '.deps'
[void][IO.Directory]::CreateDirectory($deps)
$runtime = Join-Path $deps 'pwsh'
if (-not (Test-Path -LiteralPath (Join-Path $runtime 'pwsh.exe'))) {
    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/PowerShell/PowerShell/releases/tags/v$PowerShellVersion"
    $assetName = "PowerShell-$PowerShellVersion-win-x64.zip"
    $asset = $release.assets | Where-Object name -EQ $assetName | Select-Object -First 1
    $checksums = $release.assets | Where-Object name -Match 'hashes.sha256' | Select-Object -First 1
    if (-not $asset -or -not $checksums) { throw 'Official runtime archive/checksums not found.' }
    $archive = Join-Path $deps $assetName
    if (-not (Test-Path -LiteralPath $archive)) { Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $archive }
    $hashResponse = Invoke-WebRequest -Uri $checksums.browser_download_url
    $reader = [IO.StreamReader]::new($hashResponse.RawContentStream, [Text.Encoding]::UTF8, $true)
    try { $hashText = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $line = ($hashText -split '\r?\n') | Where-Object { $_.Contains($assetName) } | Select-Object -First 1
    if (-not $line) { throw 'Runtime archive absent from official checksum manifest.' }
    $expected = [regex]::Match($line, '[a-fA-F0-9]{64}').Value
    $actual = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
    if (-not $expected -or $actual -ne $expected -or $actual -ne '02FE458BE20493FBDF43F61EA20610B811EE6C738AB1676C61B9CFCD1A33C860') { throw 'Official pinned runtime checksum mismatch.' }
    Expand-Archive -LiteralPath $archive -DestinationPath $runtime
    [pscustomobject]@{ Version = $PowerShellVersion; Sha256 = $actual; Source = $asset.browser_download_url } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runtime 'danmu-runtime.json') -Encoding utf8
}
$sdk = Join-Path $deps 'webview2'
if (-not (Test-Path -LiteralPath $sdk)) {
    $archive = Join-Path $deps 'webview2.zip'
    Invoke-WebRequest -Uri "https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/$WebViewVersion/microsoft.web.webview2.$WebViewVersion.nupkg" -OutFile $archive
    if((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne '90778DEB1B77E36292828B4B5833C9255C27533F40E0DDD55891AE718944EDC4'){
        throw 'WebView2 SDK package checksum mismatch.'
    }
    Expand-Archive -LiteralPath $archive -DestinationPath $sdk
}
$mediaSdk=Join-Path $deps 'windows-sdk'
if (-not (Test-Path -LiteralPath $mediaSdk)) {
    $archive=Join-Path $deps 'windows-sdk.zip'
    Invoke-WebRequest -Uri 'https://api.nuget.org/v3-flatcontainer/microsoft.windows.sdk.net.ref/10.0.26100.84/microsoft.windows.sdk.net.ref.10.0.26100.84.nupkg' -OutFile $archive
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne '2EFF36ACCB9D76466D36E27E7199D375C662818D2C1376E65125D48BA4BD1C96') { throw 'Windows SDK package checksum mismatch.' }
    Expand-Archive -LiteralPath $archive -DestinationPath $mediaSdk
}
& (Join-Path $runtime 'pwsh.exe') -NoProfile -Command '$PSVersionTable.PSVersion.ToString()'
if ($LASTEXITCODE -ne 0) { throw 'Portable runtime validation failed.' }
Get-ChildItem -LiteralPath (Join-Path $sdk 'lib') -Directory | ForEach-Object { 'WebView2 target: ' + $_.Name }
Write-Output 'Desktop dependencies prepared. No system installation was changed.'
