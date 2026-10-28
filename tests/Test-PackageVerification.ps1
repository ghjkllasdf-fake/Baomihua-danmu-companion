#requires -Version 7.6
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$runtime=Join-Path $root '.deps\pwsh\pwsh.exe'
$suite=Join-Path $root ('.cache\package directory '+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($suite)
function New-Fixture {
    param([string]$RedirectTo)
    $folder=Join-Path $suite ([Guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory((Join-Path $folder 'desktop'))
    if($RedirectTo){ $null=New-Item -ItemType Junction -Path (Join-Path $folder 'desktop\native') -Target $RedirectTo }
    else { [void][IO.Directory]::CreateDirectory((Join-Path $folder 'desktop\native')) }
    Copy-Item -LiteralPath (Join-Path $root 'scripts\Verify-Package.ps1') -Destination $folder
    [IO.File]::WriteAllText((Join-Path $folder 'desktop\native\Core.cs'),'synthetic source')
    $files=@(Get-Item -LiteralPath (Join-Path $folder 'Verify-Package.ps1'),(Join-Path $folder 'desktop\native\Core.cs') | ForEach-Object {
        [pscustomobject]@{path=[IO.Path]::GetRelativePath($folder,$_.FullName).Replace('\','/');length=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()}
    })
    [pscustomobject]@{folder=$folder;manifest=[pscustomobject]@{schemaVersion=1;version='0.1.0-preview.1';preview=$true;files=$files}}
}
function Verify($fixture,[bool]$success,[string]$reason,[string]$expected=''){
    $fixture.manifest|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $fixture.folder 'manifest.json') -Encoding utf8
    $output=(& $runtime -NoProfile -ExecutionPolicy Bypass -File (Join-Path $fixture.folder 'Verify-Package.ps1') 2>&1|Out-String)
    if(($LASTEXITCODE -eq 0) -ne $success){ throw ('Directory verification result was wrong: '+$reason+'; '+$output) }
    if($expected -and -not $output.Contains($expected)){ throw ('Wrong directory rejection: '+$reason+'; '+$output) }
}
$valid=New-Fixture
Verify $valid $true 'unchanged synthetic package'
[void][IO.Directory]::CreateDirectory((Join-Path $valid.folder '.cache\app'))
[IO.File]::WriteAllText((Join-Path $valid.folder '.cache\app\preferences.json'),'synthetic user state')
Verify $valid $true 'runtime cache is outside the distribution manifest'
$private=New-Fixture
[void][IO.Directory]::CreateDirectory((Join-Path $private.folder '.cache'))
$privatePath=Join-Path $private.folder '.cache\private.txt'
[IO.File]::WriteAllText($privatePath,'must never be a distributed file')
$private.manifest.files+=@([pscustomobject]@{path='.cache/private.txt';length=(Get-Item $privatePath).Length;sha256=(Get-FileHash $privatePath).Hash.ToLowerInvariant()})
Verify $private $false 'a manifest cannot authorize shipping private cache' 'Invalid package file record'
$changed=New-Fixture
[IO.File]::WriteAllText((Join-Path $changed.folder 'desktop\native\Core.cs'),('x'*(Get-Item (Join-Path $changed.folder 'desktop\native\Core.cs')).Length))
Verify $changed $false 'changed payload'
$extra=New-Fixture
[IO.File]::WriteAllText((Join-Path $extra.folder 'extra.txt'),'unlisted')
Verify $extra $false 'unexpected file'
$escape=New-Fixture
$escape.manifest.files[0].path='../outside.txt'
Verify $escape $false 'escaping manifest path'
$duplicate=New-Fixture
$duplicate.manifest.files+=@($duplicate.manifest.files[0])
Verify $duplicate $false 'duplicate manifest records'
$unsupported=New-Fixture
$unsupported.manifest.preview=$false
Verify $unsupported $false 'unsupported channel'
$outside=Join-Path $suite 'outside'
[void][IO.Directory]::CreateDirectory($outside)
$redirect=New-Fixture -RedirectTo $outside
Verify $redirect $false 'redirected folder' 'redirected'
'PASS: actual directory checker accepts an intact package/cache and rejects private manifests, corruption, extras, escape, duplicates, unsupported channel and junctions.'
