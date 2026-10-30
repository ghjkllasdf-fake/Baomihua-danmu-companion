#requires -Version 7.6
# UI harness for dynamically compiled source tests; no EXE, API or user profile is copied.
$project=Split-Path $PSScriptRoot -Parent
$application=Join-Path $project ('.cache/native-test-app-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory((Join-Path $application 'assets'))
[void][IO.Directory]::CreateDirectory((Join-Path $application 'ui'))
foreach($icon in @('app.ico','tray.ico')){
    Copy-Item -LiteralPath (Join-Path $project ('assets/'+$icon)) -Destination (Join-Path $application 'assets')
}
Copy-Item -LiteralPath (Join-Path $project 'ui/dist') -Destination (Join-Path $application 'ui/dist') -Recurse
Copy-Item -LiteralPath (Join-Path $project '.deps/webview2/runtimes/win-x64/native/WebView2Loader.dll') -Destination $application
$application
