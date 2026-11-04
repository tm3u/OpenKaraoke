param([Parameter(Mandatory=$true)][string]$Package,[string]$RuntimeDirectory,[switch]$Packaged,[string]$Executable)
$ErrorActionPreference='Stop'
$karaokeRoot=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$karaokeDotnet=Join-Path $karaokeRoot '.tools/dotnet/dotnet.exe'
if(-not(Test-Path -LiteralPath $Package -PathType Leaf)){throw '驗收歌曲檔不存在'}
Push-Location $karaokeRoot
try {
    if(-not $Packaged){& (Join-Path $PSScriptRoot 'build.ps1')}
    $karaokeFolder=Join-Path $karaokeRoot ('artifacts/creation-ui-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
    New-Item -ItemType Directory -Path $karaokeFolder -Force | Out-Null
    $karaokeArguments=@('--fixture','--fixture-media',(Join-Path $karaokeRoot 'fixtures/dual-track-test.mp4'),'--data-dir',(Join-Path $karaokeFolder 'data'),'--verify-creation',(Join-Path $karaokeFolder 'report.json'),'--verification-package',([IO.Path]::GetFullPath($Package)))
    if($RuntimeDirectory){$karaokeArguments+=@('--ai-dir',([IO.Path]::GetFullPath($RuntimeDirectory)))}
    if($Packaged){$karaokeExecutable=if($Executable){[IO.Path]::GetFullPath($Executable,$karaokeRoot)}else{Join-Path $karaokeRoot 'dist/OpenKaraoke-Standalone/OpenKaraoke.exe'}}
    else {$karaokeExecutable=$karaokeDotnet;$karaokeArguments=@((Join-Path $karaokeRoot 'src/OpenKaraoke.Prototype/bin/Release/net10.0-windows/OpenKaraoke.dll'))+$karaokeArguments}
    $karaokeQuoted=$karaokeArguments|ForEach-Object {'"'+$_+'"'}
    $karaokeApp=Start-Process -FilePath $karaokeExecutable -ArgumentList $karaokeQuoted -WorkingDirectory $karaokeRoot -WindowStyle Hidden -PassThru
    if(-not $karaokeApp.WaitForExit(90000)){throw '加入歌曲驗收逾時'}
    if(-not(Test-Path -LiteralPath (Join-Path $karaokeFolder 'report.json'))){throw '未產生加入歌曲驗收結果'}
    $karaokeResult=Get-Content -LiteralPath (Join-Path $karaokeFolder 'report.json') -Raw|ConvertFrom-Json
    $karaokeResult.Checks|Select-Object Name,Passed
    if(-not $karaokeResult.Passed){throw $karaokeResult.Error}
    Write-Output "加入歌曲驗收全部通過：$karaokeFolder"
} finally {Pop-Location}
