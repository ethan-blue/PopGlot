[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [switch]$RunVerify
)

$ErrorActionPreference = 'Stop'

# 确保 dotnet 和 cargo 路径在 PATH 中可用
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue
if (-not $dotnet -and (Test-Path "$env:ProgramFiles\dotnet\dotnet.exe")) {
    $dotnet = "$env:ProgramFiles\dotnet\dotnet.exe"
    $env:PATH = "$env:ProgramFiles\dotnet;$env:PATH"
}
if (-not $dotnet) {
    throw 'dotnet CLI was not found. Install .NET SDK first.'
}

$cargoBin = Join-Path $env:USERPROFILE '.cargo\bin'
if ((Test-Path $cargoBin) -and ($env:PATH -notlike "*$cargoBin*")) {
    $env:PATH = "$cargoBin;$env:PATH"
}

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

Write-Host "PopGlot 重新编译脚本开始运行..." -ForegroundColor Cyan

if ($RunVerify) {
    Write-Host "正在运行验证测试套件 (verify.ps1)..." -ForegroundColor Cyan
    & "$PSScriptRoot\verify.ps1"
    if ($LASTEXITCODE -ne 0) {
        Write-Error "verify.ps1 验证失败，退出代码: $LASTEXITCODE"
        exit $LASTEXITCODE
    }
}

Write-Host "正在清理旧构建输出..." -ForegroundColor Cyan
Remove-Item -Path "$repoRoot\apps\PopGlot.Windows\obj\*" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -Path "$repoRoot\apps\PopGlot.Windows\bin\$Configuration\*" -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "正在重新构建 PopGlot ($Configuration 配置)..." -ForegroundColor Cyan
$projectPath = Join-Path $repoRoot 'apps/PopGlot.Windows/PopGlot.Windows.csproj'
& $dotnet build $projectPath -c $Configuration
if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet build 构建失败，退出代码: $LASTEXITCODE"
    exit $LASTEXITCODE
}

$buildOutputDir = Join-Path $repoRoot "apps/PopGlot.Windows/bin/$Configuration/net10.0-windows10.0.19041.0"
$sourceExe = Join-Path $buildOutputDir 'PopGlot.exe'

if (-not (Test-Path -LiteralPath $sourceExe)) {
    Write-Error "构建输出未找到可执行文件: $sourceExe"
    exit 1
}

Write-Host "构建成功！" -ForegroundColor Green
$versionInfo = (Get-Item $sourceExe).VersionInfo
Write-Host "版本: $($versionInfo.FileVersion) ($($versionInfo.ProductVersion))" -ForegroundColor Green

$destDir = Join-Path $repoRoot '.alma/release-local'
if (-not (Test-Path -LiteralPath $destDir)) {
    New-Item -Path $destDir -ItemType Directory -Force | Out-Null
}

Write-Host "正在复制构建产物到 $destDir ..." -ForegroundColor Cyan

# 复制运行 PopGlot 所需的完整运行文件（exe, dll, runtimeconfig.json, deps.json）
$filesToCopy = Get-ChildItem -Path $buildOutputDir -File | Where-Object {
    $_.Extension -in @('.exe', '.dll', '.json')
}

foreach ($file in $filesToCopy) {
    Copy-Item -LiteralPath $file.FullName -Destination $destDir -Force
    Write-Host "  -> 已复制 $($file.Name)" -ForegroundColor Gray
}

Write-Host "`nPopGlot 最新版本 ($($versionInfo.FileVersion)) 已成功同步到 .alma/release-local/ !" -ForegroundColor Green
