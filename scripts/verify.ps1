param([string]$StatePath = (Join-Path $env:LOCALAPPDATA 'OpenKaraoke/artifacts/player-state.json'), [string]$ReportPath = 'artifacts/audio-verification.json')
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$dotnet = Join-Path $projectRoot '.tools/dotnet/dotnet.exe'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
Push-Location $projectRoot
try {
    & $dotnet build 'tests/OpenKaraoke.Verifier/OpenKaraoke.Verifier.csproj' -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw '驗證器建置失敗' }
    & $dotnet 'tests/OpenKaraoke.Verifier/bin/Release/net10.0-windows/OpenKaraoke.Verifier.dll' $StatePath $ReportPath
    if ($LASTEXITCODE -ne 0) { throw "驗證未通過，請查看 $ReportPath" }
} finally { Pop-Location }
