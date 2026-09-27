[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$artifactRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
$fixedReleaseRoot = [System.IO.Path]::GetFullPath((Join-Path $artifactRoot 'release'))

function Assert-ChildPath {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $Parent
    )
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $fullParent = [System.IO.Path]::GetFullPath($Parent)
    if (-not $fullPath.StartsWith(
        $fullParent + [System.IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing path outside '$fullParent': '$fullPath'."
    }
    return $fullPath
}

$targets = [System.Collections.Generic.List[string]]::new()

# Historical artifact folders are removable only when they contain compiled
# binaries. Screenshots, reports and logs remain untouched. The stable release
# folder is always excluded.
if (Test-Path -LiteralPath $artifactRoot) {
    foreach ($directory in Get-ChildItem -LiteralPath $artifactRoot -Directory -Force) {
        if ($directory.FullName.Equals($fixedReleaseRoot, [StringComparison]::OrdinalIgnoreCase)) {
            continue
        }
        $containsBinary = Get-ChildItem -LiteralPath $directory.FullName -Recurse -File -ErrorAction SilentlyContinue |
            Where-Object { $_.Extension -in @('.exe', '.dll') } |
            Select-Object -First 1
        if ($null -ne $containsBinary) {
            $targets.Add((Assert-ChildPath -Path $directory.FullName -Parent $artifactRoot))
        }
    }
}

# Keep conventional Debug/Release folders. Remove only the historical custom
# configuration names that caused dozens of parallel output trees.
$buildRoots = @(
    'apps/PopGlot.Windows/bin',
    'apps/PopGlot.Windows/obj',
    'tests/PopGlot.Windows.LogicTests/bin',
    'tests/PopGlot.Windows.LogicTests/obj',
    'tests/PopGlot.Windows.PureTests/bin',
    'tests/PopGlot.Windows.PureTests/obj'
)
foreach ($relativeRoot in $buildRoots) {
    $buildRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $relativeRoot))
    if (-not (Test-Path -LiteralPath $buildRoot)) {
        continue
    }
    foreach ($directory in Get-ChildItem -LiteralPath $buildRoot -Directory -Force) {
        if ($directory.Name -notin @('Debug', 'Release')) {
            $targets.Add((Assert-ChildPath -Path $directory.FullName -Parent $buildRoot))
        }
    }
}

$oldPackage = Join-Path $artifactRoot 'PopGlot-0.1.1-win-x64.zip'
if (Test-Path -LiteralPath $oldPackage) {
    $targets.Add((Assert-ChildPath -Path $oldPackage -Parent $artifactRoot))
}

$uniqueTargets = $targets | Sort-Object -Unique
if ($uniqueTargets.Count -eq 0) {
    Write-Host 'No redundant compiled output was found.'
    return
}

foreach ($target in $uniqueTargets) {
    if ($target.Equals($fixedReleaseRoot, [StringComparison]::OrdinalIgnoreCase) -or
        $target.StartsWith(
            $fixedReleaseRoot + [System.IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove the fixed release directory: '$target'."
    }
    if ($PSCmdlet.ShouldProcess($target, 'Remove redundant compiled output')) {
        Remove-Item -LiteralPath $target -Recurse -Force
    }
}

if ($WhatIfPreference) {
    Write-Host "Would remove $($uniqueTargets.Count) redundant output paths."
} else {
    Write-Host "Removed $($uniqueTargets.Count) redundant output paths."
}
Write-Host "Fixed release retained at: $(Join-Path $fixedReleaseRoot 'win-x64\PopGlot.exe')"
