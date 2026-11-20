[CmdletBinding()]
param([string]$Version = '0.1.44')
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Expected a numeric release version.' }
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$evidence = Join-Path $root "artifacts/review-$Version"
$publish = Join-Path $evidence 'publish'
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
[xml]$project = Get-Content 'apps/PopGlot.Windows/PopGlot.Windows.csproj' -Raw
if ($project.Project.PropertyGroup.Version -notcontains $Version) { throw 'Candidate version mismatch.' }

& dotnet publish 'apps/PopGlot.Windows/PopGlot.Windows.csproj' -c Release -r win-x64 --self-contained true -o $publish /p:DebugType=None /p:DebugPortablePdb=false /p:ContinuousIntegrationBuild=true
if ($LASTEXITCODE -ne 0) { throw 'Candidate publish failed.' }
$native = Join-Path $publish 'popglot_ffi.dll'
$builtNative = Join-Path $root 'target/release/popglot_ffi.dll'
if ((Get-FileHash $native).Hash -ne (Get-FileHash $builtNative).Hash) { throw 'Candidate native core is stale.' }
foreach ($file in @('PopGlot.exe', 'PopGlot.dll', 'hostfxr.dll', 'System.Private.CoreLib.dll', 'help/provider-setup/index.md')) {
    if (-not (Test-Path (Join-Path $publish $file))) { throw "Missing candidate file: $file" }
}
$appVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $publish 'PopGlot.dll')).FileVersion
if (-not $appVersion.StartsWith($Version + '.')) { throw "Unexpected app file version $appVersion" }

# Real packaged executable startup, isolated storage and in-memory credentials.
$marker = Join-Path $evidence 'package-smoke.json'
$smokeRoot = Join-Path $evidence ('smoke-data-' + [Guid]::NewGuid().ToString('N'))
try {
    if (Test-Path $marker) { Remove-Item $marker -Force }
    $process = Start-Process -FilePath (Join-Path $publish 'PopGlot.exe') -ArgumentList @('--smoke-startup', ('"' + $marker + '"'), ('"' + $smokeRoot + '"')) -PassThru
    if (-not $process.WaitForExit(30000)) { $process.Kill(); throw 'Packaged startup timed out.' }
    if ($process.ExitCode -ne 0) { throw "Packaged startup failed; see $marker" }
    if (-not (Test-Path $marker)) { throw 'Packaged startup did not produce fresh smoke evidence.' }
    $smoke = Get-Content $marker -Raw | ConvertFrom-Json
    if ($smoke.version -ne $appVersion) { throw 'Packaged smoke version does not match the newly built DLL.' }
    if ($smoke.failure -or -not $smoke.hotkeysRegistered) { throw 'Packaged tray/hotkey startup did not succeed.' }
} finally {
    if (Test-Path $smokeRoot) { Remove-Item $smokeRoot -Recurse -Force }
}

$manifestPath = Join-Path $publish 'candidate-manifest.json'
$files = @(Get-ChildItem $publish -Recurse -File | Where-Object { $_.FullName -ne $manifestPath } | ForEach-Object {
    @{ path = [IO.Path]::GetRelativePath($publish, $_.FullName); sha256 = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
$sourceInputs = @(git ls-files --cached --others --exclude-standard apps crates vendor tests scripts docs/help Cargo.toml Cargo.lock Directory.Build.props global.json rust-toolchain.toml | Sort-Object -Unique | ForEach-Object {
    if (Test-Path -LiteralPath $_ -PathType Leaf) { @{ path = $_; sha256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant() } }
})
$manifest = @{ version = $Version; sourceHead = (git rev-parse HEAD); sourceInputs = $sourceInputs; status = 'candidate-not-publicly-released'; files = $files }
$manifest | ConvertTo-Json -Depth 8 | Set-Content $manifestPath -Encoding utf8NoBOM
foreach ($item in $files) {
    if ((Get-FileHash (Join-Path $publish $item.path)).Hash.ToLowerInvariant() -ne $item.sha256) { throw "Candidate changed: $($item.path)" }
}
$zip = Join-Path $evidence "PopGlot-v$Version-win-x64-candidate.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
[IO.Compression.ZipFile]::CreateFromDirectory($publish, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $([IO.Path]::GetFileName($zip))" | Set-Content ($zip + '.sha256') -Encoding utf8NoBOM
Write-Output "Candidate version=$appVersion; native DLL matches Release build; packaged startup hotkeys=$($smoke.hotkeysRegistered), trayMs=$($smoke.trayAvailableMs)."
Write-Output "Portable candidate: $zip"
Write-Output 'This script does not create a GitHub Release or an installer, and does not claim external AI inference passed.'
