$vsRoot = Join-Path $env:LOCALAPPDATA "Microsoft\VisualStudio"
$inst = Get-ChildItem $vsRoot -Directory |
  Where-Object { $_.Name -like '18.0_*Exp' } |
  Sort-Object LastWriteTime -Descending |
  Select-Object -First 1

if (-not $inst) {
  $inst = Get-ChildItem $vsRoot -Directory |
    Where-Object { $_.Name -like '18.0_*' -and $_.Name -notlike '*Exp*' } |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
}
if (-not $inst) { Write-Host "未找到 VS2026 实例目录"; exit 1 }

$vsInstance = $inst.Name

$logPath = Join-Path $env:APPDATA "Microsoft\VisualStudio\$VsInstance\ActivityLog.xml"
if (-not (Test-Path $logPath)) {
    Write-Host "ActivityLog 不存在: $logPath"
    exit 1
}

$ts = Get-Date -Format 'yyyyMMdd_HHmmss'
$outFile = Join-Path $env:TEMP "VS2026_AzureOpenAI_Log_$ts.txt"

[xml]$xml = Get-Content $logPath
$entries = $xml.SelectNodes('//entry')

$patterns = @(
    'AzureOpenAI',
    'ChatToolWindow',
    'ShowToolWindow',
    'XamlParseException',
    'Menus.ctmenu',
    'HrLoadNativeUILibrary',
    'Construction of frame content failed'
)

$hits = foreach ($e in $entries) {
    $text = ($e.description + ' ' + $e.errorinfo)
    if ($patterns | Where-Object { $text -match $_ }) {
        [PSCustomObject]@{
            Record      = [int]$e.record
            Time        = $e.time
            Type        = $e.type
            Source      = $e.source
            Description = ($e.description -replace "`r?`n", ' ')
            ErrorInfo   = ($e.errorinfo -replace "`r?`n", ' ')
        }
    }
}

"=== ActivityLog: $logPath ===" | Out-File -FilePath $outFile -Encoding utf8
"=== Filter: $($patterns -join ', ') ===" | Out-File -FilePath $outFile -Append -Encoding utf8
"=== Last 200 matching entries ===" | Out-File -FilePath $outFile -Append -Encoding utf8
$hits | Sort-Object Record | Select-Object -Last 200 | Format-List | Out-File -FilePath $outFile -Append -Encoding utf8

Write-Host "已输出: $outFile"