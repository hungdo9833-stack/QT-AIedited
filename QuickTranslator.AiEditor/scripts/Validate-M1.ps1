param([string]$Stage = 'm1-final')
$ErrorActionPreference = 'Stop'
if ($Stage -notmatch '^[a-zA-Z0-9-]+$') { throw 'Stage must be a simple directory name.' }
$moduleRoot = Split-Path $PSScriptRoot -Parent
$repoRoot = Split-Path $moduleRoot -Parent
$localSdk = Join-Path $repoRoot '.tools/dotnet/dotnet.exe'
$sdk = if (Test-Path -LiteralPath $localSdk) { $localSdk } else { (Get-Command dotnet).Source }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
if (Test-Path -LiteralPath $localSdk) {
    $env:DOTNET_CLI_HOME = Join-Path $repoRoot '.tools/cli'
    $env:NUGET_PACKAGES = Join-Path $repoRoot '.tools/nuget'
}
$results = Join-Path $moduleRoot ('artifacts/m1/' + $Stage)
New-Item -ItemType Directory -Force -Path $results | Out-Null
Push-Location $moduleRoot
try {
    & $sdk restore QuickTranslator.AiEditor.slnx --locked-mode -p:NuGetAudit=false | Tee-Object (Join-Path $results 'restore.txt')
    if ($LASTEXITCODE -ne 0) { throw "Restore failed ($LASTEXITCODE)." }
    & $sdk build QuickTranslator.AiEditor.slnx -c Release --no-restore --no-incremental | Tee-Object (Join-Path $results 'build.txt')
    if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)." }
    & $sdk test QuickTranslator.AiEditor.slnx -c Release --no-build --no-restore --logger trx --results-directory $results |
        Tee-Object (Join-Path $results 'tests.txt')
    if ($LASTEXITCODE -ne 0) { throw "Tests failed ($LASTEXITCODE)." }
    & $sdk run --project tools/QuickTranslator.AiEditor.ChapterDetector.Demo/QuickTranslator.AiEditor.ChapterDetector.Demo.csproj `
        -c Release --no-build --no-restore -- --synthetic 5000 | Tee-Object (Join-Path $results 'demo-5000.json')
    if ($LASTEXITCODE -ne 0) { throw "Demo failed ($LASTEXITCODE)." }
}
finally { Pop-Location }
