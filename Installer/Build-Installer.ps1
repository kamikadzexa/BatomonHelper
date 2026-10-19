[CmdletBinding()]
param([string]$CompilerPath)

$ErrorActionPreference = 'Stop'
$installerRoot = $PSScriptRoot
$repositoryRoot = Split-Path -Parent $installerRoot
$overlayProject = Join-Path $repositoryRoot 'src\BatomonHelper.Overlay\BatomonHelper.Overlay.csproj'
$stagingDirectory = Join-Path $installerRoot 'staging'
$outputDirectory = Join-Path $installerRoot 'output'

[xml]$buildProperties = Get-Content (Join-Path $repositoryRoot 'Directory.Build.props')
$appVersion = $buildProperties.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($appVersion)) { throw 'Directory.Build.props must define Version.' }

$isccCandidates = @(@(
    $CompilerPath,
    (Get-Command ISCC.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue),
    'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
) | Where-Object { $_ -and (Test-Path $_) })
if ($isccCandidates.Count -eq 0) { throw 'Inno Setup 6 is required. Install it, then run this script again.' }

if ([IO.Path]::GetFullPath($stagingDirectory) -ne [IO.Path]::GetFullPath("$installerRoot\staging")) { throw 'Invalid staging path.' }
if (Test-Path -LiteralPath $stagingDirectory) {
    if ((Get-Item -LiteralPath $stagingDirectory).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Staging must not be linked.' }
    Get-ChildItem -LiteralPath $stagingDirectory -Force | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force }
}
New-Item -ItemType Directory -Force -Path $stagingDirectory, $outputDirectory | Out-Null

dotnet publish $overlayProject --configuration Release --runtime win-x64 --self-contained true --output $stagingDirectory
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

# Preserve exact runtime license texts alongside the application's own license.
$assets = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src\BatomonHelper.Overlay\obj\project.assets.json') -Raw | ConvertFrom-Json
$packageRoots = $assets.packageFolders.PSObject.Properties.Name
$dependencies = Get-Content -LiteralPath (Join-Path $stagingDirectory 'BatomonHelper.Overlay.deps.json') -Raw | ConvertFrom-Json
$runtimeNotices = Join-Path $stagingDirectory 'runtime-notices'
New-Item -ItemType Directory -Force -Path $runtimeNotices | Out-Null
foreach ($runtimeName in @('microsoft.netcore.app.runtime.win-x64', 'microsoft.windowsdesktop.app.runtime.win-x64')) {
    $runtimeLibrary = $dependencies.libraries.PSObject.Properties.Name | Where-Object { $_.StartsWith("runtimepack.$runtimeName/", [StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1
    if ($runtimeLibrary) { $runtimeLibrary = $runtimeLibrary.Substring('runtimepack.'.Length) }
    if (-not $runtimeLibrary) { throw "Missing resolved runtime package: $runtimeName" }
    $runtimePackage = $packageRoots | ForEach-Object { Join-Path $_ $runtimeLibrary.ToLowerInvariant() } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $runtimePackage) { throw "Missing runtime package directory: $runtimeLibrary" }
    foreach ($notice in @('LICENSE', 'LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT')) {
        $noticePath = Join-Path $runtimePackage $notice
        if (Test-Path -LiteralPath $noticePath) { Copy-Item -LiteralPath $noticePath -Destination (Join-Path $runtimeNotices "$runtimeName-$notice") -Force }
    }
}
$installerNotices = Join-Path $stagingDirectory 'installer-notices'
New-Item -ItemType Directory -Force -Path $installerNotices | Out-Null
Copy-Item -LiteralPath (Join-Path (Split-Path $isccCandidates[0] -Parent) 'license.txt') -Destination (Join-Path $installerNotices 'Inno-Setup-license.txt') -Force

foreach ($required in @('BatomonHelper.Overlay.exe', 'coreclr.dll', 'PresentationFramework.dll', 'LICENSE.txt', 'THIRD-PARTY-NOTICES.md', 'assets\wiki\batomon-wiki.db', 'mods\BatomonHelperBridge\mod_main.gd')) {
    if (-not (Test-Path -LiteralPath (Join-Path $stagingDirectory $required))) { throw "Release payload is missing $required" }
}
if (Get-ChildItem -LiteralPath $stagingDirectory -Recurse -File | Where-Object { $_.Name -match '^BatomonHelper\.(Training|TrainingHost)\.(exe|dll)$|HelperInspector|^mod_(loader|settings_menu|title_state)\.gd$' }) { throw 'Unexpected release payload: Training/inspection tools and community Mod Loader scripts must not be bundled.' }

& $isccCandidates[0] '/Q' "/DAppVersion=$appVersion" (Join-Path $installerRoot 'BatomonHelper.iss')
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed.' }

$installerPath = Join-Path $outputDirectory "BatomonHelper-Setup-$appVersion.exe"
$sha256 = [System.Security.Cryptography.SHA256]::Create()
try {
    $stream = [System.IO.File]::OpenRead($installerPath)
    try { $hash = ([System.BitConverter]::ToString($sha256.ComputeHash($stream))).Replace('-', '').ToLowerInvariant() }
    finally { $stream.Dispose() }
}
finally { $sha256.Dispose() }
Set-Content -LiteralPath "$installerPath.sha256" -Value "$hash  $([IO.Path]::GetFileName($installerPath))" -Encoding ascii
Copy-Item -LiteralPath (Join-Path $repositoryRoot "docs\release-$appVersion.md") -Destination $outputDirectory -Force
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'docs\release-validation.md') -Destination $outputDirectory -Force
Write-Host "Installer and checksum created: $installerPath"
