param([Parameter(Mandatory = $true)][string]$ArchivePath)
$ErrorActionPreference = 'Stop'
$expectedArchiveHash = 'F031D61AD96CD018F8413AF948613B9EC94CC0EFF1DEE670461AA490E933522C'
if ((Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash -ne $expectedArchiveHash) {
    throw 'Not the locked crates.io genai 0.7.1 archive.'
}
$repoRoot = Split-Path -Parent $PSScriptRoot
$vendorRoot = Join-Path $repoRoot 'vendor/genai'
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$scratchPath = Join-Path $tempRoot ('popglot-genai-verify-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratchPath | Out-Null
function Read-NormalizedText([string]$Path) {
    return [IO.File]::ReadAllText($Path).Replace("`r`n", "`n")
}
try {
    tar -xf $ArchivePath -C $scratchPath
    if ($LASTEXITCODE -ne 0) { throw 'Archive extraction failed.' }
    $upstreamRoot = Join-Path $scratchPath 'genai-0.7.1'
    $sourceFiles = @(Get-ChildItem -LiteralPath (Join-Path $upstreamRoot 'src') -Recurse -File)
    if ($sourceFiles.Count -ne 156) { throw 'Unexpected upstream source count.' }
    $expectedFiles = @('Cargo.toml', 'README.md', 'LICENSE-MIT', 'LICENSE-APACHE', 'POPGLOT-PATCH.md')
    $expectedFiles += @($sourceFiles | ForEach-Object {
        [IO.Path]::GetRelativePath($upstreamRoot, $_.FullName).Replace('\', '/')
    })
    $localFiles = @(Get-ChildItem -LiteralPath $vendorRoot -Recurse -File -Force | ForEach-Object {
        [IO.Path]::GetRelativePath($vendorRoot, $_.FullName).Replace('\', '/')
    })
    $expectedPaths = ($expectedFiles | Sort-Object) -join "`n"
    $localPaths = ($localFiles | Sort-Object) -join "`n"
    if ($expectedPaths -cne $localPaths) {
        throw 'Unexpected vendored distribution files.'
    }
    $extraDirectories = @(Get-ChildItem -LiteralPath $vendorRoot -Directory -Force | Where-Object Name -ne 'src')
    if ($extraDirectories.Count -ne 0) { throw 'Unexpected vendored distribution directories.' }
    $payloadExport = @'
/// Build a protocol payload without performing I/O. PopGlot's only SDK patch.
/// Transport, credentials and endpoint selection remain with the caller.
pub fn prepare_payload(
    target: crate::ServiceTarget,
    request: crate::chat::ChatRequest,
    options: &crate::chat::ChatOptions,
    stream: bool,
) -> crate::Result<serde_json::Value> {
    let service = if stream { ServiceType::ChatStream } else { ServiceType::Chat };
    AdapterDispatcher::to_web_request_data(
        target, service, request,
        crate::chat::ChatOptionsSet::default().with_chat_options(Some(options)),
    ).map(|request| request.payload)
}
'@
    $payloadExport = $payloadExport.Replace("`r`n", "`n")
    foreach ($file in $sourceFiles) {
        $relative = [IO.Path]::GetRelativePath($upstreamRoot, $file.FullName).Replace('\', '/')
        $expected = Read-NormalizedText $file.FullName
        if ($relative -eq 'src/adapter/mod.rs') {
            $anchor = "pub use adapter_kind::*;`n"
            if (-not $expected.Contains($anchor)) { throw 'Payload export anchor changed upstream.' }
            $expected = $expected.Replace($anchor, $anchor + "`n" + $payloadExport + "`n")
        }
        if ((Read-NormalizedText (Join-Path $vendorRoot $relative)) -cne $expected) {
            throw "Unexpected upstream modification: $relative"
        }
    }
    foreach ($file in @('README.md', 'LICENSE-MIT', 'LICENSE-APACHE')) {
        if ((Read-NormalizedText (Join-Path $vendorRoot $file)) -cne
            (Read-NormalizedText (Join-Path $upstreamRoot $file))) {
            throw "Upstream distribution file changed: $file"
        }
    }
    # The library manifest retains upstream package/features/runtime dependencies,
    # but does not refer to absent development targets or development dependencies.
    $manifest = Read-NormalizedText (Join-Path $upstreamRoot 'Cargo.toml')
    $manifest = $manifest.Replace(
        "# If you are reading this file be aware that the original Cargo.toml`n# will likely look very different (and much more reasonable).`n# See Cargo.toml.orig for the original contents.",
        "# PopGlot retains only the library distribution. See POPGLOT-PATCH.md for`n# provenance and the deliberately omitted upstream development targets.")
    $targetStart = $manifest.IndexOf("[[example]]`n", [StringComparison]::Ordinal)
    $targetEnd = $manifest.IndexOf('[dependencies.aws-config]', [StringComparison]::Ordinal)
    if ($targetStart -lt 0 -or $targetEnd -le $targetStart) { throw 'Upstream target layout changed.' }
    $manifest = $manifest.Remove($targetStart, $targetEnd - $targetStart)
    $devStart = $manifest.IndexOf('[dev-dependencies.base64]', [StringComparison]::Ordinal)
    $devEnd = $manifest.IndexOf('[lints.rust]', [StringComparison]::Ordinal)
    if ($devStart -lt 0 -or $devEnd -le $devStart) { throw 'Upstream development dependency layout changed.' }
    $manifest = $manifest.Remove($devStart, $devEnd - $devStart)
    if ((Read-NormalizedText (Join-Path $vendorRoot 'Cargo.toml')) -cne $manifest) {
        throw 'Unexpected vendored library manifest modification.'
    }
    Write-Output "PASS: genai 0.7.1 archive checksum, 156 sources, README, both licenses and minimal library manifest; only the exact payload export differs in source."
} finally {
    $resolvedScratch = [IO.Path]::GetFullPath($scratchPath)
    if (-not $resolvedScratch.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path -Leaf $resolvedScratch) -notmatch '^popglot-genai-verify-[a-f0-9]{32}$') {
        throw 'Refusing temporary-directory cleanup outside the verification directory.'
    }
    Remove-Item -LiteralPath $resolvedScratch -Recurse -Force
}
