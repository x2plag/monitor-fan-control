param([Parameter(Mandatory = $true)][string]$PackageDirectory)
$ErrorActionPreference = 'Stop'
$packagePath = [System.IO.Path]::GetFullPath($PackageDirectory)
$exePath = Join-Path $packagePath 'MonitorFanControl.exe'
$admissionPath = Join-Path $packagePath 'osd-admission.json'
$lockPath = Join-Path $packagePath '.launcher.lock'
$launcherLock = $null
$admissionCreated = $false
try {
    if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
        throw 'Extract the entire ZIP into a writable folder before starting.'
    }
    try {
        $launcherLock = [System.IO.File]::Open($lockPath, 'OpenOrCreate', 'ReadWrite', 'None')
    } catch {
        throw 'Another launcher is running, or this folder is not writable.'
    }
    Write-Host 'LG 39GX950B Quiet - experimental / Windows x64'
    Write-Host 'Check the monitor: its service menu must be closed before starting.'
    $answer = Read-Host 'Is the service menu closed? Type YES to confirm, or Enter to cancel'
    if ($answer.Trim() -cne 'YES') {
        Write-Host 'Cancelled. The application was not started.'
        exit 0
    }
    $observation = [ordered]@{
        Kind = 'explicit-human-observation'
        PhysicalOsdClosed = $true
        ObservedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        Evidence = 'Operator explicitly confirmed the service menu is closed before starting this session.'
    }
    [System.IO.File]::WriteAllText($admissionPath, ($observation | ConvertTo-Json), [System.Text.UTF8Encoding]::new($false))
    $admissionCreated = $true
    $application = Start-Process -FilePath $exePath -ArgumentList '--lg39-apply-exit' -WorkingDirectory $packagePath -Wait -PassThru
    if ($application.ExitCode -ne 0) {
        throw ('The application exited with code {0}. See %LOCALAPPDATA%\MonitorFanControl.Quiet\logs.' -f $application.ExitCode)
    }
} catch {
    Write-Host ('Error: ' + $_.Exception.Message) -ForegroundColor Red
    exit 1
} finally {
    if ($admissionCreated -and (Test-Path -LiteralPath $admissionPath)) {
        Remove-Item -LiteralPath $admissionPath -Force
    }
    if ($null -ne $launcherLock) {
        $launcherLock.Dispose()
        Remove-Item -LiteralPath $lockPath -Force -ErrorAction SilentlyContinue
    }
}
