#requires -Version 7.6
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Add-Type -AssemblyName System.Security.Cryptography.ProtectedData
Add-Type -Path @((Join-Path $root 'desktop/native/Core.cs'),(Join-Path $root 'desktop/native/Api.cs'),(Join-Path $root 'desktop/native/ApiPreferences.cs')) -ReferencedAssemblies (@(Get-ChildItem (Join-Path $PSHOME 'ref') -Filter '*.dll'|ForEach-Object FullName)+(Join-Path $PSHOME 'System.Security.Cryptography.ProtectedData.dll'))
function Assert($condition,[string]$message){if(-not $condition){throw $message}}
$folder=Join-Path $root ('.cache/api-store-test-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($folder)
$path=Join-Path $folder 'api.dat'
$address='https://danmu.example.com/EXAMPLE_TOKEN_ONLY'
$missing=[Popcorn.ApiPreferences]::Load($path)
Assert (-not $missing.Saved -and $missing.ApiBase -eq '' -and $missing.Notice -eq '') 'Missing configuration must start empty.'
[Popcorn.ApiPreferences]::Save($path,($address+'/api/v2/'))
$bytes=[IO.File]::ReadAllBytes($path)
Assert (-not [Text.Encoding]::UTF8.GetString($bytes).Contains('EXAMPLE_TOKEN_ONLY')) 'The saved file must not contain a plaintext token.'
$loaded=[Popcorn.ApiPreferences]::Load($path)
Assert ($loaded.Saved -and $loaded.ApiBase -ceq $address) 'A new reader must restore the normalized address using current-user DPAPI.'
$before=[Convert]::ToHexString($bytes)
try{[Popcorn.ApiPreferences]::Save($path,'file:///not-an-api');throw 'Invalid API accepted.'}catch{if($_.Exception.Message -like '*Invalid API accepted*'){throw}}
Assert ([Convert]::ToHexString([IO.File]::ReadAllBytes($path)) -ceq $before) 'Invalid replacement must preserve the prior saved file.'
$bytes[20]=$bytes[20] -bxor 127
[IO.File]::WriteAllBytes($path,$bytes)
$broken=[Popcorn.ApiPreferences]::Load($path)
Assert (-not $broken.Saved -and $broken.ApiBase -eq '' -and $broken.Notice -ne '' -and -not $broken.Notice.Contains('EXAMPLE_TOKEN_ONLY')) 'Tampered data must fail closed with a generic notice.'
Assert ([IO.File]::Exists($path)) 'A failed read must preserve the original file.'
[IO.File]::WriteAllBytes($path,[byte[]]::new(16385))
Assert (-not [Popcorn.ApiPreferences]::Load($path).Saved) 'Oversized encrypted data must be rejected.'
[Popcorn.ApiPreferences]::Save($path,$address)
$locked=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
try{
    $failed=$false
    try{[Popcorn.ApiPreferences]::Save($path,'https://danmu.example.com/NEW_EXAMPLE')}catch{$failed=$true}
    Assert $failed 'A blocked write must report failure.'
}finally{$locked.Dispose()}
Assert ([Popcorn.ApiPreferences]::Load($path).ApiBase -ceq $address) 'A failed write must preserve the prior configuration.'
[Popcorn.ApiPreferences]::Save($path,'')
Assert (-not [IO.File]::Exists($path) -and -not [Popcorn.ApiPreferences]::Load($path).Saved) 'Saving an empty address must remove the saved secret.'
'PASS: current-user DPAPI roundtrip, no plaintext, normalization, tamper/size rejection, failed-write preservation and explicit removal.'
