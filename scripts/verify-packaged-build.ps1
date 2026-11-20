[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$PublishDir = 'artifacts/release/win-x64',
    [string]$EvidenceDir = 'artifacts/release-verification',
    [string]$SourceCommit
)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Expected a stable numeric version.' }
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$publish = [IO.Path]::GetFullPath((Join-Path $root $PublishDir))
$evidence = [IO.Path]::GetFullPath((Join-Path $root $EvidenceDir))
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
$head = (git rev-parse HEAD).Trim()
if ($SourceCommit) {
    $expectedCommit = (git rev-parse ($SourceCommit + '^{commit}')).Trim()
    if ($LASTEXITCODE -ne 0 -or $head -ne $expectedCommit) { throw 'Checkout does not match the release source commit.' }
}
foreach ($name in @('PopGlot.exe', 'PopGlot.dll', 'popglot_ffi.dll', 'hostfxr.dll', 'System.Private.CoreLib.dll', 'help/provider-setup/index.md')) {
    if (-not (Test-Path (Join-Path $publish $name))) { throw "Missing packaged file: $name" }
}
$appVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $publish 'PopGlot.dll')).FileVersion
if ($appVersion -ne ($Version + '.0')) { throw "Packaged DLL version is $appVersion, not $Version.0" }
$native = Join-Path $publish 'popglot_ffi.dll'
if ((Get-FileHash $native).Hash -ne (Get-FileHash (Join-Path $root 'target/release/popglot_ffi.dll')).Hash) {
    throw 'Published native core differs from the Release build.'
}
$marker = Join-Path $evidence 'package-smoke.json'
$smokeData = Join-Path $evidence ('smoke-data-' + [Guid]::NewGuid().ToString('N'))
$process = $null
try {
    if (Test-Path $marker) { Remove-Item $marker -Force }
    $process = Start-Process -FilePath (Join-Path $publish 'PopGlot.exe') -ArgumentList @('--smoke-startup', ('"' + $marker + '"'), ('"' + $smokeData + '"')) -PassThru
    if (-not $process.WaitForExit(30000)) {
        $process.Kill(); $process.WaitForExit(); throw 'Packaged executable startup timed out.'
    }
    if ($process.ExitCode -ne 0 -or -not (Test-Path $marker)) { throw 'Packaged executable did not produce successful fresh startup evidence.' }
    $smoke = Get-Content $marker -Raw | ConvertFrom-Json
    if ($smoke.failure -or -not $smoke.hotkeysRegistered -or $smoke.version -ne $appVersion) {
        throw 'Packaged version/tray/hotkey startup verification failed.'
    }
} finally {
    if ($process) { $process.Dispose() }
    if (Test-Path $smokeData) { Remove-Item $smokeData -Recurse -Force }
}
$manifestPath = Join-Path $publish 'release-manifest.json'
$files = @(Get-ChildItem $publish -Recurse -File | Where-Object { $_.FullName -ne $manifestPath } | Sort-Object FullName | ForEach-Object {
    @{ path = [IO.Path]::GetRelativePath($publish, $_.FullName).Replace('\', '/'); sha256 = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
@{
    version = $Version
    commit = $head
    tag = 'v' + $Version
    verifiedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    packagedStartup = @{ version = $smoke.version; hotkeysRegistered = $smoke.hotkeysRegistered; trayAvailableMs = $smoke.trayAvailableMs }
    files = $files
} | ConvertTo-Json -Depth 8 | Set-Content $manifestPath -Encoding utf8NoBOM
Write-Output "Packaged verification PASS: version=$appVersion; commit=$head; files=$($files.Count); hotkeys=$($smoke.hotkeysRegistered); startupMs=$($smoke.trayAvailableMs)"
