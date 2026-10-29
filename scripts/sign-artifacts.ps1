[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string[]]$Path,

    [string]$CertificatePath = $env:POPGLOT_SIGN_CERTIFICATE_PATH,

    [string]$CertificatePassword = $env:POPGLOT_SIGN_CERTIFICATE_PASSWORD,

    [string]$CertificateThumbprint = $env:POPGLOT_SIGN_CERTIFICATE_THUMBPRINT,

    [string]$TimestampUrl = $env:POPGLOT_SIGN_TIMESTAMP_URL
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($CertificatePath) -and
    [string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
    Write-Warning '未配置代码签名证书，已跳过签名。'
    return
}

function Find-SignTool {
    $command = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    $kitsRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (Test-Path -LiteralPath $kitsRoot -PathType Container) {
        $candidate = Get-ChildItem -LiteralPath $kitsRoot -Filter signtool.exe -File -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.Directory.Name -eq 'x64' } |
            Sort-Object -Property FullName -Descending |
            Select-Object -First 1
        if ($candidate) {
            return $candidate.FullName
        }
    }

    return $null
}

$signTool = Find-SignTool
if (-not $signTool) {
    throw '已配置签名证书，但未找到 signtool.exe。请安装 Windows SDK 或将 signtool.exe 加入 PATH。'
}

$certificateArguments = @()
if (-not [string]::IsNullOrWhiteSpace($CertificatePath)) {
    if (-not (Test-Path -LiteralPath $CertificatePath -PathType Leaf)) {
        throw "签名证书文件不存在：$CertificatePath"
    }

    $resolvedCertificatePath = (Resolve-Path -LiteralPath $CertificatePath).Path
    $certificateArguments += @('/f', $resolvedCertificatePath)
    if (-not [string]::IsNullOrWhiteSpace($CertificatePassword)) {
        $certificateArguments += @('/p', $CertificatePassword)
    }
}
else {
    $normalizedThumbprint = $CertificateThumbprint -replace '\s', ''
    $certificateArguments += @('/sha1', $normalizedThumbprint, '/s', 'My')
}

foreach ($artifact in $Path) {
    if (-not (Test-Path -LiteralPath $artifact -PathType Leaf)) {
        throw "待签名文件不存在：$artifact"
    }

    $resolvedArtifact = (Resolve-Path -LiteralPath $artifact).Path
    $arguments = @('sign', '/fd', 'SHA256') + $certificateArguments
    if (-not [string]::IsNullOrWhiteSpace($TimestampUrl)) {
        $arguments += @('/tr', $TimestampUrl, '/td', 'SHA256')
    }
    $arguments += $resolvedArtifact

    & $signTool @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "签名失败：$resolvedArtifact（signtool 退出代码：$LASTEXITCODE）"
    }

    Write-Host "已签名：$resolvedArtifact"
}
