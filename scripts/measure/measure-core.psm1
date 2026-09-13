# measure-core.psm1 — C09 measurement core, split out of the production
# entry so the fixture can drive every error path through an INJECTED child
# launcher at the test boundary. The production instance pre-check does NOT
# live here: it stays in scripts/measure-startup.ps1, unconditional and
# unbypassable, so no production switch can skip it.

$script:SmokeTimeoutMs = 30000
$script:ReapWaitMs = 10000

function Test-InstanceConflict {
    <#
    .SYNOPSIS
    Production pre-check: refuses to measure while a real PopGlot instance
    runs (hotkey / single-instance conflicts would poison every sample).
    Called unconditionally by the production entry — no switch bypasses it.
    #>
    $running = @(Get-Process -Name 'PopGlot' -ErrorAction SilentlyContinue)
    if ($running.Count -gt 0) {
        throw ("a real PopGlot instance is running (PID $($running[0].Id)); quit it from the tray before measuring - hotkey and single-instance conflicts would poison every sample.")
    }
}

function Test-ArtifactPackage {
    <#
    .SYNOPSIS
    Verifies the WHOLE published package and its build origin:
      - hostfxr.dll and PopGlot.deps.json present;
      - build-manifest.json (emitted by scripts/publish-package.ps1) present
        with its REQUIRED fields and a NON-EMPTY file list;
      - every manifest file hash matches the current bytes;
      - the COMPLETE directory set is covered — a leftover file that the
        manifest does not list is rejected as well.
    Throws on any mismatch; a stale or incomplete package is never measured.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Exe
    )
    if (-not (Test-Path $Exe)) {
        throw "self-contained Release publish not found at '$Exe'. Run scripts/publish-package.ps1 first."
    }
    $exeDir = Split-Path -Parent $Exe
    if (-not (Test-Path (Join-Path $exeDir 'hostfxr.dll'))) {
        throw "'$exeDir' does not look like a self-contained publish (hostfxr.dll missing)."
    }
    if (-not (Test-Path (Join-Path $exeDir 'PopGlot.deps.json'))) {
        throw "'$exeDir' is missing PopGlot.deps.json - not a complete publish output."
    }
    $manifestPath = Join-Path $exeDir 'build-manifest.json'
    if (-not (Test-Path $manifestPath)) {
        throw "'$exeDir' has no build-manifest.json - the package origin is unproven. Run scripts/publish-package.ps1 (it records the build manifest covering Rust and untracked inputs)."
    }
    $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json

    # C09 round 4: required fields and a non-empty file list, explicitly.
    $missingFields = @()
    foreach ($field in 'gitCommit', 'dirtyDiffHash', 'rustFfiSha256', 'generatedAt', 'files') {
        if ($null -eq $manifest.$field) { $missingFields += $field }
    }
    if ($missingFields.Count -gt 0) {
        throw ("build-manifest.json is missing required fields: " + ($missingFields -join ', ') + ".")
    }
    if (@($manifest.files).Count -eq 0) {
        throw "build-manifest.json declares an EMPTY file list - the package origin is unproven."
    }

    # Every manifest entry must match the current bytes...
    $drift = @()
    foreach ($entry in $manifest.files) {
        $path = Join-Path $exeDir $entry.file
        if (-not (Test-Path $path)) {
            $drift += "$($entry.file) (missing)"
            continue
        }
        $hash = (Get-FileHash -Path $path -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($hash -ne $entry.sha256) {
            $drift += "$($entry.file) (hash mismatch)"
        }
    }
    if ($drift.Count -gt 0) {
        throw ("the publish package does not match its build manifest: " + ($drift -join '; ') + ". Re-run scripts/publish-package.ps1.")
    }

    # ...and the directory must contain NOTHING the manifest does not list:
    # a leftover artifact would be silently certified otherwise.
    $unmanifested = @(Get-ChildItem $exeDir -Recurse -File | Where-Object {
        $_.FullName.Substring($exeDir.Length + 1) -notin @($manifest.files | ForEach-Object { $_.file })
    } | ForEach-Object { $_.FullName.Substring($exeDir.Length + 1) })
    if ($unmanifested.Count -gt 0) {
        throw ("the publish directory contains files the manifest does not cover: " + ($unmanifested -join '; ') + ". Re-run scripts/publish-package.ps1.")
    }

    return [PSCustomObject]@{
        gitCommit = $manifest.gitCommit
        dirtyDiffHash = $manifest.dirtyDiffHash
        untrackedInputs = @($manifest.untrackedInputs)
        rustFfiSha256 = $manifest.rustFfiSha256
        artifactSha256 = (Get-FileHash -Path $Exe -Algorithm SHA256).Hash.ToLowerInvariant()
        artifactFileVersion = (Get-Item $Exe).VersionInfo.FileVersion
        packageFileCount = @($manifest.files).Count
    }
}

function Invoke-SmokeLaunch {
    <#
    .SYNOPSIS
    One launch against a fresh isolated data dir. The CHILD LAUNCHER is the
    injectable process adapter (test boundary): it STARTS the child and
    returns the live process WITHOUT waiting — C09 round 4 requires the
    readiness observation to happen while the child is still running, with
    the exit reaped and checked only AFTER readiness is captured.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Exe,
        [Parameter(Mandatory = $true)][int]$Index,
        [Parameter(Mandatory = $true)][string]$Kind,
        [Parameter(Mandatory = $true)][string]$SmokeRoot,
        [Parameter(Mandatory = $true)][scriptblock]$ChildLauncher
    )
    $dataDir = Join-Path $SmokeRoot ("run-" + $Index)
    $marker = Join-Path $SmokeRoot ("marker-" + $Index + ".json")
    $parentClock = [System.Diagnostics.Stopwatch]::StartNew()
    $child = $null
    try {
        # The launcher adapter STARTS the child and returns the live
        # process; waiting happens here, in phases.
        $child = & $ChildLauncher $Exe $marker $dataDir
    }
    catch {
        return [PSCustomObject]@{ run = $Index; kind = $Kind; error = "start exception: $($_.Exception.GetType().Name)" }
    }
    if ($null -eq $child) {
        return [PSCustomObject]@{ run = $Index; kind = $Kind; error = 'launcher returned no process' }
    }

    # Phase 1 - readiness, observed WHILE the child runs. An early exit is
    # detected inside the loop and reported with its exit code.
    $wallReadyMs = -1
    while ($parentClock.ElapsedMilliseconds -lt $script:SmokeTimeoutMs) {
        if (Test-Path $marker) {
            $wallReadyMs = $parentClock.ElapsedMilliseconds
            break
        }
        if ($child.HasExited) {
            return [PSCustomObject]@{ run = $Index; kind = $Kind; error = "child exited before readiness (exit code $($child.ExitCode))"; wallReadyMs = $parentClock.ElapsedMilliseconds }
        }
        Start-Sleep -Milliseconds 5
    }
    if ($wallReadyMs -lt 0) {
        return [PSCustomObject]@{ run = $Index; kind = $Kind; error = 'readiness marker timeout (30s)' }
    }

    # Phase 2 - parse the marker. A failed readiness is failed regardless of
    # what the process does afterwards.
    try {
        $payload = Get-Content $marker -Raw | ConvertFrom-Json
    }
    catch {
        return [PSCustomObject]@{ run = $Index; kind = $Kind; error = 'marker unreadable (invalid JSON)'; wallReadyMs = $wallReadyMs }
    }
    if ($payload.failure) {
        return [PSCustomObject]@{ run = $Index; kind = $Kind; error = $payload.failure; wallReadyMs = $wallReadyMs }
    }
    if (-not $payload.hotkeysRegistered) {
        return [PSCustomObject]@{ run = $Index; kind = $Kind; error = 'marker claims success but hotkeys are not registered'; wallReadyMs = $wallReadyMs }
    }

    # Phase 3 - reap AFTER readiness is captured: a nonzero exit (even one
    # that happens long after the marker) still fails the sample, but the
    # readiness time is never polluted by the exit wait.
    if (-not $child.WaitForExit($script:ReapWaitMs)) {
        $child.Kill()
        try { $child.WaitForExit(5000) | Out-Null } catch {}
        return [PSCustomObject]@{ run = $Index; kind = $Kind; error = 'process did not exit within 10s of readiness'; wallReadyMs = $wallReadyMs }
    }
    if ($child.ExitCode -ne 0) {
        return [PSCustomObject]@{ run = $Index; kind = $Kind; error = "child exit code $($child.ExitCode) after readiness"; wallReadyMs = $wallReadyMs }
    }
    return [PSCustomObject]@{
        run = $Index; kind = $Kind
        wallStartToReadyMs = [long]$wallReadyMs
        childTrayAvailableMs = [long]$payload.trayAvailableMs
        hotkeysRegistered = [bool]$payload.hotkeysRegistered
    }
}

function Write-MeasurementFailureReport {
    <#
    .SYNOPSIS
    Every abort path (pre-check, package verification, warmup failure,
    all-counted-failed) lands a STRUCTURED failure report with the evidence.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$OutputDir,
        [Parameter(Mandatory = $true)][string]$Phase,
        [Parameter(Mandatory = $true)][string]$ErrorText,
        [object[]]$Warmups = @(),
        [object[]]$Samples = @(),
        [hashtable]$Identity = @{}
    )
    # NOTE: PowerShell variables are case-insensitive — the local MUST NOT
    # be named $identity or it would clobber the [hashtable]$Identity param.
    $failureIdentity = [PSCustomObject]@{
        phase = $Phase
        error = $ErrorText
        gitCommit = $Identity.gitCommit
        dirtyDiffHash = $Identity.dirtyDiffHash
        artifactSha256 = $Identity.artifactSha256
        artifactFileVersion = $Identity.artifactFileVersion
        warmups = @($Warmups)
        samples = @($Samples)
        startedUtc = [DateTimeOffset]::UtcNow.ToString('o')
    }
    New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
    $failureIdentity | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $OutputDir 'startup-failure.json') -Encoding UTF8
}

function Invoke-MeasurementRun {
    <#
    .SYNOPSIS
    The warmup/counted/gate orchestration. The instance pre-check and the
    real launcher belong to the production entry; this function only needs
    the injectable child launcher (start-only, returns the live process), so
    the fixture can drive every error path in isolation and the reports land
    in an arbitrary -OutputDir (the real artifacts/perf directory is never
    touched by tests).
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Exe,
        [Parameter(Mandatory = $true)][int]$Runs,
        [Parameter(Mandatory = $true)][string]$OutputDir,
        [Parameter(Mandatory = $true)][hashtable]$Identity,
        [Parameter(Mandatory = $true)][scriptblock]$ChildLauncher,
        [int]$WarmupCount = 2,
        [scriptblock]$MemorySampler,
        [int]$MemoryStabilizeSeconds = 30,
        [int]$MemoryCpuWindowSeconds = 60
    )
    New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
    $warmups = New-Object System.Collections.Generic.List[object]
    $samples = New-Object System.Collections.Generic.List[object]
    $smokeRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("popglot-perf-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $smokeRoot | Out-Null

    $abort = $null
    try {
        for ($w = 1; $w -le $WarmupCount; $w++) {
            $warmup = Invoke-SmokeLaunch -Exe $Exe -Index (-1 * $w) -Kind "warmup-$w" -SmokeRoot $smokeRoot -ChildLauncher $ChildLauncher
            $warmups.Add($warmup)
            if ($warmup.error) {
                $abort = "warmup launch $w failed: $($warmup.error) - fix the publish before measuring."
                break
            }
        }
        if (-not $abort) {
            for ($i = 0; $i -lt $Runs; $i++) {
                $kind = if ($i -eq 0) { 'first-counted-post-warmup' } else { 'warm-cache' }
                $samples.Add((Invoke-SmokeLaunch -Exe $Exe -Index $i -Kind $kind -SmokeRoot $smokeRoot -ChildLauncher $ChildLauncher))
            }
        }
    }
    finally {
        try { Remove-Item $smokeRoot -Recurse -Force -ErrorAction SilentlyContinue } catch {}
    }

    $failures = @($samples | Where-Object { $_.error })
    $ok = @($samples | Where-Object { -not $_.error } | ForEach-Object { [long]$_.wallStartToReadyMs }) | Sort-Object

    # Pre-compute the arrays: inline method calls in argument position make
    # Windows PowerShell mis-bind the following named parameters.
    $warmupArray = $warmups.ToArray()
    $sampleArray = $samples.ToArray()
    if ($abort) {
        Write-MeasurementFailureReport -OutputDir $OutputDir -Phase 'warmup' -ErrorText $abort -Warmups $warmupArray -Samples $sampleArray -Identity $Identity
        return [PSCustomObject]@{ Aborted = $true; ExitCode = 1; Phase = 'warmup'; Verdict = 'FAIL' }
    }
    if ($ok.Count -eq 0) {
        Write-MeasurementFailureReport -OutputDir $OutputDir -Phase 'all-failed' -ErrorText 'every counted launch failed; nothing to report' -Warmups $warmupArray -Samples $sampleArray -Identity $Identity
        return [PSCustomObject]@{ Aborted = $true; ExitCode = 1; Phase = 'all-failed'; Verdict = 'FAIL' }
    }

    function Percentile([double[]]$sorted, [double]$p) {
        if ($sorted.Count -eq 1) { return $sorted[0] }
        $index = [Math]::Ceiling(($p / 100.0) * $sorted.Count) - 1
        if ($index -lt 0) { $index = 0 }
        return $sorted[[int]$index]
    }
    $p50 = Percentile ([double[]]$ok) 50
    $p95 = Percentile ([double[]]$ok) 95
    $budgetP50 = 600
    $budgetP95 = 1200
    $budgetOk = ($p50 -le $budgetP50) -and ($p95 -le $budgetP95)
    $gateOk = $budgetOk -and ($failures.Count -eq 0)

    $report = [PSCustomObject]@{
        metric = 'app_start_process_to_readiness_marker'
        boundary = 'parent-clock launch -> marker file present, observed WHILE the child runs (C09); exit is reaped and checked only after readiness'
        exe = $Exe
        buildIdentity = [PSCustomObject]@{
            gitCommit = $Identity.gitCommit
            dirtyDiffHash = $Identity.dirtyDiffHash
            configuration = 'Release self-contained win-x64 publish + popglot_ffi.dll'
            artifactSha256 = $Identity.artifactSha256
            artifactFileVersion = $Identity.artifactFileVersion
            packageFileCount = $Identity.packageFileCount
            rustFfiSha256 = $Identity.rustFfiSha256
            untrackedInputs = @($Identity.untrackedInputs)
        }
        runs = $Runs
        succeeded = $ok.Count
        failed = $failures.Count
        warmupDefinition = 'uncounted warmup launches precede the first counted run; warmup results retained below'
        warmups = $warmups.ToArray()
        packageManifestSource = 'build-manifest.json emitted by scripts/publish-package.ps1 and re-verified at measurement time'
        p50Ms = [long]$p50
        p95Ms = [long]$p95
        minMs = [long]$ok[0]
        maxMs = [long]$ok[-1]
        budget = [PSCustomObject]@{ p50Ms = $budgetP50; p95Ms = $budgetP95 }
        verdict = if ($gateOk) { 'PASS' } else { 'FAIL' }
        verdictReason = if (-not $budgetOk) { 'P50/P95 over budget' } elseif ($failures.Count -gt 0) { "failed samples present: $($failures.Count)" } else { 'within budget, no failed samples' }
        machine = [PSCustomObject]@{
            os = (Get-CimInstance Win32_OperatingSystem).Caption
            cores = [Environment]::ProcessorCount
            dotnet = [Environment]::Version.ToString()
        }
        startedUtc = [DateTimeOffset]::UtcNow.ToString('o')
        samples = $samples.ToArray()
    }
    # C09-c: an OPTIONAL memory sampler runs inside the orchestration so the
    # final report — with memory data, PASS or FAIL — is persisted exactly
    # once, from the same call that built the samples.
    if ($MemorySampler) {
        $memory = & $MemorySampler $MemoryStabilizeSeconds $MemoryCpuWindowSeconds
        $report | Add-Member -NotePropertyName memory -NotePropertyValue $memory
        if ($memory.verdict -eq 'FAIL') {
            $report.verdict = 'FAIL'
            $report.verdictReason = "$($report.verdictReason); memory/CPU phase failed"
            $gateOk = $false
        }
    }
    $report | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $OutputDir 'startup.json') -Encoding UTF8
    return [PSCustomObject]@{
        Aborted = $false
        ExitCode = if ($gateOk) { 0 } else { 1 }
        Verdict = $report.verdict
        Report = $report
    }
}

function Invoke-MemorySampling {
    <#
    .SYNOPSIS
    C09 round 4: the memory/CPU phase with an INJECTABLE process factory
    (test boundary). The result hashtable is returned to the caller, which
    MUST persist it — PASS and FAIL alike. The child is reaped and its exit
    CONFIRMED in the finally block; an unconfirmed reap is recorded as an
    error on the result, never silently dropped.
    #>
    param(
        [Parameter(Mandatory = $true)][scriptblock]$ProcessFactory,
        [int]$StabilizeSeconds = 30,
        [int]$CpuWindowSeconds = 60
    )
    $result = $null
    $child = $null
    try {
        $child = & $ProcessFactory
        if ($null -eq $child) {
            return [PSCustomObject]@{ error = 'process factory returned no child'; verdict = 'FAIL' }
        }
        Start-Sleep -Seconds $StabilizeSeconds
        if ($child.HasExited) {
            return [PSCustomObject]@{
                error = "measurement instance exited early (code $($child.ExitCode))"
                verdict = 'FAIL'
            }
        }
        # V04/C09 lesson: PowerShell swallows .NET property-getter exceptions
        # in expression context (returns $null) — an unreadable counter must
        # become an explicit failure, never a silent zero.
        $wsBefore = $child.WorkingSet64
        if ($null -eq $wsBefore) {
            return [PSCustomObject]@{ error = 'working set unreadable'; verdict = 'FAIL' }
        }
        $privateBefore = $child.PrivateMemorySize64
        if ($null -eq $privateBefore) {
            return [PSCustomObject]@{ error = 'private bytes unreadable'; verdict = 'FAIL' }
        }
        $cpuBefore = $child.TotalProcessorTime
        Start-Sleep -Seconds $CpuWindowSeconds
        if ($child.HasExited) {
            return [PSCustomObject]@{
                error = "measurement instance exited during the CPU window (code $($child.ExitCode))"
                verdict = 'FAIL'
            }
        }
        $cpuAfter = $child.TotalProcessorTime
        $idleCpuNormalized = if ($CpuWindowSeconds -gt 0) {
            [Math]::Round((($cpuAfter - $cpuBefore).TotalMilliseconds / ($CpuWindowSeconds * 1000.0)) * 100.0 / [Environment]::ProcessorCount, 3)
        } else { 0 }
        $memoryOk = ($wsBefore -le 125829120) -and ($idleCpuNormalized -le 0.5)
        $result = [PSCustomObject]@{
            boundary = 'one isolated --background instance; POPGLOT_DATA_ROOT temp profile + in-memory credentials + registry self-heal skipped + consent unset (nothing sent); stabilization then normalized CPU window'
            workingSetBytes = $wsBefore
            privateBytes = $privateBefore
            idleCpuPercentNormalized = $idleCpuNormalized
            idleCpuAlgorithm = '(delta TotalProcessorTime ms / window ms) * 100 / core count; reported with core count'
            wsTargetBytes = 83886080
            wsHardGateBytes = 125829120
            verdict = if ($memoryOk) { 'PASS' } else { 'FAIL' }
            verdictReason = if ($wsBefore -gt 125829120) { 'WS over 120MiB hard gate' } elseif ($idleCpuNormalized -gt 0.5) { 'idle CPU over 0.5% normalized' } else { 'within memory and CPU budgets' }
        }
    }
    catch {
        # C09: a sampling exception is a structured memory failure.
        $result = [PSCustomObject]@{
            error = "sampling exception: $($_.Exception.GetType().Name)"
            verdict = 'FAIL'
        }
    }
    finally {
        # C09: the measurement child is ALWAYS reaped here and its exit is
        # confirmed; a failed reap is recorded on the result, never dropped.
        if ($null -ne $child) {
            try {
                if (-not $child.HasExited) { $child.Kill() }
                if (-not $child.WaitForExit(5000)) {
                    $result = [PSCustomObject]@{
                        error = 'child did not confirm exit within 5s of Kill'
                        verdict = 'FAIL'
                    }
                }
            }
            catch {
                $result = [PSCustomObject]@{
                    error = "reap exception: $($_.Exception.GetType().Name)"
                    verdict = 'FAIL'
                }
            }
        }
    }
    return $result
}

Export-ModuleMember -Function Test-InstanceConflict, Test-ArtifactPackage, Invoke-SmokeLaunch, Write-MeasurementFailureReport, Invoke-MeasurementRun, Invoke-MemorySampling
