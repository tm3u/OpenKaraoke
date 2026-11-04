param()
$ErrorActionPreference='Stop'
$projectRoot=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$dotnet=Join-Path $projectRoot '.tools/dotnet/dotnet.exe'
Push-Location $projectRoot
try {
    & (Join-Path $PSScriptRoot 'build.ps1')
    & $dotnet run --project tests/OpenKaraoke.SingleMachine.Tests -c Release -- $projectRoot
    if($LASTEXITCODE -ne 0){throw '歌曲庫或匯入驗證失敗'}
    $stamp=Get-Date -Format 'yyyyMMdd-HHmmss'
    $data=Join-Path $projectRoot "artifacts/ui-tests-$stamp"
    $report=Join-Path $projectRoot "artifacts/interface-verification-$stamp.json"
    $state=Join-Path $projectRoot "artifacts/ui-state-$stamp.json"
    $application=Join-Path $projectRoot 'src/OpenKaraoke.Prototype/bin/Release/net10.0-windows/OpenKaraoke.dll'
    $arguments=@($application,'--fixture','--fixture-media',(Join-Path $projectRoot 'fixtures/dual-track-test.mp4'),'--data-dir',$data,'--diagnostics',$state,'--verify-ui',$report) | ForEach-Object {'"'+$_+'"'}
    $app=Start-Process -FilePath $dotnet -ArgumentList $arguments -WorkingDirectory $projectRoot -WindowStyle Hidden -PassThru
    if(-not $app.WaitForExit(60000)){throw '介面驗證逾時'}
    if(-not (Test-Path -LiteralPath $report)){throw '未產生介面驗收結果'}
    $result=Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
    $result.Checks | Select-Object Name,Passed
    if(-not $result.Passed){throw $result.Error}
    Write-Output "介面驗證全部通過：$report"
} finally { Pop-Location }
