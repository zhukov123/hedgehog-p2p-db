$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$logDir = Join-Path $repoRoot ".hedgehog\ops\logs"
New-Item -ItemType Directory -Force -Path $logDir | Out-Null

$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$logPath = Join-Path $logDir "update-$timestamp.log"

Set-Location $repoRoot

function Log {
    param([string] $Message)
    $line = "$(Get-Date -Format o) $Message"
    Add-Content -Path $logPath -Value $line
    Write-Host $line
}

$before = git rev-parse HEAD
Log "Before=$before"

git fetch --prune | Tee-Object -FilePath $logPath -Append

$branch = git rev-parse --abbrev-ref HEAD
Log "Branch=$branch"

git pull --ff-only origin $branch | Tee-Object -FilePath $logPath -Append

$after = git rev-parse HEAD
Log "After=$after"

function Test-ListeningPort {
    param([int] $Port)

    $listener = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
    return $null -ne $listener
}

$apiListening = Test-ListeningPort -Port 5081
$uiListening = Test-ListeningPort -Port 5082

if ($before -ne $after) {
    Log "Code changed; building and restarting Hedgehog Admin."
    dotnet build Hedgehog.sln | Tee-Object -FilePath $logPath -Append
    & (Join-Path $PSScriptRoot "Start-HedgehogAdmin.ps1") | Tee-Object -FilePath $logPath -Append
} elseif (-not $apiListening -or -not $uiListening) {
    Log "One or more services are not listening; restarting Hedgehog Admin."
    & (Join-Path $PSScriptRoot "Start-HedgehogAdmin.ps1") | Tee-Object -FilePath $logPath -Append
} else {
    Log "No code change and both services are listening; leaving existing processes alone."
}
