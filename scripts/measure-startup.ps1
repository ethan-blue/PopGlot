# PopGlot startup budget measurement (T15).
#
# Launches the REAL Release executable N times in --smoke-startup mode against
# fresh isolated data directories, records the tray-available milliseconds
# from each marker, and reports P50/P95. No user configuration is touched.
#
# Usage:  powershell -ExecutionPolicy Bypass -File scripts/measure-startup.ps1 [-Runs 30] [-Exe <path>]
# Output: artifacts/perf/startup.json (raw samples + P50/P95 + environment)

param(
    [int]$Runs = 30,
    [string]$Exe = ""
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $Exe) {
    $Exe = Join-Path $repoRoot 'apps/PopGlot.Windows/bin/Release/net10.0-windows10.0.19041.0/PopGlot.exe'
}
if (-not (Test-Path $Exe)) {
    # Fall back to the Debug build the developer already has.
    $Exe = Join-Path $repoRoot 'apps/PopGlot.Windows/bin/Debug/net10.0-windows10.0.19041.0/PopGlot.exe'
}
if (-not (Test-Path $Exe)) {
    throw "PopGlot.exe not found; build the app first."
}

$outDir = Join-Path $repoRoot 'artifacts/perf'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$samples = New-Object System.Collections.Generic.List[object]
$smokeRoot = Join-Path $env:TEMP ("popglot-perf-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $smokeRoot | Out-Null

try {
    for ($i = 0; $i -lt $Runs; $i++) {
        $dataDir = Join-Path $smokeRoot ("run-" + $i)
        $marker = Join-Path $smokeRoot ("marker-" + $i + ".json")
        $kind = if ($i -eq 0) { 'cold' } else { 'warm-cache' }

        $proc = Start-Process -FilePath $Exe `
            -ArgumentList @('--smoke-startup', $marker, $dataDir) `
            -PassThru -WindowStyle Hidden
        $exited = $proc.WaitForExit(30000)
        if (-not $exited) {
            $proc.Kill()
            $samples += [PSCustomObject]@{ run = $i; kind = $kind; error = 'timeout' }
            continue
        }
        if (-not (Test-Path $marker)) {
            $samples += [PSCustomObject]@{ run = $i; kind = $kind; error = 'no marker' }
            continue
        }

        $payload = Get-Content $marker -Raw | ConvertFrom-Json
        if ($payload.failure) {
            $samples += [PSCustomObject]@{ run = $i; kind = $kind; error = $payload.failure }
            continue
        }
        $samples += [PSCustomObject]@{
            run = $i; kind = $kind; trayAvailableMs = [long]$payload.trayAvailableMs
        }
    }
}
finally {
    try { Remove-Item $smokeRoot -Recurse -Force -ErrorAction SilentlyContinue } catch {}
}

$ok = @($samples | Where-Object { -not $_.error } | ForEach-Object { [long]$_.trayAvailableMs }) | Sort-Object
if ($ok.Count -eq 0) {
    throw "every smoke launch failed; nothing to report"
}

function Percentile([double[]]$sorted, [double]$p) {
    if ($sorted.Count -eq 1) { return $sorted[0] }
    $index = [Math]::Ceiling(($p / 100.0) * $sorted.Count) - 1
    if ($index -lt 0) { $index = 0 }
    return $sorted[[int]$index]
}

$p50 = Percentile ([double[]]$ok) 50
$p95 = Percentile ([double[]]$ok) 95
$budgetP50 = 600   # AI-RULES 9.1: P50 <= 600 ms
$budgetP95 = 1200  # AI-RULES 9.1: P95 <= 1200 ms

$report = [PSCustomObject]@{
    metric = 'app_startup_to_tray_available'
    exe = $Exe
    runs = $Runs
    succeeded = $ok.Count
    p50Ms = [long]$p50
    p95Ms = [long]$p95
    minMs = [long]$ok[0]
    maxMs = [long]$ok[-1]
    budget = [PSCustomObject]@{ p50Ms = $budgetP50; p95Ms = $budgetP95 }
    verdict = if ($p50 -le $budgetP50 -and $p95 -le $budgetP95) { 'PASS' } else { 'FAIL' }
    machine = [PSCustomObject]@{
        os = (Get-CimInstance Win32_OperatingSystem).Caption
        cores = [Environment]::ProcessorCount
        dotnet = [Environment]::Version.ToString()
    }
    startedUtc = [DateTimeOffset]::UtcNow.ToString('o')
    samples = $samples
}

$outPath = Join-Path $outDir 'startup.json'
$report | ConvertTo-Json -Depth 4 | Set-Content $outPath -Encoding UTF8
Write-Host ""
Write-Host "[Startup to tray available] runs=$($report.runs) ok=$($ok.Count) P50=$($report.p50Ms)ms P95=$($report.p95Ms)ms (budget P50<=${budgetP50}ms P95<=${budgetP95}ms) => $($report.verdict)"
Write-Host "Raw samples + environment: $outPath"
if ($report.verdict -eq 'FAIL') {
    exit 1
}
