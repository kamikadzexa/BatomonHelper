[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$installerRoot = $PSScriptRoot
$repositoryRoot = Split-Path -Parent $installerRoot
$overlayProject = Join-Path $repositoryRoot 'src\BatomonHelper.Overlay\BatomonHelper.Overlay.csproj'
$stagingDirectory = Join-Path $installerRoot 'staging'
$outputDirectory = Join-Path $installerRoot 'output'
$dependenciesDirectory = Join-Path $installerRoot 'dependencies'
$runtimeVersion = '10.0.10'
$runtimeFileName = "windowsdesktop-runtime-$runtimeVersion-win-x64.exe"
$runtimePath = Join-Path $dependenciesDirectory $runtimeFileName
$runtimeUri = "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/$runtimeVersion/$runtimeFileName"

[xml]$buildProperties = Get-Content (Join-Path $repositoryRoot 'Directory.Build.props')
$appVersion = $buildProperties.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($appVersion)) { throw 'Directory.Build.props must define Version.' }

$isccCandidates = @(
    (Get-Command ISCC.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue),
    'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
) | Where-Object { $_ -and (Test-Path $_) }
if ($isccCandidates.Count -eq 0) { throw 'Inno Setup 6 is required. Install it, then run this script again.' }

New-Item -ItemType Directory -Force -Path $stagingDirectory, $outputDirectory, $dependenciesDirectory | Out-Null
Remove-Item -Recurse -Force -Path (Join-Path $stagingDirectory '*') -ErrorAction SilentlyContinue

if (-not (Test-Path $runtimePath)) {
    Write-Host "Downloading .NET Windows Desktop Runtime $runtimeVersion..."
    Invoke-WebRequest -Uri $runtimeUri -OutFile $runtimePath
}

dotnet publish $overlayProject --configuration Release --runtime win-x64 --self-contained false --output $stagingDirectory
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

& $isccCandidates[0] "/DAppVersion=$appVersion" (Join-Path $installerRoot 'BatomonHelper.iss')
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed.' }

Write-Host "Installer created: $(Join-Path $outputDirectory "BatomonHelper-Setup-$appVersion.exe")"
