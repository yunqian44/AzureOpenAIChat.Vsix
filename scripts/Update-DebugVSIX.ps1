[CmdletBinding()]
param(
    [string]$RepoRoot = "",
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
            $candidate = Join-Path ($installPath.Trim()) 'Common7\IDE\devenv.exe'
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

function Get-LatestVSIXInstallerLog {
    return Get-ChildItem -Path $env:TEMP -Filter 'dd_VSIXInstaller_*.log' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
}

function Get-BlockingProcessesFromLog([string]$LogPath) {
    if (-not (Test-Path $LogPath)) {
        return @()
    }

    $lines = Get-Content -LiteralPath $LogPath -ErrorAction SilentlyContinue
    if (-not $lines) {
        return @()
    }

    $result = New-Object System.Collections.Generic.List[object]
    foreach ($line in $lines) {
        if ($line -match '(?<name>[A-Za-z0-9._-]+\.exe)\s*\(ID\s*(?<pid>\d+)\)') {
            $result.Add([pscustomobject]@{ Name = $Matches['name']; Pid = [int]$Matches['pid'] })
            continue
        }

        if ($line -match '(?<name>[A-Za-z0-9._-]+\.exe)\s*\((?<pid>\d+)\)\s*:') {
            $result.Add([pscustomobject]@{ Name = $Matches['name']; Pid = [int]$Matches['pid'] })
        }
    }

    return $result | Sort-Object Pid -Unique
}

function Stop-ProcessesByPid([object[]]$Processes) {
    if (-not $Processes -or $Processes.Count -eq 0) {
        return
    }

    foreach ($p in $Processes) {
        try {
            $alive = Get-Process -Id $p.Pid -ErrorAction SilentlyContinue
            if (-not $alive) {
                continue
            }

            Write-Host "关闭阻塞进程: $($p.Name) (PID=$($p.Pid))"
            Stop-Process -Id $p.Pid -Force -ErrorAction Stop
        }
        catch {
            Write-Warning "关闭阻塞进程失败: $($p.Name) (PID=$($p.Pid))，$($_.Exception.Message)"
        }
    }
}

function Stop-VSProcesses {
    $names = @(
        'devenv',
        'ServiceHub.Host.Extensibility.x64',
        'ServiceHub.Host.CLR.x64',
        'ServiceHub.IdentityHost',
        'ServiceHub.SettingsHost',
        'ServiceHub.VSDetouredHost',
        'Microsoft.ServiceHub.Controller',
        'DevHub',
        'MSBuild',
        'VBCSCompiler'
    )

    foreach ($name in $names) {
        $procs = Get-Process -Name $name -ErrorAction SilentlyContinue
        if (-not $procs) {
            continue
        }

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

    Start-Sleep -Seconds 2
}

function Install-Vsix([string]$InstallerPath, [string]$VsixPath) {
    $proc = Start-Process -FilePath $InstallerPath -ArgumentList @('/quiet',"`"$VsixPath`"") -PassThru -Wait -WindowStyle Hidden
    return $proc.ExitCode
}

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
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
$env:MSBUILDDISABLENODEREUSE = '1'
& dotnet build $solution -c $Configuration -nodeReuse:false
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build 失败，退出码: $LASTEXITCODE"
}

Write-Step '清理构建后台进程'
& dotnet build-server shutdown | Out-Null
Stop-VSProcesses

if (-not (Test-Path $vsix)) {
    throw "编译后未找到 VSIX 包: $vsix"
}
Write-Host "VSIX: $vsix" -ForegroundColor Green

Write-Step '静默安装 VSIX'
$code = Install-Vsix -InstallerPath $vs.VSIXInstaller -VsixPath $vsix
Write-Host "VSIXInstaller ExitCode = $code"

if ($code -eq 2004) {
    $latestLog = Get-LatestVSIXInstallerLog
    if ($latestLog) {
        Write-Warning "首次安装仍被阻塞，尝试从日志识别并关闭阻塞进程: $($latestLog.FullName)"
        $blockers = Get-BlockingProcessesFromLog -LogPath $latestLog.FullName
        Stop-ProcessesByPid -Processes $blockers
    }

    Start-Sleep -Seconds 2
    Write-Step '重试静默安装 VSIX（第 2 次）'
    $code = Install-Vsix -InstallerPath $vs.VSIXInstaller -VsixPath $vsix
    Write-Host "VSIXInstaller ExitCode = $code"
}

switch ($code) {
    0 { Write-Host '安装成功。' -ForegroundColor Green }
    1001 { Write-Warning '安装完成，但需要重启 Visual Studio。' }
    2004 {
        $latestLog = Get-LatestVSIXInstallerLog
        if ($latestLog) {
            $blockers = Get-BlockingProcessesFromLog -LogPath $latestLog.FullName
            if ($blockers -and $blockers.Count -gt 0) {
                $blockedText = ($blockers | ForEach-Object { "{0}(PID={1})" -f $_.Name, $_.Pid }) -join ', '
                throw "安装失败：有 VS 相关进程仍在运行或被阻塞。阻塞进程: $blockedText。日志: $($latestLog.FullName)"
            }

            throw "安装失败：有 VS 相关进程仍在运行或被阻塞，请重试。日志: $($latestLog.FullName)"
        }

        throw '安装失败：有 VS 相关进程仍在运行或被阻塞，请重试。'
    }
    8006 { throw '安装失败：权限不足，请使用“管理员 PowerShell”重试。' }
    default {
        $latestLog = Get-LatestVSIXInstallerLog
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
