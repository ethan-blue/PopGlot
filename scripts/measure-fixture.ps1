# PopGlot measurement-core fixture (C09 rework, round 4).
#
# Drives scripts/measure/measure-core.psm1 DIRECTLY with an injected child
# launcher (start-only, returns the live process) and an INDEPENDENT TEMP
# OUTPUT DIRECTORY — the real artifacts/perf reports are never read,
# overwritten or deleted. The production instance pre-check is tested as its
# own function against the live machine; the production entry has no bypass
# switch, so the guard stays effective.
#
# Covered paths:
#   1. success            -> startup.json PASS, warmups retained, identity
#      carried, machine.cpuModel present + non-empty + stable (C09-d)
#   2. warmup failure     -> startup-failure.json (phase=warmup), evidence kept
#   3. all counted failed -> startup-failure.json (phase=all-failed), per-run samples
#   4. bad marker JSON    -> startup.json FAIL with 'marker unreadable' sample
#   5. exit before readiness -> FAIL with the real exit code (C09-a)
#   6. marker then nonzero exit -> readiness time stays clean, sample FAILS (C09-a)
#   7. immediate vs delayed exit after marker -> readiness times agree (C09-a)
#   8. an existing startup.json survives a failing run BYTE-FOR-BYTE
#   9. package verification: missing exe / missing deps / manifest drift /
#      empty file list / missing manifest field / unmanifested extra file (C09-b)
#  10. instance pre-check refuses while a PopGlot-named process runs
#      (deterministic: a fixture-spawned renamed powershell.exe is the
#      throwaway instance; user-owned processes are never touched)
#  11. memory phase from disk: PASS / over-budget / sampling exception /
#      early exit — data and final verdict re-read from startup.json (C09-c)
#
# Usage: powershell -ExecutionPolicy Bypass -File scripts/measure-fixture.ps1

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $repoRoot 'scripts/measure/measure-core.psm1') -Force

$outDir = Join-Path ([System.IO.Path]::GetTempPath()) ("popglot-fixture-out-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$fakeDir = Join-Path ([System.IO.Path]::GetTempPath()) ("popglot-fixture-pkg-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $fakeDir | Out-Null
$failures = New-Object System.Collections.Generic.List[string]

function Assert-True([bool]$condition, [string]$name) {
    if ($condition) { Write-Host "PASS $name" }
    else { Write-Host "FAIL $name"; $script:failures.Add($name) }
}

# --- fake package: hostfxr/deps.json stubs + a fake exe whose behaviour is
#     driven by a per-RUN scenario table (run index -> behaviour) ---
Set-Content (Join-Path $fakeDir 'hostfxr.dll') 'stub' -Encoding ASCII
Set-Content (Join-Path $fakeDir 'PopGlot.deps.json') '{}' -Encoding ASCII
Set-Content (Join-Path $fakeDir 'FakeSmoke.cs') @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
static class FakeSmoke
{
    static Dictionary<string, string> Load()
    {
        var table = new Dictionary<string, string>();
        foreach (var line in File.ReadAllLines(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scenarios.txt")))
        {
            var parts = line.Split('|');
            if (parts.Length == 2) { table[parts[0]] = parts[1]; }
        }
        return table;
    }
    static int Main(string[] args)
    {
        var marker = args[0];
        var index = Path.GetFileNameWithoutExtension(marker).Replace("marker-", "");
        var table = Load();
        string b = null;
        if (table.TryGetValue(index, out b)) { }
        var behaviour = b ?? "success";
        if (behaviour == "exitcode") { return 3; }
        if (behaviour == "badjson") { File.WriteAllText(marker, "not json {{{"); return 0; }
        if (behaviour == "marker-exit-now") { File.WriteAllText(marker, "{\"trayAvailableMs\":123,\"hotkeysRegistered\":true,\"failure\":null}"); return 3; }
        if (behaviour == "marker-exit-delayed") { File.WriteAllText(marker, "{\"trayAvailableMs\":123,\"hotkeysRegistered\":true,\"failure\":null}"); Thread.Sleep(2000); return 3; }
        var failure = behaviour == "fail" ? "hotkey registration failed: fixture" : null;
        var hotkeys = failure == null ? "true" : "false";
        var failureJson = failure == null ? "null" : "\"" + failure + "\"";
        File.WriteAllText(marker, "{\"trayAvailableMs\":123,\"hotkeysRegistered\":" + hotkeys + ",\"failure\":" + failureJson + "}");
        return 0;
    }
}
'@ -Encoding ASCII
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$cscArgs = @('/nologo', '/target:exe', ('/out:' + (Join-Path $fakeDir 'PopGlot.exe')), (Join-Path $fakeDir 'FakeSmoke.cs'))
& $csc @cscArgs | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'fixture fake exe compilation failed' }
$fakeExe = Join-Path $fakeDir 'PopGlot.exe'

$identity = @{
    gitCommit = 'fixture-commit'
    dirtyDiffHash = 'fixture-diff'
    artifactSha256 = 'fixture-artifact'
    artifactFileVersion = '0.1.4'
    packageFileCount = 3
    rustFfiSha256 = 'fixture-rust'
    untrackedInputs = @()
}

function New-FakeLauncher([string]$dir, [string]$table) {
    Set-Content (Join-Path $dir 'scenarios.txt') $table -Encoding ASCII
    # C09-a: the launcher STARTS the child and returns the LIVE process —
    # no waiting; the measurement core observes readiness while it runs.
    return {
        param([string]$exe, [string]$marker, [string]$dataDir)
        Start-Process -FilePath $exe -ArgumentList @($marker, $dataDir) -PassThru -WindowStyle Hidden
    }.GetNewClosure()
}

try {
    # --- 1. success: PASS, warmups retained, identity in the report ---
    $launcher = New-FakeLauncher $fakeDir "-1|success`n-2|success`n0|success`n1|success"
    $result = Invoke-MeasurementRun -Exe $fakeExe -Runs 2 -OutputDir $outDir -Identity $identity -ChildLauncher $launcher
    Assert-True ($result.Aborted -eq $false -and $result.ExitCode -eq 0 -and $result.Verdict -eq 'PASS') 'success scenario reports PASS with exit 0'
    $r = Get-Content (Join-Path $outDir 'startup.json') -Raw | ConvertFrom-Json
    Assert-True (@($r.warmups).Count -eq 2) 'warmup results are retained in the report'
    Assert-True ($r.samples[0].hotkeysRegistered -eq $true) 'the readiness marker carries real hotkey registration'
    Assert-True ($r.buildIdentity.rustFfiSha256 -eq 'fixture-rust') 'the build identity covers the Rust artifact'
    # C09-d: the machine block must carry a STABLE cpuModel - present in the
    # persisted report, non-empty (the core falls back to the environment or
    # the literal 'unknown', never fabricates), and reproducible across calls.
    Assert-True ($null -ne $r.machine.PSObject.Properties['cpuModel']) 'the startup.json machine block carries a cpuModel field'
    Assert-True (-not [string]::IsNullOrWhiteSpace([string]$r.machine.cpuModel)) 'the machine cpuModel is non-empty'
    $freshCpuModel = (Get-CpuModel)
    Assert-True ([string]$r.machine.cpuModel -eq $freshCpuModel -and $freshCpuModel -eq (Get-CpuModel)) "the persisted cpuModel is stable across queries ($($r.machine.cpuModel))"

    # --- 2. warmup failure: structured report with warmup evidence ---
    $launcher = New-FakeLauncher $fakeDir "-1|fail`n-2|success`n0|success`n1|success"
    $result = Invoke-MeasurementRun -Exe $fakeExe -Runs 2 -OutputDir $outDir -Identity $identity -ChildLauncher $launcher
    Assert-True ($result.Aborted -and $result.Phase -eq 'warmup' -and $result.ExitCode -eq 1) 'warmup failure aborts in the warmup phase'
    Assert-True (Test-Path (Join-Path $outDir 'startup-failure.json')) 'warmup failure leaves a structured failure report'
    $f = Get-Content (Join-Path $outDir 'startup-failure.json') -Raw | ConvertFrom-Json
    Assert-True ($f.phase -eq 'warmup' -and @($f.warmups).Count -ge 1 -and $f.warmups[0].error) 'warmup failure evidence is retained'

    # --- 3. all counted runs fail ---
    $launcher = New-FakeLauncher $fakeDir "-1|success`n-2|success`n0|fail`n1|fail"
    $result = Invoke-MeasurementRun -Exe $fakeExe -Runs 2 -OutputDir $outDir -Identity $identity -ChildLauncher $launcher
    Assert-True ($result.Aborted -and $result.Phase -eq 'all-failed' -and $result.ExitCode -eq 1) 'all-counted-failed aborts with per-run samples'
    $f = Get-Content (Join-Path $outDir 'startup-failure.json') -Raw | ConvertFrom-Json
    Assert-True (@($f.samples).Count -eq 2 -and $f.samples[0].error) 'the all-fail report keeps every failure sample'

    # --- 4. bad marker JSON (mixed run) -> FAIL with evidence ---
    $launcher = New-FakeLauncher $fakeDir "-1|success`n-2|success`n0|badjson`n1|success"
    $result = Invoke-MeasurementRun -Exe $fakeExe -Runs 2 -OutputDir $outDir -Identity $identity -ChildLauncher $launcher
    Assert-True ($result.Verdict -eq 'FAIL' -and $result.ExitCode -eq 1) 'a bad marker fails the total gate'
    Assert-True (@($result.Report.samples | Where-Object { $_.error -like '*marker unreadable*' }).Count -ge 1) 'the bad-marker sample keeps its evidence'

    # --- 5. exit BEFORE readiness -> FAIL with the real exit code (C09-a) ---
    $launcher = New-FakeLauncher $fakeDir "-1|success`n-2|success`n0|exitcode`n1|success"
    $result = Invoke-MeasurementRun -Exe $fakeExe -Runs 2 -OutputDir $outDir -Identity $identity -ChildLauncher $launcher
    Assert-True ($result.Verdict -eq 'FAIL' -and $result.ExitCode -eq 1) 'an exit before readiness fails the total gate'
    Assert-True (@($result.Report.samples | Where-Object { $_.error -like '*exited before readiness (exit code 3)*' }).Count -ge 1) 'the early-exit sample keeps its real exit code'

    # --- 6/7. marker then nonzero exit: readiness stays clean (C09-a) ---
    # Mixed table: one succeeding run keeps the report alive; the two
    # post-readiness exits (immediate + delayed 2s) must both fail WITHOUT
    # their exit wait polluting the readiness time.
    $launcher = New-FakeLauncher $fakeDir "-1|success`n-2|success`n0|marker-exit-now`n1|marker-exit-delayed`n2|success"
    $result = Invoke-MeasurementRun -Exe $fakeExe -Runs 3 -OutputDir $outDir -Identity $identity -ChildLauncher $launcher
    Assert-True ($result.Verdict -eq 'FAIL' -and $result.ExitCode -eq 1) 'a nonzero exit after readiness still fails the sample'
    $nowSample = @($result.Report.samples | Where-Object { $_.kind -eq 'first-counted-post-warmup' })[0]
    $delayedSample = @($result.Report.samples | Where-Object { $_.kind -eq 'warm-cache' })[0]
    Assert-True ($nowSample.error -like '*exit code 3 after readiness*' -and $delayedSample.error -like '*exit code 3 after readiness*') 'both post-readiness exits keep their failure evidence'
    $readinessDelta = [Math]::Abs($nowSample.wallStartToReadyMs - $delayedSample.wallStartToReadyMs)
    Assert-True ($readinessDelta -lt 1500) "readiness times agree regardless of the later exit delay (delta $readinessDelta ms) - exit time never pollutes readiness"

    # --- 8. an existing startup.json survives a failing run byte-for-byte ---
    $before = Get-Content (Join-Path $outDir 'startup.json') -Raw
    $launcher = New-FakeLauncher $fakeDir "-1|fail`n-2|success`n0|success`n1|success"
    [void] (Invoke-MeasurementRun -Exe $fakeExe -Runs 2 -OutputDir $outDir -Identity $identity -ChildLauncher $launcher)
    $after = Get-Content (Join-Path $outDir 'startup.json') -Raw
    Assert-True ($before -eq $after) 'an existing startup.json is untouched (byte-identical) by a failing run'

    # --- 9. package verification (C09-b) ---
    $thrown = $false
    try { [void] (Test-ArtifactPackage -Exe (Join-Path $fakeDir 'nope')) } catch { $thrown = $true }
    Assert-True $thrown 'a missing exe is rejected'
    $noDeps = Join-Path $fakeDir 'nodeps'
    New-Item -ItemType Directory -Force -Path $noDeps | Out-Null
    Copy-Item $fakeExe (Join-Path $noDeps 'PopGlot.exe')
    Set-Content (Join-Path $noDeps 'hostfxr.dll') 'stub' -Encoding ASCII
    $thrown = $false
    try { [void] (Test-ArtifactPackage -Exe (Join-Path $noDeps 'PopGlot.exe')) } catch { $thrown = $true }
    Assert-True $thrown 'a package without deps.json is rejected'
    $drift = Join-Path $fakeDir 'drift'
    New-Item -ItemType Directory -Force -Path $drift | Out-Null
    Copy-Item (Join-Path $fakeDir 'hostfxr.dll') (Join-Path $drift 'hostfxr.dll')
    Copy-Item (Join-Path $fakeDir 'PopGlot.deps.json') (Join-Path $drift 'PopGlot.deps.json')
    Copy-Item $fakeExe (Join-Path $drift 'PopGlot.exe')
    $staleManifest = [PSCustomObject]@{
        gitCommit = 'old'; dirtyDiffHash = 'old'; untrackedInputs = @(); rustFfiSha256 = 'old'
        generatedAt = 'old'
        files = @([PSCustomObject]@{ file = 'PopGlot.exe'; sha256 = '0000000000000000000000000000000000000000000000000000000000000000' })
    }
    $staleManifest | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $drift 'build-manifest.json') -Encoding ASCII
    $thrown = $false
    try { [void] (Test-ArtifactPackage -Exe (Join-Path $drift 'PopGlot.exe')) } catch { $thrown = $true }
    Assert-True $thrown 'a stale package (manifest hash mismatch) is rejected'
    $emptyList = Join-Path $fakeDir 'emptylist'
    New-Item -ItemType Directory -Force -Path $emptyList | Out-Null
    Copy-Item (Join-Path $fakeDir 'hostfxr.dll') (Join-Path $emptyList 'hostfxr.dll')
    Copy-Item (Join-Path $fakeDir 'PopGlot.deps.json') (Join-Path $emptyList 'PopGlot.deps.json')
    Copy-Item $fakeExe (Join-Path $emptyList 'PopGlot.exe')
    $emptyManifest = [PSCustomObject]@{
        gitCommit = 'c'; dirtyDiffHash = 'd'; untrackedInputs = @(); rustFfiSha256 = 'r'
        generatedAt = 'now'; files = @()
    }
    $emptyManifest | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $emptyList 'build-manifest.json') -Encoding ASCII
    $thrown = $false
    try { [void] (Test-ArtifactPackage -Exe (Join-Path $emptyList 'PopGlot.exe')) } catch { $thrown = $true }
    Assert-True $thrown 'an EMPTY file list is explicitly rejected'
    $unmanifested = Join-Path $fakeDir 'extra'
    New-Item -ItemType Directory -Force -Path $unmanifested | Out-Null
    Copy-Item (Join-Path $fakeDir 'hostfxr.dll') (Join-Path $unmanifested 'hostfxr.dll')
    Copy-Item (Join-Path $fakeDir 'PopGlot.deps.json') (Join-Path $unmanifested 'PopGlot.deps.json')
    Copy-Item $fakeExe (Join-Path $unmanifested 'PopGlot.exe')
    $partialManifest = [PSCustomObject]@{
        gitCommit = 'c'; dirtyDiffHash = 'd'; untrackedInputs = @(); rustFfiSha256 = 'r'
        generatedAt = 'now'
        files = @([PSCustomObject]@{ file = 'PopGlot.exe'; sha256 = (Get-FileHash -Path $fakeExe -Algorithm SHA256).Hash.ToLowerInvariant() })
    }
    $partialManifest | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $unmanifested 'build-manifest.json') -Encoding ASCII
    Set-Content (Join-Path $unmanifested 'leftover.tmp') 'x' -Encoding ASCII
    $thrown = $false
    try { [void] (Test-ArtifactPackage -Exe (Join-Path $unmanifested 'PopGlot.exe')) } catch { $thrown = $true }
    Assert-True $thrown 'an unmanifested leftover file is rejected (complete-set check)'
    $missingField = Join-Path $fakeDir 'nofield'
    New-Item -ItemType Directory -Force -Path $missingField | Out-Null
    Copy-Item (Join-Path $fakeDir 'hostfxr.dll') (Join-Path $missingField 'hostfxr.dll')
    Copy-Item (Join-Path $fakeDir 'PopGlot.deps.json') (Join-Path $missingField 'PopGlot.deps.json')
    Copy-Item $fakeExe (Join-Path $missingField 'PopGlot.exe')
    $badManifest = [PSCustomObject]@{
        gitCommit = 'c'; files = @([PSCustomObject]@{ file = 'PopGlot.exe'; sha256 = (Get-FileHash -Path $fakeExe -Algorithm SHA256).Hash.ToLowerInvariant() })
    }
    $badManifest | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $missingField 'build-manifest.json') -Encoding ASCII
    $thrown = $false
    try { [void] (Test-ArtifactPackage -Exe (Join-Path $missingField 'PopGlot.exe')) } catch { $thrown = $true }
    Assert-True $thrown 'a manifest missing required fields is rejected'

    # --- 10. the instance pre-check refuses while a real PopGlot runs ---
    # Deterministic on ANY machine (the old live-machine probe failed on
    # every clean machine): a RENAMED powershell.exe becomes a live process
    # named 'PopGlot' for the duration of the check. The fixture never
    # touches a user-owned instance - only its own throwaway process below
    # is reaped.
    $guardProc = $null
    try {
        $guardDir = Join-Path $fakeDir 'guard'
        New-Item -ItemType Directory -Force -Path $guardDir | Out-Null
        $guardExe = Join-Path $guardDir 'PopGlot.exe'
        Copy-Item (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe') $guardExe -Force
        $guardProc = Start-Process -FilePath $guardExe -ArgumentList '-NoProfile', '-Command', 'Start-Sleep -Seconds 45' -PassThru -WindowStyle Hidden
        $preCheckThrew = $false
        $preCheckMessage = $null
        try { [void] (Test-InstanceConflict) } catch { $preCheckThrew = $true; $preCheckMessage = $_.Exception.Message }
        Assert-True $preCheckThrew 'the instance pre-check refuses while a PopGlot-named process runs (production guard intact)'
        Assert-True ($preCheckMessage -like '*a real PopGlot instance is running*') 'the pre-check refusal carries the running-instance evidence'
    }
    finally {
        if ($null -ne $guardProc) {
            try {
                if (-not $guardProc.HasExited) { $guardProc.Kill() }
                [void] ($guardProc.WaitForExit(5000))
            } catch {}
        }
    }

    # --- 11. memory phase from disk (C09-c): PASS / over-budget / sampling
    #        exception / early exit, with the final verdict re-read from
    #        startup.json on disk ---
    $fakeProcSource = Join-Path $fakeDir 'FakeProc.cs'
    Set-Content $fakeProcSource @'
using System;
public class FakeProc
{
    private readonly bool _throwOnWs;
    public FakeProc(bool hasExited, long ws, bool throwOnWs)
    {
        HasExited = hasExited; WorkingSet64 = ws; _throwOnWs = throwOnWs; ExitCode = 3;
    }
    public bool HasExited { get; set; }
    public int ExitCode { get; set; }
    public long WorkingSet64 { get { if (_throwOnWs) { throw new InvalidOperationException("boom"); } return WorkingSet64Backing; } set { WorkingSet64Backing = value; } }
    public long WorkingSet64Backing;
    public long PrivateMemorySize64 { get; set; }
    public TimeSpan TotalProcessorTime { get; set; }
    public void Kill() { }
    public bool WaitForExit(int ms) { return true; }
}
'@ -Encoding ASCII
    $cscArgs2 = @('/nologo', '/target:library', ('/out:' + (Join-Path $fakeDir 'FakeProc.dll')), $fakeProcSource)
    & $csc @cscArgs2 | Out-Null
    [void] [System.Reflection.Assembly]::LoadFrom((Join-Path $fakeDir 'FakeProc.dll'))

    $memoryScenarios = @(
        @{ name = 'memory PASS persists to disk'; ws = 52428800L; throwOnWs = $false; hasExited = $false; expectVerdict = 'PASS'; expectError = $false }
        @{ name = 'memory over-budget fails from disk'; ws = 157286400L; throwOnWs = $false; hasExited = $false; expectVerdict = 'FAIL'; expectError = $false }
        @{ name = 'sampling exception fails from disk'; ws = 52428800L; throwOnWs = $true; hasExited = $false; expectVerdict = 'FAIL'; expectError = $true }
        @{ name = 'early exit fails from disk'; ws = 52428800L; throwOnWs = $false; hasExited = $true; expectVerdict = 'FAIL'; expectError = $true }
    )
    foreach ($scenario in $memoryScenarios) {
        $launcher = New-FakeLauncher $fakeDir "-1|success`n-2|success`n0|success`n1|success"
        $factory = {
            New-Object FakeProc -ArgumentList @($scenario.hasExited, $scenario.ws, $scenario.throwOnWs)
        }.GetNewClosure()
        $sampler = {
            param([int]$stab, [int]$win)
            Invoke-MemorySampling -ProcessFactory $factory -StabilizeSeconds $stab -CpuWindowSeconds $win
        }.GetNewClosure()
        $result = Invoke-MeasurementRun -Exe $fakeExe -Runs 2 -OutputDir $outDir -Identity $identity -ChildLauncher $launcher -MemorySampler $sampler -MemoryStabilizeSeconds 0 -MemoryCpuWindowSeconds 0
        Assert-True ($result.Aborted -eq $false) "$($scenario.name): the run itself completes"
        $r = Get-Content (Join-Path $outDir 'startup.json') -Raw | ConvertFrom-Json
        Assert-True ($null -ne $r.memory) "$($scenario.name): memory data is on disk"
        if (-not $scenario.expectError) {
            Assert-True ($r.memory.workingSetBytes -eq $scenario.ws) "$($scenario.name): the memory data on disk matches the sample"
        }
        Assert-True ($r.verdict -eq $scenario.expectVerdict) "$($scenario.name): the final verdict on disk is $($scenario.expectVerdict)"
    }
}
finally {
    Remove-Module measure-core -ErrorAction SilentlyContinue
    try { Remove-Item $outDir -Recurse -Force -ErrorAction SilentlyContinue } catch {}
    try { Remove-Item $fakeDir -Recurse -Force -ErrorAction SilentlyContinue } catch {}
}

Write-Host ""
if ($failures.Count -eq 0) {
    Write-Host 'Fixture result: ALL CORE ERROR PATHS VERIFIED (isolated temp output, fake launcher, no real environment touched).'
    exit 0
}
Write-Host "Fixture result: $($failures.Count) FAILED: $($failures -join '; ')"
exit 1
