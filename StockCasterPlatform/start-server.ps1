$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$applicationRoot = Join-Path $projectRoot "StockCasterPlatform"
$projectFile = Join-Path $applicationRoot "StockCasterPlatform.csproj"
$executable = Join-Path $applicationRoot "bin\Debug\net10.0\StockCasterPlatform.exe"

$existing = Get-Process -Name "StockCasterPlatform" -ErrorAction SilentlyContinue |
    Where-Object {
        $processPath = $null
        try { $processPath = $_.Path } catch { }
        $processPath -and $processPath.StartsWith($projectRoot, [StringComparison]::OrdinalIgnoreCase)
    } |
    Select-Object -First 1

if ($null -eq $existing) {
    dotnet build $projectFile -c Debug
    if ($LASTEXITCODE -ne 0) { throw "StockCaster Platform build failed." }

    $existing = Start-Process -FilePath $executable -WorkingDirectory $applicationRoot -WindowStyle Hidden -PassThru
}

$ready = $false
for ($attempt = 0; $attempt -lt 20; $attempt++) {
    try {
        $health = Invoke-RestMethod -Uri "http://127.0.0.1:5075/health" -TimeoutSec 1
        if ($health.web -eq "ready" -and $health.mediaServer -eq "ready") {
            $ready = $true
            break
        }
    }
    catch { }
    Start-Sleep -Milliseconds 500
}

if (-not $ready) { throw "The broadcast server did not become ready in time." }

Start-Process "http://127.0.0.1:5075/studio.html"
Write-Output "StockCaster Platform server is ready. PID: $($existing.Id)"
