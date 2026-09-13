# PopGlot startup budget measurement — production entry (C09 contract).
#
# This entry owns the MANDATORY instance pre-check (no switch bypasses it)
# and the real child launcher; the orchestration lives in
# scripts/measure/measure-core.psm1 with the child launcher as the injectable
# process adapter at the test boundary.
#
# Every abort path (pre-check, package verification, warmup failure,
# all-counted-failed) writes a structured failure report under artifacts/perf
# and exits nonzero. The package must come from scripts/publish-package.ps1
# (self-contained publish + FFI dll + build-manifest.json); the manifest is
# re-verified against the current bytes at measurement time.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File scripts/publish-package.ps1
#   powershell -ExecutionPolicy Bypass -File scripts/measure-startup.ps1 [-Runs 30] [-MeasureMemory]
# Output: artifacts/perf/startup.json (or startup-failure.json on abort)

param(
    [int]$Runs = 30,
    [string]$Exe = "",
    [switch]$MeasureMemory
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
    $Exe = Join-Path $repoRoot 'apps/PopGlot.Windows/bin/Release/net10.0-windows10.0.19041.0/win-x64/publish/PopGlot.exe'
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

# --- the real child launcher: Start-Process of the published exe in
#     --smoke-startup mode against a fresh isolated data directory ---
# C09-a: the launcher STARTS the child and returns the live process —
# readiness is observed by the measurement core WHILE the child runs, and
# the exit is reaped and checked only after readiness is captured.
$childLauncher = {
    param([string]$exe, [string]$marker, [string]$dataDir)
    Start-Process -FilePath $exe `
        -ArgumentList @('--smoke-startup', $marker, $dataDir) `
        -PassThru -WindowStyle Hidden
}

$result = Invoke-MeasurementRun -Exe $Exe -Runs $Runs -OutputDir $outDir -Identity @{
    gitCommit = $identity.gitCommit
    dirtyDiffHash = $identity.dirtyDiffHash
    artifactSha256 = $identity.artifactSha256
    artifactFileVersion = $identity.artifactFileVersion
    packageFileCount = $identity.packageFileCount
    rustFfiSha256 = $identity.rustFfiSha256
    untrackedInputs = $identity.untrackedInputs
} -ChildLauncher $childLauncher

if ($result.Aborted) {
    Write-Host "ABORT: measurement aborted in phase $($result.Phase)."
    Write-Host "Structured failure report: $(Join-Path $outDir 'startup-failure.json')"
    exit $result.ExitCode
}

$report = $result.Report

# --- memory + idle CPU sampling (C09): module-driven with an injectable
#     process factory; the result is persisted for PASS and FAIL alike ---
if ($MeasureMemory) {
    # C09-c: restore the caller's environment value, never delete blindly.
    $previousDataRoot = [Environment]::GetEnvironmentVariable('POPGLOT_DATA_ROOT')
    $memRoot = Join-Path $env:TEMP ("popglot-perf-mem-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $memRoot | Out-Null
    $env:POPGLOT_DATA_ROOT = $memRoot
    try {
        $processFactory = {
            Start-Process -FilePath $Exe -ArgumentList @('--background') `
                -PassThru -WindowStyle Hidden
        }
        $memory = Invoke-MemorySampling -ProcessFactory $processFactory
        $report | Add-Member -NotePropertyName memory -NotePropertyValue $memory
        if ($memory.verdict -eq 'FAIL') {
            $report.verdict = 'FAIL'
            $report.verdictReason = "$($report.verdictReason); memory/CPU phase failed"
        }
        # C09-c: the final report — with memory data, PASS or FAIL — is
        # ALWAYS persisted before this phase ends.
        $report | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $outDir 'startup.json') -Encoding UTF8
    }
    finally {
        if ($null -ne $previousDataRoot) {
            $env:POPGLOT_DATA_ROOT = $previousDataRoot
        }
        else {
            Remove-Item Env:POPGLOT_DATA_ROOT -ErrorAction SilentlyContinue
        }
        try { Remove-Item $memRoot -Recurse -Force -ErrorAction SilentlyContinue } catch {}
    }
}

Write-Host ""
Write-Host "[Process start -> readiness marker] runs=$($report.runs) ok=$($report.succeeded) failed=$($report.failed) P50=$($report.p50Ms)ms P95=$($report.p95Ms)ms => $($report.verdict) ($($report.verdictReason))"
Write-Host "Build identity: commit=$($report.buildIdentity.gitCommit) diff=$($report.buildIdentity.dirtyDiffHash) artifact=$($report.buildIdentity.artifactSha256) files=$($report.buildIdentity.packageFileCount)"
Write-Host "Raw samples (incl. failure evidence + warmups + package manifest): $(Join-Path $outDir 'startup.json')"
if ($report.verdict -eq 'FAIL') {
    exit 1
}
