#requires -Version 7.4
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$cache = Join-Path $projectRoot '.tools'
New-Item -ItemType Directory -Force -Path $cache | Out-Null
$dotnet = Join-Path $cache 'dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) {
    $installer = Join-Path $cache 'dotnet-install.ps1'
    Invoke-WebRequest 'https://builds.dotnet.microsoft.com/dotnet/scripts/v1/dotnet-install.ps1' -OutFile $installer
    & $installer -Version '10.0.403' -InstallDir (Join-Path $cache 'dotnet') -NoPath
    if (-not (Test-Path -LiteralPath $dotnet)) { throw '.NET SDK 安裝失敗' }
}
& (Join-Path $PSScriptRoot 'prepare-media.ps1')
Write-Output '開發工具已就緒，全部位於專案內。'
