#requires -Version 7.6
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$folder=Join-Path $root ('.cache\privacy-review-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory((Join-Path $folder 'scripts'))
[void][IO.Directory]::CreateDirectory((Join-Path $folder 'ui'))
Copy-Item -LiteralPath (Join-Path $root 'desktop') -Destination $folder -Recurse
Copy-Item -LiteralPath (Join-Path $root 'ui\dist') -Destination (Join-Path $folder 'ui') -Recurse
Copy-Item -LiteralPath (Join-Path $root 'scripts\Test-WebViewPrivacy.ps1') -Destination (Join-Path $folder 'scripts')
$null=New-Item -ItemType Junction -Path (Join-Path $folder '.deps') -Target (Join-Path $root '.deps')
$target=Join-Path $folder 'desktop\native\Desktop.cs'
$source=[IO.File]::ReadAllText($target)
foreach($name in @('IsGeneralAutofillEnabled','IsPasswordAutosaveEnabled')){
    $line='settings.'+$name+'=false;'
    if(-not $source.Contains($line)){ throw 'Privacy mutation no longer matches the source.' }
    [IO.File]::WriteAllText($target,$source.Replace($line,''))
    $output=(& (Join-Path $root '.deps\pwsh\pwsh.exe') -NoProfile -ExecutionPolicy Bypass -STA -File (Join-Path $folder 'scripts\Test-WebViewPrivacy.ps1') 2>&1 | Out-String)
    if($LASTEXITCODE -eq 0){ throw ('Privacy mutation survived: '+$name) }
    $run=Get-ChildItem -LiteralPath (Join-Path $folder '.cache') -Directory -Filter 'privacy-check-*' | Sort-Object LastWriteTime | Select-Object -Last 1
    $logs=@(Get-ChildItem -LiteralPath $run.FullName -Filter '*.err' | ForEach-Object {[IO.File]::ReadAllText($_.FullName)}) -join "`n"
    [IO.File]::WriteAllText((Join-Path $folder ($name+'.log')),$output+"`n"+$logs)
    if(-not $logs.Contains('The actual WebView2 profile permits form/password saving.')){ throw ('Privacy mutation failed for another reason: '+$name+'; inspect '+$folder) }
    'PASS: rejected actual WebView2 privacy mutation '+$name
}
'Privacy review retained at '+$folder
