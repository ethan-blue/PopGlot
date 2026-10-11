# PopGlot wake measurement — P02 (UI-WAKE-AI-CONNECTION-PLAN §5.2).
#
# Measures the REAL first-use and warm-wake paths through the production
# ShowMainWindow / close-to-tray chain, driven in-instance by the app's own
# --measure-wake mode (the same InteractionTrace timeline the hotkey/tray
# entries emit). No synthetic hotkeys, no clipboard, no user processes.
#
# This entry reuses the mandatory instance pre-check and package verification
# from scripts/measure/measure-core.psm1: every abort path writes a structured
# failure report under artifacts/perf and exits nonzero. The package must come
# from scripts/publish-package.ps1.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File scripts/publish-package.ps1
#   powershell -ExecutionPolicy Bypass -File scripts/measure-wake.ps1 `
#       [-Scenario first-open|warm-wake] [-DelaySeconds 1] [-Samples 30] [-Runs 10]
# Output: artifacts/perf/wake-<scenario>[-d<delay>].json
#
# Notes on honest naming:
# - first-open: one sample per process. The OS file cache may already be warm,
#   so results are "process first open", NOT cold-disk starts.
# - warm-wake: one process, N counted in-process show/hide cycles after one
#   untimed warmup; delay between hide and wake as requested (1/30/300 s).

param(
    [ValidateSet('first-open', 'warm-wake')]
    [string]$Scenario = 'warm-wake',
    [int]$DelaySeconds = 1,
    [int]$Samples = 30,
    # first-open only: number of counted process launches.
    [int]$Runs = 10,
    # P03 A/B: disable the DEFAULT hide-time working-set trim in the children
    # (the trim-on default is the production behaviour after the A/B verdict).
    [switch]$NoTrim,
    [string]$Exe = ""
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $PSScriptRoot 'measure/measure-core.psm1') -Force

$outDir = Join-Path $repoRoot 'artifacts/perf'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

# --- instance pre-check: MANDATORY, unconditionally first, no bypass ---
$preCheckError = $null
try {
    Test-InstanceConflict
}
catch {
    $preCheckError = $_.Exception.Message
}
if ($preCheckError) {
    Write-MeasurementFailureReport -OutputDir $outDir -Phase 'pre-check' -ErrorText $preCheckError
    throw $preCheckError
}

if (-not $Exe) {
    $Exe = Join-Path $repoRoot 'artifacts/release/win-x64/PopGlot.exe'
}

# --- package verification: any failure is structured and fatal ---
$identity = $null
$packageError = $null
try {
    $identity = Test-ArtifactPackage -Exe $Exe
}
catch {
    $packageError = $_.Exception.Message
}
if ($packageError) {
    Write-MeasurementFailureReport -OutputDir $outDir -Phase 'package-verification' -ErrorText $packageError
    throw $packageError
}

function Invoke-WakeChild {
    param([string]$exe, [string]$marker, [string]$dataDir, [string]$scenario, [int]$delay, [int]$samples)
    $arguments = @('--measure-wake', $marker, $dataDir, '--scenario', $scenario)
    if ($scenario -eq 'warm-wake') {
        $arguments += @('--delay', [string]$delay, '--samples', [string]$samples)
    }
    return Start-Process -FilePath $exe -ArgumentList $arguments -PassThru -WindowStyle Hidden
}

function Get-Percentile {
    param([double[]]$values, [double]$p)
    if ($values.Count -eq 0) { return $null }
    $sorted = $values | Sort-Object
    $rank = [Math]::Ceiling($p / 100.0 * $sorted.Count) - 1
    if ($rank -lt 0) { $rank = 0 }
    if ($rank -ge $sorted.Count) { $rank = $sorted.Count - 1 }
    return [Math]::Round($sorted[$rank], 2)
}

function Read-WakeMarker {
    param([string]$marker, [int]$timeoutSeconds)
    $deadline = [DateTime]::UtcNow.AddSeconds($timeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path $marker) {
            try {
                return Get-Content $marker -Raw | ConvertFrom-Json
            }
            catch {
                return $null
            }
        }
        Start-Sleep -Milliseconds 200
    }
    return $null
}

$allSamples = @()
$allFailures = @()
$childFailures = 0
$totalLaunches = 0

# P03 A/B: the old hide-time working-set trim is opt-in for the children.
$previousTrim = [Environment]::GetEnvironmentVariable('POPGLOT_TRIM_ON_HIDE')
if ($NoTrim) {
    $env:POPGLOT_TRIM_ON_HIDE = '0'
}

try {
    if ($Scenario -eq 'first-open') {
        # Warmup: 3 untimed process launches discarded before counting.
        for ($warmup = 0; $warmup -lt 3; $warmup++) {
            $warmData = Join-Path $env:TEMP ("popglot-wake-warm-" + [Guid]::NewGuid().ToString('N'))
            $warmMarker = Join-Path $env:TEMP ("popglot-wake-warm-" + [Guid]::NewGuid().ToString('N') + ".json")
            $child = Invoke-WakeChild -exe $Exe -marker $warmMarker -dataDir $warmData -scenario 'first-open' -delay 0 -samples 1
            $child.WaitForExit(60000) | Out-Null
            if (-not $child.HasExited) { Stop-Process -Id $child.Id -Force -ErrorAction SilentlyContinue }
        }
        for ($run = 0; $run -lt $Runs; $run++) {
            $totalLaunches++
            $dataDir = Join-Path $env:TEMP ("popglot-wake-" + [Guid]::NewGuid().ToString('N'))
            $marker = Join-Path $dataDir 'wake-marker.json'
            New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
            $child = Invoke-WakeChild -exe $Exe -marker $marker -dataDir $dataDir -scenario 'first-open' -delay 0 -samples 1
            $child.WaitForExit(120000) | Out-Null
            if (-not $child.HasExited) {
                Stop-Process -Id $child.Id -Force -ErrorAction SilentlyContinue
                $childFailures++
                $allFailures += "run ${run}: child did not exit"
                continue
            }
            if ($child.ExitCode -ne 0) { $childFailures++ }
            $report = Read-WakeMarker -marker $marker -timeoutSeconds 5
            if ($null -eq $report) {
                $childFailures++
                $allFailures += "run ${run}: marker missing or unreadable"
                continue
            }
            if ($report.failures -and $report.failures.Count -gt 0) {
                $allFailures += @($report.failures | ForEach-Object { "run ${run}: $_" })
            }
            $allSamples += @($report.samples)
        }
    }
    else {
        $totalLaunches = 1
        $dataDir = Join-Path $env:TEMP ("popglot-wake-" + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
        $marker = Join-Path $dataDir 'wake-marker.json'
        $child = Invoke-WakeChild -exe $Exe -marker $marker -dataDir $dataDir -scenario 'warm-wake' -delay $DelaySeconds -samples $Samples
        # Wake cycles include real delays; budget generously but bounded.
        $budgetSeconds = 300 + ($DelaySeconds * ($Samples + 2))
        $child.WaitForExit($budgetSeconds * 1000) | Out-Null
        if (-not $child.HasExited) {
            Stop-Process -Id $child.Id -Force -ErrorAction SilentlyContinue
            $childFailures++
            $allFailures += "child did not exit within ${budgetSeconds}s"
        }
        else {
            if ($child.ExitCode -ne 0) { $childFailures++ }
            $report = Read-WakeMarker -marker $marker -timeoutSeconds 5
            if ($null -eq $report) {
                $childFailures++
                $allFailures += "marker missing or unreadable"
            }
            else {
                if ($report.failures -and $report.failures.Count -gt 0) {
                    $allFailures += @($report.failures)
                }
                $allSamples += @($report.samples)
            }
        }
    }
}
catch {
    Write-MeasurementFailureReport -OutputDir $outDir -Phase 'execution' -ErrorText $_.Exception.Message
    throw
}
finally {
    if ($null -ne $previousTrim) {
        $env:POPGLOT_TRIM_ON_HIDE = $previousTrim
    }
    else {
        Remove-Item Env:POPGLOT_TRIM_ON_HIDE -ErrorAction SilentlyContinue
    }
}

# --- aggregation: every sample kept, failures counted into the rate ---
$interactiveValues = @($allSamples |
    Where-Object { $null -ne $_.showToInteractiveMs } |
    ForEach-Object { [double]$_.showToInteractiveMs })
$renderValues = @($allSamples |
    Where-Object { $null -ne $_.showToRenderCallbackMs } |
    ForEach-Object { [double]$_.showToRenderCallbackMs })
$constructionValues = @($allSamples |
    Where-Object { $null -ne $_.constructionMs } |
    ForEach-Object { [double]$_.constructionMs })

$totalCounted = $allSamples.Count
$failureRate = if ($totalCounted + $childFailures -gt 0) {
    [Math]::Round($childFailures / ($totalCounted + $childFailures), 4)
}
else { 0 }

$reportOut = [ordered]@{
    scenario = $Scenario
    delaySeconds = $DelaySeconds
    trimOnHide = -not [bool]$NoTrim
    counted = $totalCounted
    childFailures = $childFailures
    failureRate = $failureRate
    showToInteractive = @{
        p50Ms = Get-Percentile -values $interactiveValues -p 50
        p95Ms = Get-Percentile -values $interactiveValues -p 95
        sampleCount = $interactiveValues.Count
        allMs = @($interactiveValues | ForEach-Object { [Math]::Round($_, 2) })
    }
    showToRenderCallback = @{
        p50Ms = Get-Percentile -values $renderValues -p 50
        p95Ms = Get-Percentile -values $renderValues -p 95
        sampleCount = $renderValues.Count
        allMs = @($renderValues | ForEach-Object { [Math]::Round($_, 2) })
    }
    windowConstruction = @{
        p50Ms = Get-Percentile -values $constructionValues -p 50
        p95Ms = Get-Percentile -values $constructionValues -p 95
        sampleCount = $constructionValues.Count
        allMs = @($constructionValues | ForEach-Object { [Math]::Round($_, 2) })
    }
    failures = $allFailures
    rawSamples = $allSamples
    buildIdentity = @{
        gitCommit = $identity.gitCommit
        dirtyDiffHash = $identity.dirtyDiffHash
        artifactSha256 = $identity.artifactSha256
        artifactFileVersion = $identity.artifactFileVersion
        packageFileCount = $identity.packageFileCount
        rustFfiSha256 = $identity.rustFfiSha256
        untrackedInputs = $identity.untrackedInputs
    }
    measuredUtc = [DateTime]::UtcNow
}

$outName = if ($Scenario -eq 'warm-wake') {
    if ($NoTrim) { "wake-warm-d$DelaySeconds-notrim.json" } else { "wake-warm-d$DelaySeconds.json" }
}
elseif ($NoTrim) { "wake-first-open-notrim.json" }
else { "wake-first-open.json" }
$outPath = Join-Path $outDir $outName
$reportOut | ConvertTo-Json -Depth 8 | Set-Content $outPath -Encoding UTF8

Write-Host ""
Write-Host "[wake $Scenario delay=${DelaySeconds}s] counted=$totalCounted childFailures=$childFailures failureRate=$failureRate"
Write-Host "  show->interactive   P50=$(Get-Percentile -values $interactiveValues -p 50)ms P95=$(Get-Percentile -values $interactiveValues -p 95)ms (n=$($interactiveValues.Count))"
Write-Host "  show->render-opportunity P50=$(Get-Percentile -values $renderValues -p 50)ms P95=$(Get-Percentile -values $renderValues -p 95)ms (n=$($renderValues.Count))"
if ($constructionValues.Count -gt 0) {
    Write-Host "  construction        P50=$(Get-Percentile -values $constructionValues -p 50)ms P95=$(Get-Percentile -values $constructionValues -p 95)ms (n=$($constructionValues.Count))"
}
Write-Host "Build identity: commit=$($identity.gitCommit) diff=$($identity.dirtyDiffHash) artifact=$($identity.artifactSha256)"
Write-Host "Raw samples: $outPath"
if ($childFailures -gt 0) {
    Write-Host "FAIL: $childFailures child failure(s); see failures[] in the report."
    exit 1
}
