$ErrorActionPreference = 'Stop'

$version = '1.20.0'
$expectedHash = '7364e7672e6b4420e986ec4b56e2cc32ec7b4085f69b56ec224d596d0fa8b19f'
$archiveName = "mediamtx_v${version}_windows_amd64.zip"
$downloadUrl = "https://github.com/bluenviron/mediamtx/releases/download/v${version}/$archiveName"
$mediaDirectory = Join-Path $PSScriptRoot 'StockCasterPlatform\MediaServer'
$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("stockcaster-mediamtx-" + [Guid]::NewGuid().ToString('N'))
$archivePath = Join-Path $temporaryRoot $archiveName
$extractPath = Join-Path $temporaryRoot 'extracted'

try {
    New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $extractPath -Force | Out-Null
    New-Item -ItemType Directory -Path $mediaDirectory -Force | Out-Null

    Write-Host "MediaMTX v$version 다운로드 중..."
    Invoke-WebRequest -Uri $downloadUrl -OutFile $archivePath

    $actualHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $expectedHash) {
        throw "MediaMTX 파일 검증에 실패했습니다. 예상: $expectedHash / 실제: $actualHash"
    }

    Expand-Archive -LiteralPath $archivePath -DestinationPath $extractPath -Force
    $executablePath = Join-Path $extractPath 'mediamtx.exe'
    if (-not (Test-Path -LiteralPath $executablePath)) {
        throw '압축 파일에서 mediamtx.exe를 찾을 수 없습니다.'
    }

    Copy-Item -LiteralPath $executablePath -Destination (Join-Path $mediaDirectory 'mediamtx.exe') -Force
    Write-Host 'MediaMTX 설치가 완료되었습니다.' -ForegroundColor Green
}
finally {
    $resolvedTemp = [System.IO.Path]::GetFullPath($temporaryRoot)
    $systemTemp = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    if ($resolvedTemp.StartsWith($systemTemp, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedTemp)) {
        Remove-Item -LiteralPath $resolvedTemp -Recurse -Force
    }
}
