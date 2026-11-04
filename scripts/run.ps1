param([switch]$Fixture,[string]$DataDirectory)
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$dotnet = Join-Path $projectRoot '.tools/dotnet/dotnet.exe'
$application = Join-Path $projectRoot 'src/OpenKaraoke.Prototype/bin/Release/net10.0-windows/OpenKaraoke.dll'
if (-not (Test-Path -LiteralPath $application)) { & (Join-Path $PSScriptRoot 'build.ps1') }
$appArguments=@($application)
if($Fixture){$appArguments+=@('--fixture','--fixture-media',(Join-Path $projectRoot 'fixtures/dual-track-test.mp4'))}
if($DataDirectory){$appArguments+=@('--data-dir',[IO.Path]::GetFullPath($DataDirectory))}
& $dotnet @appArguments
