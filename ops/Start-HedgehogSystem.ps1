$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$opsRoot = Join-Path $repoRoot ".hedgehog\ops"
$logDir = Join-Path $opsRoot "logs"
$pidDir = Join-Path $opsRoot "pids"
$runtimeRoot = Join-Path $repoRoot ".hedgehog\live-runtime"

New-Item -ItemType Directory -Force -Path $logDir | Out-Null
New-Item -ItemType Directory -Force -Path $pidDir | Out-Null
New-Item -ItemType Directory -Force -Path $runtimeRoot | Out-Null

$runtimePidPath = Join-Path $pidDir "local-runtime-api.pid"

function Stop-TrackedProcess {
    param([string] $PidPath)

    if (-not (Test-Path $PidPath)) {
        return
    }

    $raw = Get-Content $PidPath -ErrorAction SilentlyContinue
    if (-not $raw) {
        Remove-Item $PidPath -Force -ErrorAction SilentlyContinue
        return
    }

    $processId = [int]$raw
    $process = Get-Process -Id $processId -ErrorAction SilentlyContinue
    if ($process) {
        Stop-Process -Id $processId -Force
    }

    Remove-Item $PidPath -Force -ErrorAction SilentlyContinue
}

Set-Location $repoRoot
Stop-TrackedProcess -PidPath $runtimePidPath

$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$stdout = Join-Path $logDir "local-runtime-api-$timestamp.out.log"
$stderr = Join-Path $logDir "local-runtime-api-$timestamp.err.log"

$env:HEDGEHOG_RUNTIME_ROOT = $runtimeRoot
$env:HEDGEHOG_RUNTIME_RESET = "false"
$env:HEDGEHOG_HEAD_COUNT = "4"
$env:HEDGEHOG_STORAGE_NODE_COUNT = "6"
$env:HEDGEHOG_REQUIRED_REPLICA_COUNT = "3"
$env:HEDGEHOG_STORAGE_NODE_CAPACITY_MIB = "1024"
$env:HEDGEHOG_TRAFFIC_ENABLED = "true"
$env:HEDGEHOG_TRAFFIC_INTERVAL_SECONDS = "5"

$dllPath = Join-Path $repoRoot "src\Hedgehog.LocalRuntime.Api\bin\Debug\net9.0\Hedgehog.LocalRuntime.Api.dll"
if (-not (Test-Path $dllPath)) {
    dotnet build src\Hedgehog.LocalRuntime.Api\Hedgehog.LocalRuntime.Api.csproj -v:minimal
}

$args = @($dllPath, "--urls", "http://0.0.0.0:5090")

$process = Start-Process `
    -FilePath "dotnet" `
    -ArgumentList $args `
    -WorkingDirectory $repoRoot `
    -RedirectStandardOutput $stdout `
    -RedirectStandardError $stderr `
    -WindowStyle Hidden `
    -PassThru

Set-Content -Path $runtimePidPath -Value $process.Id -NoNewline
Write-Host "Hedgehog live runtime pid=$($process.Id) url=http://0.0.0.0:5090 runtimeRoot=$runtimeRoot"
