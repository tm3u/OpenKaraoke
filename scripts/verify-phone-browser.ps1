$ErrorActionPreference='Stop'
$karaokeRoot=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$karaokeDotnet=Join-Path $karaokeRoot '.tools/dotnet/dotnet.exe'
$karaokePackage=Join-Path $karaokeRoot '.tools/phone-browser'
if(-not(Get-Command node -ErrorAction SilentlyContinue)){throw '瀏覽器驗收需 Node.js；正式程式不需要 Node.js'}
if(-not(Test-Path -LiteralPath (Join-Path $karaokePackage 'package/package.json'))){
 New-Item -ItemType Directory -Force -Path $karaokePackage|Out-Null
 $karaokeMeta=Invoke-RestMethod -Uri 'https://registry.npmjs.org/playwright-core/1.56.1'
 $karaokeTar=Join-Path $karaokePackage 'playwright-core.tgz';Invoke-WebRequest -Uri $karaokeMeta.dist.tarball -OutFile $karaokeTar
 $karaokeHash='sha512-'+[Convert]::ToBase64String([Security.Cryptography.SHA512]::HashData([IO.File]::ReadAllBytes($karaokeTar)))
 if($karaokeHash -ne $karaokeMeta.dist.integrity){throw '瀏覽器測試套件雜湊不符'}
 & tar -xzf $karaokeTar -C $karaokePackage;if($LASTEXITCODE -ne 0){throw '瀏覽器套件解壓縮失敗'}
}
$karaokeFolder=Join-Path $karaokeRoot ('artifacts/phone-browser-'+(Get-Date -Format 'yyyyMMdd-HHmmss'));New-Item -ItemType Directory -Force -Path $karaokeFolder|Out-Null
$karaokeInfo=Join-Path $karaokeFolder 'server.json'
Push-Location $karaokeRoot
try{
 & $karaokeDotnet build tests/OpenKaraoke.Phone.Tests -c Release --nologo
 if($LASTEXITCODE -ne 0){throw '手機服務驗收建置失敗'}
 $karaokeArgs=@((Join-Path $karaokeRoot 'tests/OpenKaraoke.Phone.Tests/bin/Release/net10.0-windows/OpenKaraoke.Phone.Tests.dll'),$karaokeRoot,'--serve',$karaokeInfo)|ForEach-Object {'"'+$_+'"'}
 $karaokeApp=Start-Process -FilePath $karaokeDotnet -ArgumentList $karaokeArgs -WindowStyle Hidden -WorkingDirectory $karaokeRoot -PassThru
 try{
  $karaokeWatch=[Diagnostics.Stopwatch]::StartNew();while(-not(Test-Path -LiteralPath $karaokeInfo)){if($karaokeApp.HasExited -or $karaokeWatch.Elapsed.TotalSeconds -gt 20){throw '瀏覽器測試服務未就緒'};Start-Sleep -Milliseconds 100}
  & node (Join-Path $karaokeRoot 'tests/OpenKaraoke.Phone.Browser/verify.cjs') $karaokeRoot $karaokeInfo
  if($LASTEXITCODE -ne 0){throw '手機瀏覽器驗收失敗'}
  Write-Output "手機瀏覽器驗收通過：$karaokeFolder"
 }finally{Set-Content -LiteralPath ($karaokeInfo+'.stop') -Value 'stop';if(-not $karaokeApp.WaitForExit(10000)){throw '測試服務未正常關閉'}}
}finally{Pop-Location}
