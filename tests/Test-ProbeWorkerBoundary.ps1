#requires -Version 7.6
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$source=[IO.File]::ReadAllText((Join-Path $root 'desktop/NativeProbe/Program.cs'))
$input='Console.OpenStandardInput()'
$run='ProbeWorkerRuntime.Run();'
foreach($boundary in @($input,$run)){
    if(($source.Split($boundary).Count-1) -ne 1){throw 'Actual native worker boundaries changed; refusing the fixture.'}
}
if($source.IndexOf('ProbeGate.OpenAsync') -gt $source.IndexOf('NativeRuntime.LoadMediaProjection')){
    throw 'The worker must admit its parent before loading player projections.'
}
$folder=Join-Path $root ('.cache/worker-boundary-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($folder)
$program=Join-Path $folder 'Program.cs'
# Replace only stdin and the terminal player entry. The actual native Main,
# apartment checks, parent gate, projection loading and error boundary remain.
[IO.File]::WriteAllText($program,$source.Replace($input,'new WorkerGateStream()').Replace($run,'WorkerFixture.Emit();'))
$common=[Security.SecurityElement]::Escape((Join-Path $root 'desktop/NativeCommon.props'))
$native=[Security.SecurityElement]::Escape((Join-Path $root 'desktop/native/*.cs'))
$fixture=[Security.SecurityElement]::Escape((Join-Path $PSScriptRoot 'fixtures/ProbeWorkerFixture.cs'))
$project=Join-Path $folder 'ProbeBoundary.csproj'
[IO.File]::WriteAllText($project,@"
<Project Sdk="Microsoft.NET.Sdk">
  <Import Project="$common" />
  <PropertyGroup><OutputType>Exe</OutputType><AssemblyName>ProbeBoundary</AssemblyName></PropertyGroup>
  <ItemGroup><Compile Include="$native" /><Compile Include="$fixture" /></ItemGroup>
</Project>
"@)
$env:DOTNET_CLI_HOME=Join-Path $root '.deps/dotnet-home'
$output=Join-Path $folder 'output'
& (Join-Path $root '.deps/dotnet/dotnet.exe') build $project -c Release -o $output
if($LASTEXITCODE -ne 0){throw 'Native bootstrap fixture did not build.'}
$paths=@('Core.cs','PlaybackModels.cs','ProbeTransport.cs','ProbeFrameReader.cs','ProbeJob.cs','ProbeProcess.cs')|ForEach-Object {Join-Path $root ('desktop/native/'+$_)}
Add-Type -Path @($paths+(Join-Path $PSScriptRoot 'fixtures/ProbeWorkerBoundaryTests.cs'))
$null=[Popcorn.ProbeWorkerBoundaryTests]::Run((Join-Path $output 'ProbeBoundary.exe')).GetAwaiter().GetResult()
'PASS: production native bootstrap admitted a synthetic emitter only; no real PlayerSampler was activated.'
