$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$ffmpeg = Join-Path $projectRoot 'tools/media/ffmpeg.exe'
if (-not (Test-Path -LiteralPath $ffmpeg)) { throw '請先執行 scripts/bootstrap.ps1' }
$folder = Join-Path $projectRoot 'fixtures'
New-Item -ItemType Directory -Force -Path $folder | Out-Null
$output = Join-Path $folder 'dual-track-test.mp4'
$arguments = @('-hide_banner','-y',
    '-f','lavfi','-i','testsrc2=size=640x360:rate=15:duration=120',
    '-f','lavfi','-i','sine=frequency=440:sample_rate=48000:duration=120',
    '-f','lavfi','-i','sine=frequency=660:sample_rate=48000:duration=120',
    '-map','0:v:0','-map','1:a:0','-map','2:a:0',
    '-c:v','libx264','-preset','ultrafast','-crf','32','-pix_fmt','yuv420p',
    '-c:a','aac','-b:a','128k','-ac','2',
    '-metadata:s:a:0','handler_name=Original test 440 Hz',
    '-metadata:s:a:1','handler_name=Instrumental test 660 Hz',
    '-disposition:a:0','default','-disposition:a:1','0','-movflags','+faststart',$output)
& $ffmpeg @arguments
if ($LASTEXITCODE -ne 0) { throw '建立雙音軌測試影片失敗' }
& $ffmpeg -hide_banner -y -i $output -t 3 -map 0:v:0 -map 0:a:0 -c copy (Join-Path $folder 'single-track-test.mp4')
if ($LASTEXITCODE -ne 0) { throw '建立單音軌測試影片失敗' }
[IO.File]::WriteAllText((Join-Path $folder 'invalid-test.mp4'), 'This is intentionally not a media file.')
