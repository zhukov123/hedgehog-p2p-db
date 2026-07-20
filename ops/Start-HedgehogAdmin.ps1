$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$logDir = Join-Path $repoRoot ".hedgehog\ops\logs"
$pidDir = Join-Path $repoRoot ".hedgehog\ops\pids"

New-Item -ItemType Directory -Force -Path $logDir | Out-Null
New-Item -ItemType Directory -Force -Path $pidDir | Out-Null

$apiPidPath = Join-Path $pidDir "admin-api.pid"
$uiPidPath = Join-Path $pidDir "admin-ui.pid"

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

function Start-DotnetService {
    param(
        [string] $Name,
        [string] $Project,
        [string] $Url,
        [string] $PidPath
    )

    $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $stdout = Join-Path $logDir "$Name-$timestamp.out.log"
    $stderr = Join-Path $logDir "$Name-$timestamp.err.log"
    $args = @("run", "--project", $Project, "--urls", $Url)

    $process = Start-Process `
        -FilePath "dotnet" `
        -ArgumentList $args `
        -WorkingDirectory $repoRoot `
        -RedirectStandardOutput $stdout `
        -RedirectStandardError $stderr `
        -WindowStyle Hidden `
        -PassThru

    Set-Content -Path $PidPath -Value $process.Id -NoNewline
    return $process
}

Set-Location $repoRoot

Stop-TrackedProcess -PidPath $apiPidPath
Stop-TrackedProcess -PidPath $uiPidPath

$api = Start-DotnetService `
    -Name "admin-api" `
    -Project "src\Hedgehog.Admin.Api\Hedgehog.Admin.Api.csproj" `
    -Url "http://0.0.0.0:5081" `
    -PidPath $apiPidPath

$ui = Start-DotnetService `
    -Name "admin-ui" `
    -Project "src\Hedgehog.Admin.Ui\Hedgehog.Admin.Ui.csproj" `
    -Url "http://0.0.0.0:5082" `
    -PidPath $uiPidPath

Write-Host "Hedgehog Admin API pid=$($api.Id) url=http://0.0.0.0:5081"
Write-Host "Hedgehog Admin UI pid=$($ui.Id) url=http://0.0.0.0:5082"

