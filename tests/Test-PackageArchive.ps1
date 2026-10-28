#requires -Version 7.6
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$folder=Join-Path $root ('.cache\package-tests-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($folder)
$outside=Join-Path $folder 'outside'
[void][IO.Directory]::CreateDirectory($outside)
[IO.File]::WriteAllText((Join-Path $outside 'source.cs'),'synthetic outside source')
$inside=Join-Path $folder 'source'
[void][IO.Directory]::CreateDirectory($inside)
$null=New-Item -ItemType Junction -Path (Join-Path $inside 'linked') -Target $outside
Add-Type -Path @((Join-Path $root 'scripts\PackageArchive.cs'),(Join-Path $PSScriptRoot 'fixtures\PackageArchiveTests.cs'))
[Popcorn.Release.PackageArchiveTests]::Run($folder)
