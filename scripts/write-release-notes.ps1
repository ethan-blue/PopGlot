# Writes the GitHub Release page for one version.
# The page is the download instructions plus that version's changelog section.
# It does not link out to another markdown file.
param(
    [Parameter(Mandatory = $true)]
    [string] $Version,
    [string] $OutFile
)

$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version must look like 0.1.8, got '$Version'."
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$changelogPath = Join-Path $repoRoot 'CHANGELOG.md'
$changelog = Get-Content -Path $changelogPath -Raw -Encoding utf8
$pattern = "(?ms)^## $([regex]::Escape($Version)) [^\r\n]*\r?\n(.*?)(?=^## |\z)"
if ($changelog -notmatch $pattern) {
    throw "CHANGELOG.md has no section for $Version."
}

$section = $Matches[1].Trim()
$tag = "v$Version"
$zip = "PopGlot-$tag-win-x64.zip"
$setup = "PopGlot-$tag-win-x64-setup.exe"
$selfContained = [version]$Version -ge [version]'0.1.2'
$hasInstaller = [version]$Version -ge [version]'0.1.35'
$packageLine = if ($selfContained) {
    '这个 zip 自带 .NET 10 桌面运行时。解压后运行 PopGlot.exe，不用另外安装 .NET。'
} else {
    '这个 zip 不带运行时。先安装 .NET 10 Desktop Runtime（x64），再运行 PopGlot.exe。0.1.2 及以后的包改为自带运行时。'
}

# 0.1.35 起同时提供安装包。安装包是推荐形态，便携 zip 仍然发布。
$downloadBlock = if ($hasInstaller) {
    @"
**方式一（推荐）：下载安装包** $setup
标准 Windows 安装向导，默认按当前用户安装、不需要管理员权限；装完自动出现在开始菜单。
下一次升级直接运行新版安装包即可——它会**自动找到上次的安装位置并原地覆盖**，不需要先卸载。
卸载只删除程序文件，你的配置与历史保留在 `%LOCALAPPDATA%\PopGlot`。

**方式二：便携 zip** $zip
$packageLine

两个包各自带一个 .sha256 校验值：

``````powershell
(Get-FileHash -Path .\$setup -Algorithm SHA256).Hash.ToLower()
Get-Content .\$setup.sha256
``````
"@
} else {
    @"
下载本页的 $zip。旁边的 .sha256 是校验值：

``````powershell
(Get-FileHash -Path .\$zip -Algorithm SHA256).Hash.ToLower()
Get-Content .\$zip.sha256
``````
"@
}

$notes = @"
Windows 10（版本 19041 及以上）或 Windows 11，64 位。

$downloadBlock

$section
"@

if ($OutFile) {
    $directory = Split-Path -Parent $OutFile
    if ($directory -and -not (Test-Path $directory)) {
        New-Item -ItemType Directory -Path $directory | Out-Null
    }
    # BOM so the release action does not read the Chinese text as Latin-1.
    Set-Content -Path $OutFile -Value $notes -Encoding utf8BOM
} else {
    Write-Output $notes
}
