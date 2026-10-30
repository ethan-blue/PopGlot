# 生成 README 用的本地 SVG 徽章。发布新版本时: ./scripts/make-badges.ps1 -Version 0.1.36
param(
    [Parameter(Mandatory = $true)]
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$badgeDir = Join-Path $repoRoot 'docs/badges'
New-Item -ItemType Directory -Force -Path $badgeDir | Out-Null

# 统一 flat 风格。字体宽度按 DejaVu Sans 近似估算（shields.io 同款思路），
# 每个字符约 7px（中文按 11px），左右留白 10px。
function Get-TextWidth([string]$text, [double]$fontPx = 11) {
    $width = 0
    foreach ($ch in $text.ToCharArray()) {
        if ([int]$ch -gt 0x2E7F) { $width += $fontPx * 1.05 } else { $width += 7 }
    }
    return [Math]::Ceiling($width)
}

function New-FlatBadge([string]$file, [string]$label, [string]$value, [string]$color, [string]$labelColor = '#555') {
    $labelW = Get-TextWidth $label
    $valueW = Get-TextWidth $value
    $pad = 10
    $labelBox = $labelW + 2 * $pad
    $valueBox = $valueW + 2 * $pad
    $totalW = $labelBox + $valueBox
    $h = 20
    $svg = @"
<svg xmlns="http://www.w3.org/2000/svg" width="$totalW" height="$h" role="img" aria-label="${label}: ${value}">
  <title>${label}: ${value}</title>
  <linearGradient id="s" x2="0" y2="100%">
    <stop offset="0" stop-color="#bbb" stop-opacity=".1"/>
    <stop offset="1" stop-opacity=".1"/>
  </linearGradient>
  <clipPath id="r"><rect width="$totalW" height="$h" rx="3" fill="#fff"/></clipPath>
  <g clip-path="url(#r)">
    <rect width="$labelBox" height="$h" fill="$labelColor"/>
    <rect x="$labelBox" width="$valueBox" height="$h" fill="$color"/>
    <rect width="$totalW" height="$h" fill="url(#s)"/>
  </g>
  <g fill="#fff" text-anchor="middle" font-family="Verdana,Geneva,DejaVu Sans,sans-serif" font-size="11">
    <text x="$($labelBox / 2)" y="15">$label</text>
    <text x="$($labelBox + $valueBox / 2)" y="15">$value</text>
  </g>
</svg>
"@
    [System.IO.File]::WriteAllText((Join-Path $badgeDir $file), $svg, [System.Text.UTF8Encoding]::new($false))
    Write-Host "badge: docs/badges/$file"
}

function New-ButtonBadge([string]$file, [string]$text, [string]$sub, [string]$color) {
    $textW = Get-TextWidth $text 13
    $subW = Get-TextWidth $sub
    $pad = 22
    $w = [Math]::Max($textW, $subW) + 2 * $pad
    $h = 40
    $svg = @"
<svg xmlns="http://www.w3.org/2000/svg" width="$w" height="$h" role="img" aria-label="$text $sub">
  <title>$text $sub</title>
  <rect width="$w" height="$h" rx="6" fill="$color"/>
  <g fill="#fff" text-anchor="middle" font-family="Verdana,Geneva,DejaVu Sans,sans-serif">
    <text x="$($w / 2)" y="18" font-size="13" font-weight="bold">$text</text>
    <text x="$($w / 2)" y="32" font-size="10" opacity=".85">$sub</text>
  </g>
</svg>
"@
    [System.IO.File]::WriteAllText((Join-Path $badgeDir $file), $svg, [System.Text.UTF8Encoding]::new($false))
    Write-Host "badge: docs/badges/$file"
}

New-FlatBadge 'platform.svg' 'platform' 'Windows 10 / 11' '#0078D4'
New-FlatBadge 'version.svg' 'version' $Version '#5B67B5'
New-FlatBadge 'license.svg' 'license' 'MIT' '#3D8B6E'
New-ButtonBadge 'download-setup.svg' '下载安装包' $Version '#2E7D32'
New-ButtonBadge 'download-portable.svg' '下载便携包' $Version '#1F6FEB'
