[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$PublishDir = 'dist\release',

    [string]$OutputDir = 'dist',

    [switch]$SkipSignature
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot

function Resolve-RepositoryPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $repoRoot $Path))
}

function Find-Iscc {
    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    )

    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            return $candidate
        }
    }

    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    return $null
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    throw 'Version 不能为空。请通过 -Version 指定安装包版本。'
}

$resolvedPublishDir = Resolve-RepositoryPath $PublishDir
$resolvedOutputDir = Resolve-RepositoryPath $OutputDir
$appExecutable = Join-Path $resolvedPublishDir 'PopGlot.exe'
if (-not (Test-Path -LiteralPath $appExecutable -PathType Leaf)) {
    throw "发布目录中缺少 PopGlot.exe：$appExecutable"
}

if (-not (Test-Path -LiteralPath $resolvedOutputDir -PathType Container)) {
    New-Item -ItemType Directory -Path $resolvedOutputDir -Force | Out-Null
}

$iscc = Find-Iscc
if (-not $iscc -and $env:CI) {
    $choco = Get-Command choco.exe -ErrorAction SilentlyContinue
    if (-not $choco) {
        throw 'CI 环境中未找到 ISCC.exe，也未找到 Chocolatey，无法安装 Inno Setup 6。'
    }

    Write-Host '未找到 Inno Setup 6，正在通过 Chocolatey 安装。'
    & $choco.Source install innosetup -y --no-progress
    if ($LASTEXITCODE -ne 0) {
        throw "Chocolatey 安装 Inno Setup 失败，退出代码：$LASTEXITCODE"
    }

    $iscc = Find-Iscc
}

if (-not $iscc) {
    throw '未找到 Inno Setup 6 的 ISCC.exe。请安装 Inno Setup 6，或将 ISCC.exe 加入 PATH。'
}

$installerScript = Join-Path $repoRoot 'scripts\installer.iss'
$isccArguments = @(
    "/DMyAppVersion=$Version",
    "/O$resolvedOutputDir"
)

# 中文界面是可选的：某些 Inno Setup 安装形态（含部分 CI 镜像预装版本）不带
# Languages\ChineseSimplified.isl。缺它就退化成英文界面，而不是编译失败。
$chineseLanguage = Join-Path (Split-Path -Parent $iscc) 'Languages\ChineseSimplified.isl'
if (-not (Test-Path -LiteralPath $chineseLanguage -PathType Leaf)) {
    $isccArguments += '/DPOPGLOT_NO_ZH_LANG=1'
    Write-Host "未找到中文语言文件（$chineseLanguage），安装包将只带英文界面。"
}

$certificatePath = $env:POPGLOT_SIGN_CERTIFICATE_PATH
$certificateThumbprint = $env:POPGLOT_SIGN_CERTIFICATE_THUMBPRINT
$signingConfigured = -not [string]::IsNullOrWhiteSpace($certificatePath) -or
    -not [string]::IsNullOrWhiteSpace($certificateThumbprint)

if (-not $SkipSignature -and $signingConfigured) {
    $signScript = Join-Path $repoRoot 'scripts\sign-artifacts.ps1'
    # `$f` is expanded by ISCC to the file being signed. The script path is
    # passed as-is: it never contains a space in this repository layout.
    $signToolCommand = "powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $signScript -Path `$f"
    $isccArguments += '/DPOPGLOT_SIGNTOOL=1'
    $isccArguments += "/Spopglotsign=$signToolCommand"
    Write-Host '已启用安装包与卸载程序代码签名。'
}
elseif ($SkipSignature) {
    Write-Host '已通过 -SkipSignature 跳过代码签名。'
}
else {
    Write-Host '未配置签名证书，将生成未签名安装包。'
}

& $iscc @isccArguments $installerScript
if ($LASTEXITCODE -ne 0) {
    throw "ISCC 编译失败，退出代码：$LASTEXITCODE"
}

$installerName = "PopGlot-v$Version-win-x64-setup.exe"
$installerPath = Join-Path $resolvedOutputDir $installerName
if (-not (Test-Path -LiteralPath $installerPath -PathType Leaf)) {
    throw "ISCC 未生成预期的安装包：$installerPath"
}

$hash = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash.ToLowerInvariant()
$checksumPath = "$installerPath.sha256"
$checksumContent = "$hash  $installerName`n"
$utf8WithoutBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($checksumPath, $checksumContent, $utf8WithoutBom)

Write-Host "安装包：$installerPath"
Write-Host "SHA-256：$checksumPath"
