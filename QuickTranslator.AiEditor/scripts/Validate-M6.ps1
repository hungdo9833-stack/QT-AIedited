param([string]$Stage = 'm6-final')

$ErrorActionPreference = 'Stop'
if ($Stage -notmatch '^[a-zA-Z0-9-]+$') { throw 'Stage must be a simple directory name.' }

$moduleRoot = Split-Path $PSScriptRoot -Parent
$repoRoot = Split-Path $moduleRoot -Parent
$localSdk = Join-Path $repoRoot '.tools/dotnet/dotnet.exe'
$sdk = if (Test-Path -LiteralPath $localSdk) { $localSdk } else { (Get-Command dotnet).Source }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
if (Test-Path -LiteralPath $localSdk) {
    $env:DOTNET_CLI_HOME = [IO.Path]::GetFullPath((Join-Path $repoRoot '.tools/cli'))
    $env:NUGET_PACKAGES = [IO.Path]::GetFullPath((Join-Path $repoRoot '.tools/nuget'))
}

$artifactRoot = [IO.Path]::GetFullPath((Join-Path $moduleRoot 'artifacts/m6'))
$results = Join-Path $artifactRoot $Stage
$releaseRoot = Join-Path $artifactRoot 'release'
$publishDirectory = Join-Path $releaseRoot 'QTStudio-M6-win-x64'
$zipPath = Join-Path $releaseRoot 'QTStudio-M6-win-x64.zip'

function Reset-ArtifactDirectory([string]$Path) {
    $resolved = [IO.Path]::GetFullPath($Path)
    $prefix = $artifactRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clear a path outside M6 artifacts: $resolved"
    }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
    New-Item -ItemType Directory -Path $resolved | Out-Null
}

Reset-ArtifactDirectory $results
Reset-ArtifactDirectory $publishDirectory
New-Item -ItemType Directory -Force -Path $releaseRoot | Out-Null
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }

Push-Location $moduleRoot
try {
    & $sdk restore QuickTranslator.AiEditor.slnx --locked-mode -p:NuGetAudit=false |
        Tee-Object (Join-Path $results 'restore.txt')
    if ($LASTEXITCODE -ne 0) { throw "Restore failed ($LASTEXITCODE)." }

    & $sdk build QuickTranslator.AiEditor.slnx -c Release --no-restore --no-incremental -p:NuGetAudit=false |
        Tee-Object (Join-Path $results 'build.txt')
    if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)." }

    & $sdk test QuickTranslator.AiEditor.slnx -c Release --no-build --no-restore --logger trx `
        --results-directory $results -p:NuGetAudit=false | Tee-Object (Join-Path $results 'tests.txt')
    if ($LASTEXITCODE -ne 0) { throw "Tests failed ($LASTEXITCODE)." }

    & $sdk publish src/QuickTranslator.AiEditor.Wpf/QuickTranslator.AiEditor.Wpf.csproj -c Release `
        -r win-x64 --self-contained true --no-restore -p:PublishSingleFile=false `
        -p:DebugType=None -p:DebugSymbols=false -p:NuGetAudit=false -o $publishDirectory |
        Tee-Object (Join-Path $results 'publish.txt')
    if ($LASTEXITCODE -ne 0) { throw "Publish failed ($LASTEXITCODE)." }

    Copy-Item -LiteralPath 'docs/M6-MANUAL-TEST.vi.md' `
        -Destination (Join-Path $publishDirectory 'HUONG-DAN-TEST-M6.md')
    Copy-Item -LiteralPath 'docs/M6-READER-MODE.vi.md' `
        -Destination (Join-Path $publishDirectory 'BAO-CAO-KY-THUAT-M6.md')
    $screenshotDirectory = Join-Path $publishDirectory 'screenshots'
    New-Item -ItemType Directory -Path $screenshotDirectory | Out-Null
    Get-ChildItem -LiteralPath 'artifacts/m6/screenshots' -Filter '*.png' -File |
        Copy-Item -Destination $screenshotDirectory

    $sample = New-Object Text.StringBuilder
    [void]$sample.AppendLine('第一卷 M6阅读验收')
    foreach ($number in 1..50) {
        [void]$sample.AppendLine("第${number}章 合成阅读测试${number}")
        [void]$sample.AppendLine("这是第${number}章的第一段合成原文。用于测试目录、阅读位置与返回编辑器。")
        [void]$sample.AppendLine("这是第${number}章的第二段合成原文。不包含任何个人项目数据。")
        [void]$sample.AppendLine()
    }
    [IO.File]::WriteAllText((Join-Path $publishDirectory 'M6-SAMPLE-50-CHAPTERS.txt'),
        $sample.ToString(), [Text.UTF8Encoding]::new($false))

    Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $zipPath -CompressionLevel Optimal
    $hash = Get-FileHash -LiteralPath $zipPath -Algorithm SHA256
    "$($hash.Hash)  $($hash.Path)" | Set-Content -LiteralPath (Join-Path $releaseRoot 'QTStudio-M6-win-x64.sha256.txt') -Encoding utf8
    Write-Host "M6_ZIP=$zipPath"
    Write-Host "M6_SHA256=$($hash.Hash)"
}
finally { Pop-Location }
