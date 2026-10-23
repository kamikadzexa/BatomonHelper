[CmdletBinding()]
param([Parameter(Mandatory)][string]$GodotPath)

$ErrorActionPreference = 'Stop'
$engine = (Resolve-Path -LiteralPath $GodotPath).Path
$root = Split-Path -Parent $PSScriptRoot
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$project = Join-Path $tempRoot ('BatomonHelper-bridge-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $project | Out-Null

function Invoke-Godot([string[]]$Arguments) {
    $info = [Diagnostics.ProcessStartInfo]::new($engine)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($info)
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(60000)) { $process.Kill($true); throw 'Godot bridge validation timed out.' }
    $output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
    if ($process.ExitCode -ne 0 -or $output -match 'SCRIPT ERROR:|Parse Error:|ERROR:') { throw "Godot bridge validation failed:`n$output" }
    return $output
}

try {
    '[application]' | Set-Content -LiteralPath (Join-Path $project 'project.godot')
    Copy-Item -LiteralPath (Join-Path $root 'mods/BatomonHelperBridge/mod_main.gd') -Destination (Join-Path $project 'bridge.gd')
    Copy-Item -Path (Join-Path $root 'tests/godot/*.gd') -Destination $project
    Invoke-Godot @('--headless', '--editor', '--path', $project, '--quit') | Out-Null
    Invoke-Godot @('--headless', '--path', $project, '--check-only', '--script', 'res://bridge.gd') | Out-Null
    $result = Invoke-Godot @('--headless', '--path', $project, '--script', 'res://bridge_smoke.gd')
    if ($result -notmatch 'BRIDGE_SMOKE_OK') { throw 'Godot did not confirm the bridge smoke test.' }
    Write-Output 'Godot bridge compilation and battle-frame smoke test passed.'
}
finally {
    $resolved = [IO.Path]::GetFullPath($project)
    if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolved) -notlike 'BatomonHelper-bridge-test-*') { throw 'Invalid bridge test cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
