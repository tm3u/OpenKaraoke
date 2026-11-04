param([switch]$Packaged,[string]$Executable)
$ErrorActionPreference='Stop'
$karaokeRoot=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$karaokeDotnet=Join-Path $karaokeRoot '.tools/dotnet/dotnet.exe'
$karaokeFolder=Join-Path $karaokeRoot ('artifacts/contract-ui-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $karaokeFolder -Force | Out-Null
if(-not $Packaged){& (Join-Path $PSScriptRoot 'build.ps1')}
foreach($karaokeMode in @('empty','populated')){
    $karaokeArguments=@('--data-dir',(Join-Path $karaokeFolder "$karaokeMode-data"))
    $karaokeReport=Join-Path $karaokeFolder "$karaokeMode-report.json"
    if($karaokeMode -eq 'empty'){$karaokeArguments+=@('--verify-empty-ui',$karaokeReport)}
    else{$karaokeArguments+=@('--fixture','--fixture-media',(Join-Path $karaokeRoot 'fixtures/dual-track-test.mp4'),'--verify-ui',$karaokeReport)}
    if($Packaged){$karaokeExecutable=if($Executable){[IO.Path]::GetFullPath($Executable,$karaokeRoot)}else{Join-Path $karaokeRoot 'dist/OpenKaraoke-Standalone/OpenKaraoke.exe'}}
    else{$karaokeExecutable=$karaokeDotnet;$karaokeArguments=@((Join-Path $karaokeRoot 'src/OpenKaraoke.Prototype/bin/Release/net10.0-windows/OpenKaraoke.dll'))+$karaokeArguments}
    $karaokeQuoted=$karaokeArguments | ForEach-Object {'"'+$_+'"'}
    $karaokeApp=Start-Process -FilePath $karaokeExecutable -ArgumentList $karaokeQuoted -WorkingDirectory $karaokeRoot -WindowStyle Hidden -PassThru
    if(-not $karaokeApp.WaitForExit(180000)){throw 'UI 對照驗收逾時'}
    if(-not(Test-Path -LiteralPath $karaokeReport)){throw '未產生 UI 對照驗收報告'}
    $karaokeResult=Get-Content -LiteralPath $karaokeReport -Raw|ConvertFrom-Json
    if(-not $karaokeResult.Passed){throw $karaokeResult.Error}
    Write-Output "$karaokeMode：$($karaokeResult.Checks.Count) 項通過"
}
$karaokeContract=Get-Content -LiteralPath (Join-Path $karaokeFolder 'ui-contract-report.json') -Raw|ConvertFrom-Json
if(-not $karaokeContract.Passed){throw $karaokeContract.Error}
Write-Output "圖示、按鈕、懸停與清單狀態：$($karaokeContract.Checks.Count) 項通過"
Write-Output "UI 對照驗收全部通過：$karaokeFolder"
