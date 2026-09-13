# PopGlot measurement package publisher — C09 build-origin contract.
#
# Produces the ONLY package shape the measurement script accepts:
#   1. dotnet publish (self-contained win-x64 Release)
#   2. cargo build --release -p popglot-ffi, then the FFI dll is copied in
#   3. build-manifest.json is written INTO the publish directory, recording:
#      - git commit + tracked dirty-diff hash
#      - the UNTRACKED input list (git status --porcelain)
#      - the Rust FFI artifact hash (Rust inputs are covered)
#      - a SHA256 manifest of EVERY file in the package
# The measurement script re-verifies this manifest against the current bytes
# at measurement time, so a stale or tampered package is rejected — file
# timestamps alone are never trusted as build origin.
#
# Usage: powershell -ExecutionPolicy Bypass -File scripts/publish-package.ps1

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

& dotnet publish (Join-Path $repoRoot 'apps/PopGlot.Windows/PopGlot.Windows.csproj') `
    -c Release -r win-x64 --self-contained
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

& cargo build --release --locked -p popglot-ffi
if ($LASTEXITCODE -ne 0) { throw 'cargo build of popglot-ffi failed' }
$rustDll = Join-Path $repoRoot 'target/release/popglot_ffi.dll'
if (-not (Test-Path $rustDll)) { throw "FFI dll not found at '$rustDll' after cargo build." }

$publishDir = Join-Path $repoRoot 'apps/PopGlot.Windows/bin/Release/net10.0-windows10.0.19041.0/win-x64/publish'
Copy-Item $rustDll (Join-Path $publishDir 'popglot_ffi.dll') -Force

# C09 round 4: the manifest must never certify ITSELF. A manifest left over
# from a previous publish would otherwise be hashed into the new one and
# then overwritten, making every verification fail. Remove it BEFORE the
# enumeration so the file set is exactly what this publish produced.

$gitCommit = (git -C $repoRoot rev-parse HEAD).Trim()
$diffText = (git -C $repoRoot diff HEAD) -join "`n"
$sha = [System.Security.Cryptography.SHA256]::Create()
$dirtyDiffHash = if ($diffText.Length -gt 0) {
    ([System.BitConverter]::ToString($sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($diffText))) -replace '-', '').Substring(0, 16).ToLowerInvariant()
} else { 'clean-tree' }
# Untracked inputs are part of the build origin and are recorded by name.
$untrackedInputs = @((git -C $repoRoot status --porcelain) | Where-Object { $_ -match '^\?\?' } | ForEach-Object { $_.Substring(3).Trim() })

$oldManifest = Join-Path $publishDir 'build-manifest.json'
if (Test-Path $oldManifest) { Remove-Item $oldManifest -Force }
$files = @(Get-ChildItem $publishDir -Recurse -File | ForEach-Object {
    [PSCustomObject]@{
        file = $_.FullName.Substring($publishDir.Length + 1)
        sha256 = (Get-FileHash -Path $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
})
if ($files.Count -eq 0) { throw 'the publish directory is empty; nothing to certify' }

$manifest = [PSCustomObject]@{
    gitCommit = $gitCommit
    dirtyDiffHash = $dirtyDiffHash
    untrackedInputs = $untrackedInputs
    rustFfiSha256 = (Get-FileHash -Path $rustDll -Algorithm SHA256).Hash.ToLowerInvariant()
    generatedAt = [DateTimeOffset]::UtcNow.ToString('o')
    files = $files
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $publishDir 'build-manifest.json') -Encoding UTF8
Write-Host "Measurement package published: $publishDir"
Write-Host "build-manifest.json written: commit=$gitCommit diff=$dirtyDiffHash files=$($files.Count) untracked=$($untrackedInputs.Count)"
