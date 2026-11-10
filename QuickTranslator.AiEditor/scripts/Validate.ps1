# Builds the solution, runs every test and, with -Publish, checks that a self-contained win-x64 publish succeeds.
# Nothing is written into the repository: logs and the publish output go to a temporary folder.
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Validate.ps1 [-Publish] [-KeepPublish] [-NoRestore]
param(
    [switch]$Publish,      # also publish the WPF app (self-contained, win-x64) to the temporary folder
    [switch]$KeepPublish,  # keep the published app (about 150-260 MB) instead of deleting it afterwards
    [switch]$NoRestore     # skip 'dotnet restore' (packages already restored)
)

$ErrorActionPreference = 'Stop'
$moduleRoot = Split-Path $PSScriptRoot -Parent
$repoRoot = Split-Path $moduleRoot -Parent
$localSdk = Join-Path $repoRoot '.tools/dotnet/dotnet.exe'
$sdk = if (Test-Path -LiteralPath $localSdk) { $localSdk } else { (Get-Command dotnet).Source }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'

$results = Join-Path ([IO.Path]::GetTempPath()) ('qtstudio-validate-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $results | Out-Null
$publishDirectory = Join-Path $results 'publish'

function Invoke-Step([string]$Name, [string[]]$Arguments) {
    Write-Host "== $Name"
    & $sdk @Arguments | Tee-Object (Join-Path $results "$Name.txt") | Select-Object -Last 6
    if ($LASTEXITCODE -ne 0) { throw "$Name failed ($LASTEXITCODE). Log: $(Join-Path $results "$Name.txt")" }
}

Push-Location $moduleRoot
try {
    if (-not $NoRestore) { Invoke-Step 'restore' @('restore', 'QuickTranslator.AiEditor.slnx', '--locked-mode') }
    Invoke-Step 'build' @('build', 'QuickTranslator.AiEditor.slnx', '-c', 'Release', '--no-restore', '-nologo', '-warnaserror')
    Invoke-Step 'test' @('test', 'QuickTranslator.AiEditor.slnx', '-c', 'Release', '--no-build', '-nologo')
    if ($Publish) {
        # Publishing for win-x64 restores Core with a runtime and rewrites its packages.lock.json: put it back afterwards.
        $coreLock = Join-Path $moduleRoot 'src/QuickTranslator.AiEditor.Core/packages.lock.json'
        $coreLockBytes = [IO.File]::ReadAllBytes($coreLock)
        try {
            Invoke-Step 'publish' @('publish', 'src/QuickTranslator.AiEditor.Wpf/QuickTranslator.AiEditor.Wpf.csproj', '-c', 'Release',
                '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=false', '-p:DebugType=None', '-o', $publishDirectory)
        }
        finally { [IO.File]::WriteAllBytes($coreLock, $coreLockBytes) }
        if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory 'QuickTranslator.AiEditor.exe'))) {
            throw 'Publish finished without QuickTranslator.AiEditor.exe.'
        }
        if ($KeepPublish) { Write-Host "Published app kept at $publishDirectory" }
        else { Remove-Item -LiteralPath $publishDirectory -Recurse -Force }
    }
}
finally { Pop-Location }
Write-Host "Validation passed. Logs: $results"
