param([string]$Output = "dist")
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    python scripts/prepare_payload.py
    if ($LASTEXITCODE -ne 0) { throw 'Payload preparation failed' }
    dotnet run --project tests/StreamDock.Tests -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Core tests failed' }
    dotnet publish src/StreamDock.App -c Release -r win-x64 --self-contained true -o $Output -p:ContinuousIntegrationBuild=true
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
    $exe = Join-Path $Output 'StreamDock.exe'
    if (!(Test-Path $exe)) { throw 'Single executable was not produced' }
    $others = @(Get-ChildItem $Output -File | Where-Object { $_.Name -ne 'StreamDock.exe' })
    if ($others.Count -gt 0) { throw "Unexpected external files: $($others.Name -join ', ')" }
    $hash = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  StreamDock.exe" | Set-Content (Join-Path $Output 'SHA256SUMS.txt') -Encoding utf8NoBOM
} finally { Pop-Location }
