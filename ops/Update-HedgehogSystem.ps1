$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$opsRoot = Join-Path $repoRoot ".hedgehog\ops"
$logDir = Join-Path $opsRoot "logs"
New-Item -ItemType Directory -Force -Path $logDir | Out-Null

$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$logPath = Join-Path $logDir "system-update-$timestamp.log"

Set-Location $repoRoot

function Log {
    param([string] $Message)
    $line = "$(Get-Date -Format o) $Message"
    Add-Content -Path $logPath -Value $line
    Write-Host $line
}

function Test-HttpOk {
    param([string] $Url)
    try {
        $response = Invoke-WebRequest -UseBasicParsing $Url -TimeoutSec 5
        return $response.StatusCode -ge 200 -and $response.StatusCode -lt 300
    } catch {
        return $false
    }
}

function Restart-System {
    Log "Building and restarting Hedgehog live runtime."
    dotnet build src\Hedgehog.LocalRuntime.Api\Hedgehog.LocalRuntime.Api.csproj -v:minimal | Tee-Object -FilePath $logPath -Append
    & (Join-Path $PSScriptRoot "Start-HedgehogSystem.ps1") | Tee-Object -FilePath $logPath -Append
}

$before = git rev-parse HEAD
Log "Before=$before"

$dirty = git status --porcelain
if ($dirty) {
    Log "Working tree has local changes; skipping git pull and only enforcing service health."
} else {
    git fetch --prune | Tee-Object -FilePath $logPath -Append
    $branch = git rev-parse --abbrev-ref HEAD
    Log "Branch=$branch"
    git pull --ff-only origin $branch | Tee-Object -FilePath $logPath -Append
}

$after = git rev-parse HEAD
Log "After=$after"

$ready = Test-HttpOk "http://127.0.0.1:5090/health/ready"
if ($before -ne $after) {
    Restart-System
} elseif (-not $ready) {
    Log "Live runtime is not healthy; restarting."
    Restart-System
} else {
    Log "Live runtime is healthy and no deployable code change was detected."
}

