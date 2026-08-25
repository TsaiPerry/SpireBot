# Builds SpireBot and stages the Steam Workshop item content, then verifies the staged tree.
# Usage: powershell -ExecutionPolicy Bypass -File scripts\stage-workshop.ps1
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot

dotnet build "$repo\SpireBot.csproj" -p:UploadBuild=true
if ($LASTEXITCODE -ne 0) { throw "build failed" }

# Resolve Sts2Path the same way the build did: local.props first, else ask MSBuild is overkill -
# read local.props, fall back to the default Steam location.
$sts2 = $null
$localProps = "$repo\local.props"
if (Test-Path $localProps) {
    $m = Select-String -Path $localProps -Pattern "<Sts2Path>(.*)</Sts2Path>"
    if ($m) { $sts2 = $m.Matches[0].Groups[1].Value }
}
if (-not $sts2) { $sts2 = "C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2" }
$content = Join-Path $sts2 "mod_upload\SpireBot\content\SpireBot"
if (-not (Test-Path $content)) { throw "staged content not found at $content" }

$expected = @(
    "SpireBot.json", "SpireBot.dll",
    "Microsoft.ML.OnnxRuntime.dll", "onnxruntime.dll", "onnxruntime_providers_shared.dll",
    "model\model.onnx", "model\contract.json", "model\stub.onnx",
    "licenses\LICENSE-onnxruntime.txt", "licenses\LICENSE-RunReplays.txt"
)
$actual = Get-ChildItem $content -Recurse -File | ForEach-Object { $_.FullName.Substring($content.Length + 1) }
$missing = $expected | Where-Object { $actual -notcontains $_ }
$extra   = $actual   | Where-Object { $expected -notcontains $_ }
if ($missing) { throw "missing from staged item: $($missing -join ', ')" }
if ($extra)   { throw "unexpected files in staged item (pdb/bak leak?): $($extra -join ', ')" }

Write-Host "Staged OK: $content"
Get-ChildItem $content -Recurse -File | ForEach-Object {
    "{0,12:N0}  {1}  {2}" -f $_.Length, (Get-FileHash $_.FullName).Hash.Substring(0,12), $_.FullName.Substring($content.Length + 1)
}
$total = (Get-ChildItem $content -Recurse -File | Measure-Object Length -Sum).Sum
Write-Host ("Total item size: {0:N1} MB" -f ($total / 1MB))
