[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$GameDirectory,
    [string]$LoaderDirectory
)
$ErrorActionPreference = 'Stop'
$taskGame = (Resolve-Path -LiteralPath $GameDirectory).Path
if (-not (Test-Path -LiteralPath (Join-Path $taskGame 'batomon_showdown.exe'))) {
    throw 'Choose the directory containing batomon_showdown.exe.'
}
if (Get-Process -Name batomon_showdown -ErrorAction SilentlyContinue) {
    throw 'Close Batomon Showdown before installing the bridge.'
}
$taskMods = Join-Path $taskGame 'Mods'
$taskLoader = Join-Path $taskMods 'mod_loader.gd'
if (-not (Test-Path -LiteralPath $taskLoader)) {
    if (-not $LoaderDirectory) { throw 'Install the community Mod Loader 1.2.2 first, or supply -LoaderDirectory pointing to its extracted download.' }
    $taskLoaderSource = Join-Path (Resolve-Path -LiteralPath $LoaderDirectory).Path 'Mods'
    foreach ($taskName in @('mod_loader.gd', 'mod_settings_menu.gd', 'mod_title_state.gd')) {
        if (-not (Test-Path -LiteralPath (Join-Path $taskLoaderSource $taskName))) { throw "Loader download is missing $taskName." }
    }
    New-Item -ItemType Directory -Path $taskMods -Force | Out-Null
    foreach ($taskName in @('mod_loader.gd', 'mod_settings_menu.gd', 'mod_title_state.gd')) {
        Copy-Item -LiteralPath (Join-Path $taskLoaderSource $taskName) -Destination $taskMods
    }
}
$taskOverride = Join-Path $taskGame 'override.cfg'
$taskConfig = if (Test-Path -LiteralPath $taskOverride) { Get-Content -LiteralPath $taskOverride -Raw } else { '' }
$taskAutoload = 'ModLoader="*res://Mods/mod_loader.gd"'
if ($taskConfig -match '(?m)^ModLoader\s*=') {
    if ($taskConfig -notmatch '(?m)^ModLoader\s*=\s*"\*res://Mods/mod_loader\.gd"\s*$') {
        throw 'override.cfg already declares a different ModLoader. Keep its configuration and install the bridge folder manually.'
    }
} else {
    if (Test-Path -LiteralPath $taskOverride) {
        $taskBackup = "$taskOverride.batomon-helper.bak"
        if (-not (Test-Path -LiteralPath $taskBackup)) { Copy-Item -LiteralPath $taskOverride -Destination $taskBackup }
    }
    if ($taskConfig -match '(?m)^\[autoload\]\s*$') {
        $taskConfig = [regex]::Replace($taskConfig, '(?m)^\[autoload\]\s*$', "[autoload]`r`n$taskAutoload")
    } else { $taskConfig += "`r`n[autoload]`r`n$taskAutoload`r`n" }
    Set-Content -LiteralPath $taskOverride -Value $taskConfig -Encoding utf8
}
$taskBridgeSource = Join-Path (Split-Path $PSScriptRoot -Parent) 'mods/BatomonHelperBridge'
$taskBridgeTarget = Join-Path $taskMods 'BatomonHelperBridge'
New-Item -ItemType Directory -Path $taskBridgeTarget -Force | Out-Null
foreach ($taskName in @('mod_main.gd', 'mod.json')) {
    Copy-Item -LiteralPath (Join-Path $taskBridgeSource $taskName) -Destination $taskBridgeTarget -Force
}
Write-Output "Installed BatomonHelper Bridge to $taskBridgeTarget. Start the game and helper."
