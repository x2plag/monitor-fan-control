$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectFile = Join-Path $projectRoot 'MonitorFanControl.csproj'
$releaseDir = Join-Path $projectRoot 'release'
$portablePublishDir = Join-Path $projectRoot 'bin\Release\portable-publish'
$smallPublishDir = Join-Path $projectRoot 'bin\Release\small-publish'
[xml]$projectXml = Get-Content -LiteralPath $projectFile -Raw
$version = [string]$projectXml.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($version)) { throw 'The project version is missing.' }
$smallReleaseDir = Join-Path $releaseDir 'small'
$portableExe = Join-Path $releaseDir 'MonitorFanControl.exe'
$smallExe = Join-Path $smallReleaseDir 'MonitorFanControl.exe'
$portableCompatibilityFile = Join-Path $releaseDir 'monitor-compatibility.json'
$portableZip = Join-Path $releaseDir "MonitorFanControl-$version-portable-win-x64.zip"
$smallZip = Join-Path $releaseDir "MonitorFanControl-$version-small-win-x64.zip"

New-Item -ItemType Directory -Force -Path $releaseDir, $smallReleaseDir | Out-Null

dotnet publish $projectFile -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -o $portablePublishDir
if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish failed.' }

dotnet publish $projectFile -c Release -r win-x64 -p:SelfContained=false `
    -p:PublishSingleFile=false -o $smallPublishDir
if ($LASTEXITCODE -ne 0) { throw 'Framework-dependent publish failed.' }

$publishedPortableExe = Join-Path $portablePublishDir 'MonitorFanControl.exe'
$publishedSmallExe = Join-Path $smallPublishDir 'MonitorFanControl.exe'
$publishedPortableCompatibilityFile = Join-Path $portablePublishDir 'monitor-compatibility.json'
# dotnet publish leaves older files in an existing output folder. Package only this app's current files.
$smallPackageFiles = @(
    $publishedSmallExe,
    (Join-Path $smallPublishDir 'MonitorFanControl.dll'),
    (Join-Path $smallPublishDir 'MonitorFanControl.deps.json'),
    (Join-Path $smallPublishDir 'MonitorFanControl.runtimeconfig.json'),
    (Join-Path $smallPublishDir 'monitor-compatibility.json')
)
if (-not (Test-Path -LiteralPath $publishedPortableExe)) { throw "Published executable not found: $publishedPortableExe" }
if (-not (Test-Path -LiteralPath $publishedPortableCompatibilityFile)) { throw "Compatibility file not found: $publishedPortableCompatibilityFile" }
foreach ($packageFile in $smallPackageFiles) {
    if (-not (Test-Path -LiteralPath $packageFile)) { throw "Published file not found: $packageFile" }
}

$runningPortableApp = Get-CimInstance Win32_Process -Filter "Name='MonitorFanControl.exe'" |
    Where-Object { $_.ExecutablePath -eq $portableExe }
if ($runningPortableApp) {
    Write-Warning "The portable app is still running from '$portableExe'; its file will not be replaced while in use. The new portable ZIP will still be built."
}
else {
    Copy-Item -LiteralPath $publishedPortableExe -Destination $portableExe -Force
}
Copy-Item -LiteralPath $publishedPortableCompatibilityFile -Destination $portableCompatibilityFile -Force
$runningSmallApp = Get-CimInstance Win32_Process -Filter "Name='MonitorFanControl.exe'" |
    Where-Object { $_.ExecutablePath -eq $smallExe }
if ($runningSmallApp) {
    Write-Warning "The app is still running from '$smallReleaseDir'; the new package will be built without replacing files in use. Exit from the tray before extracting the update to this folder."
}
else {
    Copy-Item -LiteralPath $smallPackageFiles -Destination $smallReleaseDir -Force
}

$portableSha = (Get-FileHash -Algorithm SHA256 -LiteralPath $publishedPortableExe).Hash.ToLowerInvariant()
$smallSha = (Get-FileHash -Algorithm SHA256 -LiteralPath $publishedSmallExe).Hash.ToLowerInvariant()
$portableShaFile = Join-Path $releaseDir 'SHA256SUMS-portable.txt'
$smallShaFile = Join-Path $releaseDir 'SHA256SUMS-small.txt'
$combinedShaFile = Join-Path $releaseDir 'SHA256SUMS.txt'
Set-Content -LiteralPath $portableShaFile -Value "$portableSha  MonitorFanControl.exe" -Encoding utf8
Set-Content -LiteralPath $smallShaFile -Value "$smallSha  MonitorFanControl.exe" -Encoding utf8

Compress-Archive -LiteralPath @($publishedPortableExe, $publishedPortableCompatibilityFile, (Join-Path $projectRoot 'README.md'),
    (Join-Path $projectRoot 'LICENSE.txt'), (Join-Path $projectRoot 'SAFETY.md'),
    (Join-Path $projectRoot 'SUPPORTED_MONITORS.md'),
    $portableShaFile) -DestinationPath $portableZip -Force

$smallArchiveFiles = $smallPackageFiles + @((Join-Path $projectRoot 'README.md'),
    (Join-Path $projectRoot 'LICENSE.txt'), (Join-Path $projectRoot 'SAFETY.md'),
    (Join-Path $projectRoot 'SUPPORTED_MONITORS.md'),
    $smallShaFile)
Compress-Archive -LiteralPath $smallArchiveFiles -DestinationPath $smallZip -Force

$archiveChecksums = @($portableZip, $smallZip) | ForEach-Object {
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $_).Hash.ToLowerInvariant()
    "$hash  $(Split-Path -Leaf $_)"
}
Set-Content -LiteralPath $combinedShaFile -Value $archiveChecksums -Encoding utf8

Get-Item -LiteralPath $portableExe, $smallExe, $portableZip, $smallZip,
    $portableShaFile, $smallShaFile, $combinedShaFile |
    Select-Object FullName, Length
