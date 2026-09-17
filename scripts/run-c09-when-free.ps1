<#
.SYNOPSIS
    One-click runner for C09 startup measurement, memory/CPU profiling, and UI07 visual matrix screenshots.
.DESCRIPTION
    1. Pre-checks for conflicting PopGlot instances (guards against hotkey/single-instance interference).
    2. Builds and certifies a self-contained Release publish package (publish-package.ps1).
    3. Runs 30-iteration startup measurement and 90s idle memory/CPU sampling (measure-startup.ps1 -MeasureMemory).
    4. Runs LogicTests screenshot test to refresh UI07 visual matrix (56 PNGs in artifacts/screenshots/).
    5. Summarizes budget compliance against PRODUCT_SPEC.md.
#>
[CmdletBinding()]
param(
    [int]$Runs = 30,
    [switch]$SkipScreenshots
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

Write-Host "======================================================================" -ForegroundColor Cyan
Write-Host " PopGlot C09 Startup Measurement + UI07 Visual Matrix Runner" -ForegroundColor Cyan
Write-Host "======================================================================" -ForegroundColor Cyan
Write-Host ""

# 1. Instance conflict pre-check
Write-Host "[1/4] Checking for running PopGlot instances..." -ForegroundColor Yellow
$running = @(Get-Process -Name 'PopGlot' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    Write-Host "ERROR: A PopGlot instance is running (PID $($running[0].Id))." -ForegroundColor Red
    Write-Host "Please exit PopGlot from the system tray first, then re-run this script." -ForegroundColor Red
    exit 1
}
Write-Host "  No conflicting PopGlot instance detected. Ready to proceed." -ForegroundColor Green

# 2. Package publish & certification
Write-Host ""
Write-Host "[2/4] Publishing self-contained Release package with build manifest..." -ForegroundColor Yellow
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'publish-package.ps1')
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: publish-package.ps1 failed with exit code $LASTEXITCODE" -ForegroundColor Red
    exit $LASTEXITCODE
}

# 3. Startup & memory/CPU measurement
Write-Host ""
Write-Host "[3/4] Running C09 startup measurement ($Runs runs) and memory/CPU sampling..." -ForegroundColor Yellow
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'measure-startup.ps1') -Runs $Runs -MeasureMemory
if ($LASTEXITCODE -ne 0) {
    Write-Host "WARNING: measure-startup.ps1 reported non-zero exit code: $LASTEXITCODE" -ForegroundColor Yellow
}

# 4. Refresh UI07 visual matrix screenshots
if (-not $SkipScreenshots) {
    Write-Host ""
    Write-Host "[4/4] Refreshing UI07 visual matrix screenshots via LogicTests..." -ForegroundColor Yellow
    & dotnet run --project (Join-Path $repoRoot 'tests/PopGlot.Windows.LogicTests') -c Release
    if ($LASTEXITCODE -ne 0) {
        Write-Host "WARNING: LogicTests reported non-zero exit code: $LASTEXITCODE" -ForegroundColor Yellow
    }
} else {
    Write-Host ""
    Write-Host "[4/4] Skipping screenshots as requested." -ForegroundColor Gray
}

# Summary
Write-Host ""
Write-Host "======================================================================" -ForegroundColor Cyan
Write-Host " Summary & Artifacts" -ForegroundColor Cyan
Write-Host "======================================================================" -ForegroundColor Cyan
$perfFile = Join-Path $repoRoot 'artifacts/perf/startup.json'
if (Test-Path $perfFile) {
    $report = Get-Content $perfFile -Raw | ConvertFrom-Json
    Write-Host "  Startup P50: $($report.p50Ms) ms (Budget: <= 600 ms)" -ForegroundColor $(if ($report.p50Ms -le 600) { "Green" } else { "Red" })
    Write-Host "  Startup P95: $($report.p95Ms) ms (Budget: <= 1200 ms)" -ForegroundColor $(if ($report.p95Ms -le 1200) { "Green" } else { "Red" })
    Write-Host "  Startup Max: $($report.maxMs) ms" -ForegroundColor Gray
    Write-Host "  Runs: $($report.runs) (OK: $($report.succeeded), Failed: $($report.failed))" -ForegroundColor Gray
    if ($report.memory) {
        $wsMb = [math]::Round($report.memory.workingSetBytes / 1MB, 2)
        $privMb = [math]::Round($report.memory.privateBytes / 1MB, 2)
        Write-Host "  Working Set: $wsMb MiB (Target: <= 80 MiB, Hard Gate: <= 120 MiB)" -ForegroundColor $(if ($wsMb -le 120) { "Green" } else { "Red" })
        Write-Host "  Private Bytes: $privMb MiB" -ForegroundColor Gray
        Write-Host "  Idle CPU Normalized: $($report.memory.idleCpuPercentNormalized)% (Budget: <= 0.5%)" -ForegroundColor $(if ($report.memory.idleCpuPercentNormalized -le 0.5) { "Green" } else { "Red" })
        Write-Host "  Memory Verdict: $($report.memory.verdict) ($($report.memory.verdictReason))" -ForegroundColor Green
    }
    Write-Host "  Overall Verdict: $($report.verdict) ($($report.verdictReason))" -ForegroundColor $(if ($report.verdict -eq 'PASS') { "Green" } else { "Red" })
    Write-Host "  Report path: $perfFile" -ForegroundColor Cyan
}

$screenshotDir = Join-Path $repoRoot 'artifacts/screenshots'
if (Test-Path $screenshotDir) {
    $count = @(Get-ChildItem $screenshotDir -File).Count
    Write-Host "  Screenshots: $count files in $screenshotDir" -ForegroundColor Cyan
}
Write-Host "======================================================================" -ForegroundColor Cyan
