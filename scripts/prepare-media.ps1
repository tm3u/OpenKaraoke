#requires -Version 7.4
param([switch]$SourcesOnly,[switch]$VerifyOnly,[string]$DestinationRoot)
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$destination = if ($DestinationRoot) { [IO.Path]::GetFullPath($DestinationRoot,$projectRoot) } else { $projectRoot }
$manifest = Get-Content -LiteralPath (Join-Path $projectRoot 'tools/manifest.json') -Raw | ConvertFrom-Json
$cache = Join-Path $projectRoot '.tools/media'
function Get-ExactFile($item,[string]$path) {
    if ((Test-Path -LiteralPath $path) -and (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -eq $item.sha256) { return }
    if ($VerifyOnly) { throw "檔案缺少或雜湊不符：$path" }
    New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
    $temporary = $path + '.download'
    try {
        Invoke-WebRequest $item.url -OutFile $temporary
        if ((Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash -ne $item.sha256) { throw "下載檔案 SHA256 不符：$($item.archive)" }
        Move-Item -LiteralPath $temporary -Destination $path -Force
    } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
}
if ($SourcesOnly) {
    foreach ($source in $manifest.sources) {
        $included = Join-Path $projectRoot ('third-party/sources/' + $source.archive)
        $path = if (Test-Path -LiteralPath $included) { $included } else { Join-Path $cache ('sources/' + $source.archive) }
        Get-ExactFile $source $path
    }
    Write-Output "第三方對應來源已核對：$($manifest.sources.Count) 個封存檔。"
    return
}
$missing = @($manifest.files | Where-Object {
    $path = Join-Path $destination $_.path
    !(Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $_.sha256
})
if ($missing.Count -eq 0) { Write-Output '媒體工具與全部原生相依檔已核對。'; return }
if ($VerifyOnly) { throw "媒體工具缺少或雜湊不符：$($missing.path -join '、')" }
foreach ($file in $missing) {
    if (!$file.package -or $file.package -notin $manifest.packages.name) { throw "找不到檔案的來源套件：$($file.path)" }
}
$sevenZip = Join-Path $cache '7zip/7z.exe'
if (!(Test-Path -LiteralPath $sevenZip)) {
    $reduced = Join-Path $cache '7zr.exe'
    Get-ExactFile $manifest.extractor.reduced $reduced
    $archive = Join-Path $cache $manifest.extractor.full.archive
    Get-ExactFile $manifest.extractor.full $archive
    & $reduced x $archive ('-o' + (Split-Path -Parent $sevenZip)) '7z.exe' '7z.dll' '-y' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw '解壓縮工具準備失敗。' }
}
foreach ($package in $manifest.packages) {
    $files = @($missing | Where-Object package -eq $package.name)
    if ($files.Count -eq 0) { continue }
    $archive = Join-Path $cache ('packages/' + $package.archive)
    Get-ExactFile $package $archive
    $temporary = Join-Path $cache ('extract-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $temporary | Out-Null
    & $sevenZip x $archive ('-o' + $temporary) '-y' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "套件解壓縮失敗：$($package.name)" }
    $tar = @(Get-ChildItem -LiteralPath $temporary -File)
    if ($tar.Count -ne 1) { throw '套件封存格式不符。' }
    $stream = [IO.File]::OpenRead($tar[0].FullName)
    $reader = [System.Formats.Tar.TarReader]::new($stream)
    $found = @{}
    try {
        while ($entry = $reader.GetNextEntry()) {
            $targets = @($files | Where-Object entry -eq $entry.Name)
            if ($targets.Count -eq 0 -or !$entry.DataStream) { continue }
            $memory = [IO.MemoryStream]::new()
            try { $entry.DataStream.CopyTo($memory); $bytes = $memory.ToArray() } finally { $memory.Dispose() }
            foreach ($file in $targets) {
                if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)) -ne $file.sha256) { throw "套件內檔案雜湊不符：$($file.path)" }
                $path = [IO.Path]::GetFullPath((Join-Path $destination $file.path))
                if (!$path.StartsWith($destination+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw '媒體工具路徑超出目標目錄。' }
                New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
                [IO.File]::WriteAllBytes($path,$bytes)
                $found[$file.path] = $true
            }
        }
    } finally { $reader.Dispose(); $stream.Dispose() }
    if ($found.Count -ne $files.Count) { throw "套件內缺少必要檔案：$($package.name)" }
    $resolvedTemporary = (Resolve-Path -LiteralPath $temporary).Path
    if (!$resolvedTemporary.StartsWith($cache+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or (Get-Item -LiteralPath $resolvedTemporary).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) { throw '暫存目錄清理路徑不符。' }
    Remove-Item -LiteralPath $resolvedTemporary -Recurse -Force
}
foreach ($file in $manifest.files) {
    $path = Join-Path $destination $file.path
    if (!(Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.sha256) { throw "媒體工具準備後仍有檔案缺少或雜湊不符：$($file.path)" }
}
Write-Output '媒體工具與全部原生相依檔準備完成。'
