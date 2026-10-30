#requires -Version 7.6
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Import-NativeTypes.ps1')
foreach($uri in @('https://danmu.local/','https://danmu.local:443/index.html')){
    if(-not [Popcorn.DesktopWindow]::IsAppSource($uri)){ throw 'Valid local origin rejected.' }
}
foreach($uri in @('http://danmu.local/','https://danmu.local.evil/','https://danmu.local:444/','https://user:password@danmu.local/','file:///index.html','about:blank')){
    if([Popcorn.DesktopWindow]::IsAppSource($uri)){ throw ('Untrusted origin accepted: '+$uri) }
}
'PASS: exact native bridge source boundary rejects scheme/host/port/userinfo/file/blank variants'
