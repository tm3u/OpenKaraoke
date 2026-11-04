$ErrorActionPreference='Stop'
$projectRoot=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $projectRoot
try {
    & (Join-Path $PSScriptRoot 'prepare-media.ps1') -SourcesOnly
    $mediaManifest = Get-Content -LiteralPath (Join-Path $projectRoot 'tools/manifest.json') -Raw | ConvertFrom-Json
    if(Get-Command rg -ErrorAction SilentlyContinue){$files=@(& rg --files --hidden --no-ignore src tests scripts licenses fixtures docs tools/creation tools/scoring tools/icons -g '!**/bin/**' -g '!**/obj/**')}
    else {$files=@(Get-ChildItem src,tests,scripts,licenses,fixtures,docs,tools/creation,tools/scoring,tools/icons -File -Recurse -Force | Where-Object FullName -NotMatch '[\\/](bin|obj)[\\/]' | ForEach-Object {$_.FullName.Substring($projectRoot.Length+1)})}
    $files+=@('LICENSE','README.md','THIRD-PARTY-NOTICES.md','Directory.Build.props','global.json','.gitignore','.gitattributes','tools/manifest.json')
    $files=$files | Sort-Object -Unique
    New-Item -ItemType Directory -Force -Path (Join-Path $projectRoot 'dist') | Out-Null
    $output=Join-Path $projectRoot 'dist/OpenKaraoke-Standalone-source.zip'
    $temporary=Join-Path $projectRoot ('dist/source-'+[Guid]::NewGuid().ToString('N')+'.zip')
    Add-Type -AssemblyName System.IO.Compression
    $archive=[IO.Compression.ZipFile]::Open($temporary,[IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach($file in $files) {
            $entry='OpenKaraoke-Standalone-source/'+$file.Replace('\','/')
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,(Join-Path $projectRoot $file),$entry,[IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
        foreach ($source in $mediaManifest.sources) {
            $included=Join-Path $projectRoot ('third-party/sources/'+$source.archive)
            $path=if(Test-Path -LiteralPath $included){$included}else{Join-Path $projectRoot ('.tools/media/sources/'+$source.archive)}
            $entry='OpenKaraoke-Standalone-source/third-party/sources/'+$source.archive
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,$path,$entry,[IO.Compression.CompressionLevel]::NoCompression) | Out-Null
        }
    } finally { $archive.Dispose() }
    Move-Item -LiteralPath $temporary -Destination $output -Force
    Write-Output "完整原始碼：$output（$($files.Count) 個專案檔案、$($mediaManifest.sources.Count) 個第三方來源封存檔）"
} finally { Pop-Location }
