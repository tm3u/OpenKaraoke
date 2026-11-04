param([switch]$Packaged,[switch]$SkipCore,[string]$Executable)
$ErrorActionPreference='Stop'
$karaokeRoot=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$karaokeDotnet=Join-Path $karaokeRoot '.tools/dotnet/dotnet.exe'
Push-Location $karaokeRoot
try{
 if(-not $Packaged){& (Join-Path $PSScriptRoot 'build.ps1')}
 if(-not $SkipCore){& $karaokeDotnet run --project tests/OpenKaraoke.Phone.Tests -c Release -- $karaokeRoot;if($LASTEXITCODE -ne 0){throw '手機麥克風核心驗收失敗'}}
 $karaokeFolder=Join-Path $karaokeRoot ('artifacts/phone-ui-'+(Get-Date -Format 'yyyyMMdd-HHmmss'));New-Item -ItemType Directory -Force -Path $karaokeFolder|Out-Null
 $karaokeArgs=@('--fixture','--fixture-media',(Join-Path $karaokeRoot 'fixtures/dual-track-test.mp4'),'--data-dir',(Join-Path $karaokeFolder 'data'),'--verify-phone',(Join-Path $karaokeFolder 'report.json'))
 if($Packaged){$karaokeExe=if($Executable){[IO.Path]::GetFullPath($Executable,$karaokeRoot)}else{Join-Path $karaokeRoot 'dist/OpenKaraoke-Standalone/OpenKaraoke.exe'}}
 else{$karaokeExe=$karaokeDotnet;$karaokeArgs=@((Join-Path $karaokeRoot 'src/OpenKaraoke.Prototype/bin/Release/net10.0-windows/OpenKaraoke.dll'))+$karaokeArgs}
 $karaokeQuoted=$karaokeArgs|ForEach-Object {'"'+$_+'"'}
 $karaokeApp=Start-Process -FilePath $karaokeExe -ArgumentList $karaokeQuoted -WorkingDirectory $karaokeRoot -WindowStyle Hidden -PassThru
 if(-not $karaokeApp.WaitForExit(180000)){throw '手機麥克風介面驗收逾時'}
 $karaokeResult=Get-Content -LiteralPath (Join-Path $karaokeFolder 'report.json') -Raw|ConvertFrom-Json
 $karaokeResult.Checks|Select-Object Name,Passed
 if(-not $karaokeResult.Passed){throw $karaokeResult.Error}
 Write-Output "手機麥克風介面驗收通過：$karaokeFolder"
}finally{Pop-Location}
