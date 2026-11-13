param([string]$Exe = 'dist/StreamDock.exe', [string]$Reports = 'artifacts')
$ErrorActionPreference = 'Stop'
$Exe = (Resolve-Path $Exe).Path
New-Item -ItemType Directory -Path $Reports -Force | Out-Null
$Reports = (Resolve-Path $Reports).Path
# Run from a path containing spaces and non-ASCII characters, with only the EXE present.
$temporaryRoot = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
$sandbox = Join-Path $temporaryRoot 'StreamDock portable тест'
New-Item -ItemType Directory -Path $sandbox -Force | Out-Null
$isolated = Join-Path $sandbox 'StreamDock.exe'
Copy-Item $Exe $isolated -Force
function Invoke-Check([string]$Mode, [string]$Report, [string]$Extra = '') {
    $path = Join-Path $Reports $Report
    $arguments = "$Mode --report `"$path`" $Extra"
    $process = Start-Process -FilePath $isolated -ArgumentList $arguments -WorkingDirectory $sandbox -PassThru
    if (!$process.WaitForExit(300000)) { Stop-Process -Id $process.Id -Force; throw "$Mode timed out" }
    if (!(Test-Path $path)) { throw "$Mode produced no report (exit $($process.ExitCode))" }
    $result = Get-Content $path -Raw | ConvertFrom-Json
    if ($process.ExitCode -ne 0 -or !$result.Success) { Get-Content $path; throw "$Mode failed" }
    Write-Host "$Mode passed"
}
Invoke-Check '--self-test' 'native-self-test.json'
$screenshot = Join-Path $Reports 'interface.png'
Invoke-Check '--ui-smoke' 'ui-smoke.json' "--screenshot `"$screenshot`""
Invoke-Check '--tray --tray-smoke' 'tray-smoke.json'
if (!(Test-Path $screenshot)) { throw 'UI screenshot is missing' }
