#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$GameDirectory,
    [string]$LoaderDirectory,
    [switch]$Uninstall,
    [string]$HelperExecutable
)
$ErrorActionPreference = 'Stop'
if (-not $HelperExecutable) { $HelperExecutable = Join-Path (Split-Path $PSScriptRoot -Parent) 'BatomonHelper.Overlay.exe' }
if (-not (Test-Path -LiteralPath $HelperExecutable)) { throw 'Supply -HelperExecutable pointing to a published helper, or run this script from its installed tools folder.' }
$taskInfo = [Diagnostics.ProcessStartInfo]::new((Resolve-Path -LiteralPath $HelperExecutable).Path)
$taskInfo.UseShellExecute = $false
$taskInfo.ArgumentList.Add($(if ($Uninstall) { '--uninstall-bridge' } else { '--install-bridge' }))
$taskInfo.ArgumentList.Add((Resolve-Path -LiteralPath $GameDirectory).Path)
if ($LoaderDirectory -and -not $Uninstall) {
    $taskInfo.ArgumentList.Add('--loader')
    $taskInfo.ArgumentList.Add((Resolve-Path -LiteralPath $LoaderDirectory).Path)
}
$taskProcess = [Diagnostics.Process]::Start($taskInfo)
$taskProcess.WaitForExit()
if ($taskProcess.ExitCode -ne 0) { throw 'Mod setup failed. Use Game setup in the helper for diagnostics and permission elevation.' }
Write-Output 'Mod setup completed.'
