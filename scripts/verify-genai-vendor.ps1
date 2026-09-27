param([Parameter(Mandatory = $true)][string]$ArchivePath)
$ErrorActionPreference = 'Stop'
$expectedArchiveHash = '1D12ABA7E9DC2C4D54654566DC3DC8383B5CB52E0CFC5754989AFE0480D933E3'
if ((Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash -ne $expectedArchiveHash) {
    throw 'Not the locked crates.io genai 0.6.5 archive.'
}
$repoRoot = Split-Path -Parent $PSScriptRoot
$vendorRoot = Join-Path $repoRoot 'vendor/genai'
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$scratchPath = Join-Path $tempRoot ('popglot-genai-verify-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratchPath | Out-Null
try {
    tar -xf $ArchivePath -C $scratchPath
    if ($LASTEXITCODE -ne 0) { throw 'Archive extraction failed.' }
    $upstreamRoot = Join-Path $scratchPath 'genai-0.6.5'
    $sourceFiles = @(Get-ChildItem -LiteralPath (Join-Path $upstreamRoot 'src') -Recurse -File)
    $localFiles = @(Get-ChildItem -LiteralPath (Join-Path $vendorRoot 'src') -Recurse -File)
    if ($sourceFiles.Count -ne $localFiles.Count) { throw 'Unexpected vendored source files.' }
    foreach ($file in $sourceFiles) {
        $relative = [IO.Path]::GetRelativePath($upstreamRoot, $file.FullName)
        $local = [IO.File]::ReadAllText((Join-Path $vendorRoot $relative)).Replace("`r`n", "`n")
        if ($relative.Replace('\', '/') -eq 'src/adapter/mod.rs') {
            $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($local)))
            if ($hash -ne '31D6547B6F9B51A46156C7D128DF354088D35C564DB4E290B9315CE866BD33BE') {
                throw 'The reviewed payload-export patch has changed.'
            }
        } elseif ($local -cne [IO.File]::ReadAllText($file.FullName).Replace("`r`n", "`n")) {
            throw "Unexpected upstream modification: $relative"
        }
    }
    foreach ($license in @('LICENSE-MIT', 'LICENSE-APACHE')) {
        if ([IO.File]::ReadAllText((Join-Path $vendorRoot $license)).Replace("`r`n", "`n") -cne
            [IO.File]::ReadAllText((Join-Path $upstreamRoot $license)).Replace("`r`n", "`n")) {
            throw "License changed: $license"
        }
    }
    Write-Output "PASS: genai 0.6.5 archive checksum, $($sourceFiles.Count) sources and both licenses; one reviewed export patch."
} finally {
    $resolvedScratch = [IO.Path]::GetFullPath($scratchPath)
    if (-not $resolvedScratch.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path -Leaf $resolvedScratch) -notmatch '^popglot-genai-verify-[a-f0-9]{32}$') {
        throw 'Refusing temporary-directory cleanup outside the verification directory.'
    }
    Remove-Item -LiteralPath $resolvedScratch -Recurse -Force
}
