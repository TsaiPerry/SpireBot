# Publishes the staged workshop item via steamcmd. First run (publishedfileid 0) creates the
# item as PRIVATE and records the assigned id in scripts\publishedfileid.txt; later runs update it.
# steamcmd login is interactive on first use (Steam Guard) - run from a real terminal.
# Usage: powershell -ExecutionPolicy Bypass -File scripts\publish-workshop.ps1 -Login <steam_user> [-Visibility 2] [-ChangeNote "..."]
param(
    [Parameter(Mandatory=$true)][string]$Login,
    [ValidateSet(0,1,2)][int]$Visibility = 2,
    [string]$ChangeNote = "",
    [string]$SteamCmd = "C:\steamcmd\steamcmd.exe"
)
$ErrorActionPreference = "Stop"
if ($ChangeNote.Contains('"')) { throw "ChangeNote must not contain double quotes (VDF quoting)" }
$repo = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path $SteamCmd)) { throw "steamcmd not found at $SteamCmd (install from https://developer.valvesoftware.com/wiki/SteamCMD or pass -SteamCmd)" }

$sts2 = $null
if (Test-Path "$repo\local.props") {
    $m = Select-String -Path "$repo\local.props" -Pattern "<Sts2Path>(.*)</Sts2Path>"
    if ($m) { $sts2 = $m.Matches[0].Groups[1].Value }
}
if (-not $sts2) { $sts2 = "C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2" }

$content = Join-Path $sts2 "mod_upload\SpireBot\content"
if (-not (Test-Path (Join-Path $content "SpireBot\SpireBot.dll"))) { throw "run scripts\stage-workshop.ps1 first" }
$preview = "$repo\dist\preview.png"
if (-not (Test-Path $preview)) { throw "missing $preview (workshop preview image)" }

$fileId = (Get-Content "$repo\scripts\publishedfileid.txt" -TotalCount 1).Trim()
$desc = "SpireBot plays Slay the Spire 2 on its own, using a neural-network policy trained with reinforcement learning (PPO). The trained model ships inside the mod - subscribe, enable mods, and start a run with the bot enabled. No configuration needed." `
      + "\n\nRequires BaseLib (listed as a required item)." `
      + "\nWindows only." `
      + "\nBuilt for the game's main branch (v0.107.1); the beta branch is not supported."

$vdfPath = Join-Path $sts2 "mod_upload\SpireBot\workshop_item.vdf"
@"
"workshopitem"
{
    "appid"           "2868840"
    "publishedfileid" "$fileId"
    "contentfolder"   "$content"
    "previewfile"     "$preview"
    "visibility"      "$Visibility"
    "title"           "SpireBot"
    "description"     "$desc"
    "changenote"      "$ChangeNote"
}
"@ | Out-File $vdfPath -Encoding ASCII
Write-Host "VDF written to $vdfPath (publishedfileid=$fileId, visibility=$Visibility)"

& $SteamCmd +login $Login +workshop_build_item $vdfPath +quit
if ($LASTEXITCODE -ne 0) { throw "steamcmd exited $LASTEXITCODE" }

# steamcmd writes the assigned id back into the VDF on a successful create - persist it.
$newId = (Select-String -Path $vdfPath -Pattern '"publishedfileid"\s+"(\d+)"').Matches[0].Groups[1].Value
if ($newId -and $newId -ne "0") {
    Set-Content "$repo\scripts\publishedfileid.txt" $newId
    Write-Host "Published. Item id $newId -> https://steamcommunity.com/sharedfiles/filedetails/?id=$newId"
} else {
    Write-Warning "Could not read back a publishedfileid - check steamcmd output above."
}
