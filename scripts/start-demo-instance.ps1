<#
.SYNOPSIS
    启动一个用于录屏的隔离 PopGlot 实例。

.DESCRIPTION
    把配置、历史、提示词等数据全部重定向到 %TEMP%\PopGlot-Demo，
    与日常使用的实例完全隔离，录屏时不会泄露真实历史、引擎列表或密钥名称。

    注意：本脚本只隔离 PopGlot 自己的数据目录，不隔离 Windows 凭据管理器，
    也不隔离外部账号。录制前请自行关闭聊天窗口、邮箱、通知预览和含个人
    路径的终端。完整隔离请使用专门的 Windows 演示账号。

    完整分镜与隐私检查清单见 docs/VIDEO_DEMO.md，录什么、发哪里见
    docs/promotion/PROMOTION.md。

.EXAMPLE
    ./scripts/start-demo-instance.ps1
    启动隔离实例。

.EXAMPLE
    ./scripts/start-demo-instance.ps1 -Reset
    先清空上一次的演示数据，再启动。

.EXAMPLE
    ./scripts/start-demo-instance.ps1 -ExePath "D:\tmp\PopGlot-v0.1.35-win-x64\PopGlot.exe"
    用解压出来的便携包启动，而不是本地构建产物。
#>
[CmdletBinding()]
param(
    # PopGlot.exe 的路径。不填时按 dist\release\PopGlot.exe、dist\PopGlot.exe 顺序查找。
    [string]$ExePath,

    # 启动前清空演示数据目录。
    [switch]$Reset,

    # 只做检查和路径解析，不真的启动程序。
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($ExePath)) {
    $candidates = @(
        (Join-Path $repoRoot 'dist\release\PopGlot.exe'),
        (Join-Path $repoRoot 'dist\PopGlot.exe')
    )
    $ExePath = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $ExePath) {
        throw '没有找到 PopGlot.exe。请先发布（dotnet publish apps/PopGlot.Windows/PopGlot.Windows.csproj -c Release -o dist\release），或用 -ExePath 指定解压出来的 PopGlot.exe。'
    }
}

if (-not (Test-Path $ExePath)) {
    throw "指定的程序不存在：$ExePath"
}

$ExePath = (Resolve-Path $ExePath).Path

# 演示数据目录。PopGlot 读 POPGLOT_DATA_ROOT 作为数据根。
$demoRoot = Join-Path $env:TEMP 'PopGlot-Demo'

if ($Reset -and (Test-Path $demoRoot)) {
    Remove-Item -Recurse -Force $demoRoot
    Write-Host "已清空演示数据：$demoRoot"
}

if (-not (Test-Path $demoRoot)) {
    New-Item -ItemType Directory -Path $demoRoot -Force | Out-Null
}

# 单实例检查：如果托盘里已经有日常实例在跑，演示请求会被转交给它，
# 录出来的是日常数据，等于白录还可能泄露隐私。
$running = Get-Process -Name 'PopGlot' -ErrorAction SilentlyContinue
if ($running) {
    Write-Warning "检测到 $($running.Count) 个正在运行的 PopGlot 进程。"
    Write-Warning '请先从托盘退出日常使用的实例，否则演示请求会被它接走。'
    if (-not $NoLaunch) {
        $answer = Read-Host '仍要继续启动吗？输入 y 继续，其它任意键取消'
        if ($answer -ne 'y') {
            Write-Host '已取消。'
            exit 0
        }
    }
}

Write-Host ''
Write-Host "程序：$ExePath"
Write-Host "演示数据：$demoRoot"
Write-Host ''

if ($NoLaunch) {
    Write-Host '（NoLaunch：未启动程序）'
    exit 0
}

$env:POPGLOT_DATA_ROOT = $demoRoot
Start-Process -FilePath $ExePath
Remove-Item Env:POPGLOT_DATA_ROOT

Write-Host '演示实例已启动。窗口不会自动弹出，图标在任务栏通知区域。'
Write-Host ''
Write-Host '录制清单（ScreenToGif，目标 docs/images/demo.gif，10-20 秒，无声）：'
Write-Host '  1. 在任意程序里选中一段英文（用无隐私的测试文本）'
Write-Host '  2. 按 Ctrl+Alt+W，等译文出现在光标旁'
Write-Host '  3. 按 Ctrl+Alt+O 打开工作台，展示左原文右译文'
Write-Host '  4. 按 Esc 关闭'
Write-Host ''
Write-Host '录完请确认：画面里没有密钥、账号、个人路径、真实聊天内容。'
Write-Host '更完整的分镜见 docs/VIDEO_DEMO.md。'
