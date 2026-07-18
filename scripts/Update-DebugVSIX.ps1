[CmdletBinding()]
param(
    [string]$RepoRoot = "C:\Code\vs2026-azure-openai-vsix",
    [string]$Configuration = "Debug",
    [switch]$RestartVS,
    [switch]$SkipKillVS
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Write-Step([string]$msg) {
    Write-Host "`n==== $msg ====" -ForegroundColor Cyan
}

function Get-VSPaths {
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    $devenv = $null

    if (Test-Path $vswhere) {
        $installPath = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
        if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($installPath)) {
            $candidate = Join-Path $installPath 'Common7\IDE\devenv.exe'
            if (Test-Path $candidate) { $devenv = $candidate }
        }
    }

    if (-not $devenv) {
        $fallback = 'C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\devenv.exe'
        if (Test-Path $fallback) { $devenv = $fallback }
    }

    if (-not $devenv) {
        throw '未找到 devenv.exe，请先确认 VS2026 已安装。'
    }

    $vsixInstaller = Join-Path (Split-Path $devenv -Parent) 'VSIXInstaller.exe'
    if (-not (Test-Path $vsixInstaller)) {
        throw "未找到 VSIXInstaller.exe: $vsixInstaller"
    }

    return [pscustomobject]@{
        Devenv = $devenv
        VSIXInstaller = $vsixInstaller
    }
}

function Stop-VSProcesses {
    $names = @('devenv', 'ServiceHub.Host.Extensibility.x64', 'DevHub')
    foreach ($name in $names) {
        $procs = Get-Process -Name $name -ErrorAction SilentlyContinue
        if ($procs) {
            foreach ($p in $procs) {
                try {
                    Write-Host "关闭进程: $($p.ProcessName) (PID=$($p.Id))"
                    Stop-Process -Id $p.Id -Force -ErrorAction Stop
                }
                catch {
                    Write-Warning "关闭失败: $($p.ProcessName) (PID=$($p.Id))，$($_.Exception.Message)"
                }
            }
        }
    }

    Start-Sleep -Seconds 2
}

Write-Step '准备路径'
$vs = Get-VSPaths
$solution = Join-Path $RepoRoot 'AzureOpenAI.Vsix.sln'
$vsix = Join-Path $RepoRoot "AzureOpenAI.Vsix\bin\$Configuration\AzureOpenAIChat.vsix"

if (-not (Test-Path $solution)) { throw "未找到解决方案: $solution" }

if (-not $SkipKillVS) {
    Write-Step '关闭 VS 相关进程（避免 VSIX 安装被阻塞）'
    Stop-VSProcesses
} else {
    Write-Step '跳过关闭 VS 进程（-SkipKillVS）'
}

Write-Step "编译 $Configuration"
& dotnet build $solution -c $Configuration
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build 失败，退出码: $LASTEXITCODE"
}

if (-not (Test-Path $vsix)) {
    throw "编译后未找到 VSIX 包: $vsix"
}
Write-Host "VSIX: $vsix" -ForegroundColor Green

Write-Step '静默安装 VSIX'
$proc = Start-Process -FilePath $vs.VSIXInstaller -ArgumentList @('/quiet',"`"$vsix`"") -PassThru -Wait -WindowStyle Hidden
$code = $proc.ExitCode
Write-Host "VSIXInstaller ExitCode = $code"

switch ($code) {
    0 { Write-Host '安装成功。' -ForegroundColor Green }
    1001 { Write-Warning '安装完成，但需要重启 Visual Studio。' }
    2004 { throw '安装失败：有 VS 相关进程仍在运行或被阻塞，请重试。' }
    8006 { throw '安装失败：权限不足，请使用“管理员 PowerShell”重试。' }
    default {
        $latestLog = Get-ChildItem -Path $env:TEMP -Filter 'dd_VSIXInstaller_*.log' -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1
        if ($latestLog) {
            throw "安装失败，ExitCode=$code。日志: $($latestLog.FullName)"
        }
        throw "安装失败，ExitCode=$code。"
    }
}

if ($RestartVS) {
    Write-Step '启动 VS2026'
    Start-Process -FilePath $vs.Devenv
}

Write-Step '完成'
Write-Host '一键更新完成。' -ForegroundColor Green
