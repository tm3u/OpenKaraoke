param([switch]$SkipCore,[switch]$Packaged,[string]$Executable)
$ErrorActionPreference='Stop'
$karaokeRoot=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$karaokeDotnet=Join-Path $karaokeRoot '.tools/dotnet/dotnet.exe'
Push-Location $karaokeRoot
try {
    if(-not $SkipCore){
        & $karaokeDotnet run --project tests/OpenKaraoke.Scoring.Tests -c Release -- $karaokeRoot
        if($LASTEXITCODE -ne 0){throw '評分核心驗收未通過'}
    } elseif(-not $Packaged) {
        & $karaokeDotnet build src/OpenKaraoke.Prototype -c Release --nologo
        if($LASTEXITCODE -ne 0){throw '評分介面建置未通過'}
    }
    $karaokeStamp=Get-Date -Format 'yyyyMMdd-HHmmss'
    $karaokeFolder=Join-Path $karaokeRoot "artifacts/scoring-ui-$karaokeStamp"
    New-Item -ItemType Directory -Force -Path $karaokeFolder | Out-Null
    $karaokeReport=Join-Path $karaokeFolder 'report.json'
    $karaokeArguments=@('--fixture','--fixture-media',(Join-Path $karaokeRoot 'fixtures/dual-track-test.mp4'),'--data-dir',(Join-Path $karaokeFolder 'data'),'--diagnostics',(Join-Path $karaokeFolder 'state.json'),'--verify-scoring',$karaokeReport)
    if($Packaged){$karaokeExecutable=if($Executable){[IO.Path]::GetFullPath($Executable,$karaokeRoot)}else{Join-Path $karaokeRoot 'dist/OpenKaraoke-Standalone/OpenKaraoke.exe'}}
    else{$karaokeExecutable=$karaokeDotnet;$karaokeArguments=@((Join-Path $karaokeRoot 'src/OpenKaraoke.Prototype/bin/Release/net10.0-windows/OpenKaraoke.dll'))+$karaokeArguments}
    $karaokeArguments=$karaokeArguments | ForEach-Object {'"'+$_+'"'}
    $karaokeProcess=Start-Process -FilePath $karaokeExecutable -ArgumentList $karaokeArguments -WorkingDirectory $karaokeRoot -WindowStyle Hidden -PassThru
    if(-not $karaokeProcess.WaitForExit(180000)){throw '評分介面驗收逾時'}
    if(-not (Test-Path -LiteralPath $karaokeReport)){throw '未產生評分驗收結果'}
    $karaokeResult=Get-Content -LiteralPath $karaokeReport -Raw | ConvertFrom-Json
    $karaokeResult.Checks | Select-Object Name,Passed
    if(-not $karaokeResult.Passed){throw $karaokeResult.Error}
    Write-Output "評分介面驗收全部通過：$karaokeReport"
} finally {Pop-Location}
