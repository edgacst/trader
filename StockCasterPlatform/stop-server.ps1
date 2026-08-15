$processes = Get-Process -Name "StockCasterPlatform" -ErrorAction SilentlyContinue
foreach ($process in $processes) {
    $processPath = $null
    try { $processPath = $process.Path } catch { }
    if ($processPath -and $processPath.StartsWith($PSScriptRoot, [StringComparison]::OrdinalIgnoreCase)) {
        Stop-Process -Id $process.Id
    }
}

Start-Sleep -Milliseconds 500

$mediaProcesses = Get-Process -Name "mediamtx" -ErrorAction SilentlyContinue
foreach ($process in $mediaProcesses) {
    $processPath = $null
    try { $processPath = $process.Path } catch { }
    if ($processPath -and $processPath.StartsWith($PSScriptRoot, [StringComparison]::OrdinalIgnoreCase)) {
        Stop-Process -Id $process.Id
    }
}

Write-Output "StockCaster Platform server has stopped."
