#requires -Version 7.6
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$folder=Join-Path $root ('.cache\package-review-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory((Join-Path $folder 'scripts'))
[void][IO.Directory]::CreateDirectory((Join-Path $folder 'tests\fixtures'))
foreach($name in @('PackageArchive.cs','Verify-Package.ps1')){ Copy-Item -LiteralPath (Join-Path $root ('scripts\'+$name)) -Destination (Join-Path $folder 'scripts') }
foreach($name in @('Test-PackageArchive.ps1','Test-PackageVerification.ps1')){ Copy-Item -LiteralPath (Join-Path $root ('tests\'+$name)) -Destination (Join-Path $folder 'tests') }
Copy-Item -LiteralPath (Join-Path $root 'tests\fixtures\PackageArchiveTests.cs') -Destination (Join-Path $folder 'tests\fixtures')
$null=New-Item -ItemType Junction -Path (Join-Path $folder '.deps') -Target (Join-Path $root '.deps')
$archive=[IO.File]::ReadAllText((Join-Path $folder 'scripts\PackageArchive.cs'))
$checker=[IO.File]::ReadAllText((Join-Path $folder 'scripts\Verify-Package.ps1'))
$cases=@(
    @{name='secret';file='PackageArchive.cs';old='Check(!needles.Any(needle=>!string.IsNullOrEmpty(needle) && text.Contains(needle,StringComparison.OrdinalIgnoreCase)),';new='Check(true,';test='Test-PackageArchive.ps1';expected='Expected rejection: sensitive'},
    @{name='dependency-checksum';file='PackageArchive.cs';old='Check(Hash(bytes,MaxTotalBytes).Equals(input.Checksum,StringComparison.OrdinalIgnoreCase),';new='Check(true,';test='Test-PackageArchive.ps1';expected='Expected rejection: checksum'},
    @{name='zip-hash';file='PackageArchive.cs';old='Hash(data,file.Length)==file.Sha256';new='true';test='Test-PackageArchive.ps1';expected='Expected rejection: hash'},
    @{name='source-root';file='PackageArchive.cs';old='Check(full.StartsWith(prefix,StringComparison.OrdinalIgnoreCase),';new='Check(true,';test='Test-PackageArchive.ps1';expected='Expected rejection: outside'},
    @{name='required-files';file='PackageArchive.cs';old='Check(RequiredPaths.All(files.Contains),';new='Check(true,';test='Test-PackageArchive.ps1';expected='Expected rejection: required'},
    @{name='directory-private';file='Verify-Package.ps1';old='-not (Test-AllowedPackagePath $entry.path)';new='$false';test='Test-PackageVerification.ps1';expected='a manifest cannot authorize shipping private cache'},
    @{name='directory-hash';file='Verify-Package.ps1';old='(Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ine $entry.sha256';new='$false';test='Test-PackageVerification.ps1';expected='changed payload'}
)
foreach($case in $cases){
    [IO.File]::WriteAllText((Join-Path $folder 'scripts\PackageArchive.cs'),$archive)
    [IO.File]::WriteAllText((Join-Path $folder 'scripts\Verify-Package.ps1'),$checker)
    $target=Join-Path $folder ('scripts\'+$case.file)
    $source=[IO.File]::ReadAllText($target)
    if(-not $source.Contains($case.old)){ throw ('Mutation no longer matches: '+$case.name) }
    [IO.File]::WriteAllText($target,$source.Replace($case.old,$case.new))
    $output=(& (Join-Path $root '.deps\pwsh\pwsh.exe') -NoProfile -ExecutionPolicy Bypass -File (Join-Path $folder ('tests\'+$case.test)) 2>&1 | Out-String)
    $exitCode=$LASTEXITCODE
    [IO.File]::WriteAllText((Join-Path $folder ($case.name+'.log')),$output)
    if($exitCode -eq 0 -or -not $output.Contains($case.expected)){ throw ('Package mutation survived or failed for another reason: '+$case.name+'; inspect '+$folder) }
    'PASS: rejected package mutation '+$case.name
}
'Review retained at '+$folder
