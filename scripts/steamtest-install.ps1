# Installs the staged workshop content into mods_STEAMTEST and shelves the local dev mod,
# so the game exercises the ModSource.SteamWorkshop path. Run steamtest-restore.ps1 to undo.
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$sts2 = $null
if (Test-Path "$repo\local.props") {
    $m = Select-String -Path "$repo\local.props" -Pattern "<Sts2Path>(.*)</Sts2Path>"
    if ($m) { $sts2 = $m.Matches[0].Groups[1].Value }
}
if (-not $sts2) { $sts2 = "C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2" }

$staged = Join-Path $sts2 "mod_upload\SpireBot\content\SpireBot"
if (-not (Test-Path $staged)) { throw "run scripts\stage-workshop.ps1 first ($staged missing)" }

$dev = Join-Path $sts2 "mods\SpireBot"
$shelf = Join-Path $sts2 "mods\SpireBot._localdev"
if (Test-Path $shelf) { throw "$shelf already exists - previous rehearsal not restored?" }
if (Test-Path $dev) { Rename-Item $dev $shelf }

$test = Join-Path $sts2 "mods_STEAMTEST\SpireBot"
if (Test-Path $test) { Remove-Item $test -Recurse -Force -Confirm:$false }
New-Item -ItemType Directory -Force $test | Out-Null
Copy-Item "$staged\*" $test -Recurse
Write-Host "Installed to $test; local dev mod shelved at $shelf. Launch the game now."
