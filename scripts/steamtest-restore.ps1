# Undoes steamtest-install.ps1: removes the STEAMTEST copy and restores the local dev mod.
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$sts2 = $null
if (Test-Path "$repo\local.props") {
    $m = Select-String -Path "$repo\local.props" -Pattern "<Sts2Path>(.*)</Sts2Path>"
    if ($m) { $sts2 = $m.Matches[0].Groups[1].Value }
}
if (-not $sts2) { $sts2 = "C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2" }

$test = Join-Path $sts2 "mods_STEAMTEST\SpireBot"
if (Test-Path $test) { Remove-Item $test -Recurse -Force -Confirm:$false }
$dev = Join-Path $sts2 "mods\SpireBot"
$shelf = Join-Path (Split-Path $sts2 -Parent) "SpireBot._localdev"
if ((Test-Path $shelf) -and -not (Test-Path $dev)) { Move-Item $shelf $dev }
Write-Host "Restored. STEAMTEST copy removed; dev mod back at $dev."
