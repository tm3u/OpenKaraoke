param([switch]$Publish,[string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$dotnet = Join-Path $projectRoot '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { throw '請先執行 scripts/bootstrap.ps1' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$project = Join-Path $projectRoot 'src/OpenKaraoke.Prototype/OpenKaraoke.Prototype.csproj'
Push-Location $projectRoot
try {
    & $dotnet build $project -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw '建置失敗' }
    if (-not $Publish) { return }
    & (Join-Path $PSScriptRoot 'prepare-media.ps1') -VerifyOnly
    $output = [IO.Path]::TrimEndingDirectorySeparator($(if($OutputDirectory){[IO.Path]::GetFullPath($OutputDirectory,$projectRoot)}else{Join-Path $projectRoot 'dist/OpenKaraoke-Standalone'}))
    if($output -eq [IO.Path]::GetPathRoot($output) -or $output -eq $projectRoot -or $projectRoot.StartsWith($output+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw '發布目錄不可為磁碟根目錄、專案目錄或其上層'}
    if(@(Get-Process OpenKaraoke -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq (Join-Path $output 'OpenKaraoke.exe') }).Count){throw '發佈目錄的 OpenKaraoke 正在執行，請改用其他輸出目錄'}
    if(Test-Path -LiteralPath $output){
        if(-not (Get-Item -LiteralPath $output).PSIsContainer){throw 'OutputDirectory 必須為目錄'}
        $unexpected=@(Get-ChildItem -LiteralPath $output -Force | Where-Object {
            if($_.PSIsContainer){$_.Name -notin @('app','tools','docs','licenses')}
            else{$_.Name -notin @('OpenKaraoke.exe','README.md','THIRD-PARTY-NOTICES.md','LICENSE')}
        })
        if($unexpected.Count){throw '發布目錄含有目前布局以外的檔案，請使用空白的 OutputDirectory'}
        if((Get-Item -LiteralPath $output).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint) -or @(Get-ChildItem -LiteralPath $output -Recurse -Force -Attributes ReparsePoint).Count){throw '發布目錄不可含有重新解析點'}
        $personal=@(Get-ChildItem -LiteralPath $output -File -Recurse -Force | Where-Object {$_.Name -in @('library.db','voice-settings.json') -or $_.Extension -in @('.opok','.openkaraoke','.lrc','.ass','.m4a')})
        if($personal.Count){throw '發布目錄含有使用者資料，請改用其他輸出目錄'}
    }
    $stage=Join-Path $projectRoot ('artifacts/publish/'+[Guid]::NewGuid().ToString('N'))
    $app=Join-Path $stage 'app'
    New-Item -ItemType Directory -Force -Path $app | Out-Null
    & $dotnet publish $project -c Release -r win-x64 --self-contained true -o $app --nologo
    if ($LASTEXITCODE -ne 0) { throw '發布失敗' }
    $symbols=$null
    $pdbFiles=@(Get-ChildItem -LiteralPath $app -File -Recurse -Filter '*.pdb')
    if($pdbFiles.Count){
        $symbols=Join-Path $projectRoot ('artifacts/symbols/'+(Get-Date -Format 'yyyyMMdd-HHmmssfff'))
        $symbolFiles=@(foreach($file in $pdbFiles){
            $relative=[IO.Path]::GetRelativePath($app,$file.FullName)
            $destination=Join-Path $symbols $relative
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
            $assembly=[IO.Path]::ChangeExtension($file.FullName,'.dll')
            $assemblyHash=if(Test-Path -LiteralPath $assembly){(Get-FileHash -LiteralPath $assembly).Hash}else{$null}
            $pdbHash=(Get-FileHash -LiteralPath $file.FullName).Hash
            Move-Item -LiteralPath $file.FullName -Destination $destination
            [pscustomobject]@{File=$relative;AssemblySha256=$assemblyHash;PdbSha256=$pdbHash}
        })
        $symbolFiles | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $symbols 'manifest.json') -Encoding utf8NoBOM
    }
    Get-ChildItem -LiteralPath $app -File -Recurse -Filter '*.lib' | ForEach-Object {Remove-Item -LiteralPath $_.FullName}
    $sdkVersion=(Get-Content -LiteralPath (Join-Path $projectRoot 'global.json') -Raw | ConvertFrom-Json).sdk.version
    $configuration=Get-Content -LiteralPath (Join-Path $app 'OpenKaraoke.runtimeconfig.json') -Raw | ConvertFrom-Json
    $runtimeVersion=($configuration.runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.NETCore.App').version
    $template=Join-Path $projectRoot ('.tools/dotnet/packs/Microsoft.NETCore.App.Host.win-x64/'+$runtimeVersion+'/runtimes/win-x64/native/apphost.exe')
    if(-not(Test-Path -LiteralPath $template)){throw '找不到相符的 .NET 原生啟動器，請執行 scripts/bootstrap.ps1'}
    $hostProject=[xml]'<Project><UsingTask TaskName="CreateAppHost"/><Target Name="CreateLauncher"><CreateAppHost AppBinaryName="app/OpenKaraoke.dll" WindowsGraphicalUserInterface="true" DotNetSearchLocations="AppLocal"/></Target></Project>'
    $hostProject.Project.UsingTask.SetAttribute('AssemblyFile',(Join-Path $projectRoot ('.tools/dotnet/sdk/'+$sdkVersion+'/Sdks/Microsoft.NET.Sdk/tools/net10.0/Microsoft.NET.Build.Tasks.dll')))
    foreach($property in @{AppHostSourcePath=$template;AppHostDestinationPath=(Join-Path $stage 'OpenKaraoke.exe');IntermediateAssembly=(Join-Path $app 'OpenKaraoke.dll')}.GetEnumerator()){
        $hostProject.Project.Target.CreateAppHost.SetAttribute($property.Key,$property.Value)
    }
    $hostProjectPath=$stage+'.proj'
    $hostProject.Save($hostProjectPath)
    & $dotnet msbuild $hostProjectPath -t:CreateLauncher --nologo -v:minimal
    if($LASTEXITCODE -ne 0){throw '建立原生啟動器失敗'}
    Remove-Item -LiteralPath $hostProjectPath
    Remove-Item -LiteralPath (Join-Path $app 'OpenKaraoke.exe')
    $mediaManifest=Get-Content -LiteralPath (Join-Path $projectRoot 'tools/manifest.json') -Raw | ConvertFrom-Json
    foreach($file in $mediaManifest.files){
        $target=Join-Path $stage $file.path
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
        Copy-Item -LiteralPath (Join-Path $projectRoot $file.path) -Destination $target
    }
    foreach($name in @('creation','scoring')){Copy-Item -LiteralPath (Join-Path $projectRoot ('tools/'+$name)) -Destination (Join-Path $stage 'tools') -Recurse}
    foreach($name in @('LICENSE','README.md','THIRD-PARTY-NOTICES.md')){Copy-Item -LiteralPath (Join-Path $projectRoot $name) -Destination $stage}
    Copy-Item -LiteralPath (Join-Path $projectRoot 'tools/manifest.json') -Destination (Join-Path $stage 'tools')
    foreach($name in @('licenses','docs')){Copy-Item -LiteralPath (Join-Path $projectRoot $name) -Destination $stage -Recurse}
    $temporaryZip=$stage+'.zip'
    Compress-Archive -LiteralPath (Get-ChildItem -LiteralPath $stage).FullName -DestinationPath $temporaryZip
    $zip=if($OutputDirectory){Join-Path (Split-Path -Parent $output) ((Split-Path -Leaf $output)+'-win-x64.zip')}else{Join-Path $projectRoot 'dist/OpenKaraoke-Standalone-win-x64.zip'}
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $output) | Out-Null
    if(Test-Path -LiteralPath $output){
        Add-Type -AssemblyName Microsoft.VisualBasic
        [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteDirectory($output,[Microsoft.VisualBasic.FileIO.UIOption]::OnlyErrorDialogs,[Microsoft.VisualBasic.FileIO.RecycleOption]::SendToRecycleBin)
    }
    if(-not $stage.StartsWith((Join-Path $projectRoot 'artifacts/publish')+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw '暫存發布目錄路徑不符'}
    Move-Item -LiteralPath $stage -Destination $output
    Move-Item -LiteralPath $temporaryZip -Destination $zip -Force
    Write-Output "可執行版本：$output"
    Write-Output "完整可攜式套件：$zip"
    if($symbols){Write-Output "偵錯符號：$symbols"}
} finally { Pop-Location }
