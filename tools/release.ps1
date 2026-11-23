<#
.SYNOPSIS
  Builds every Quill release asset for one version and prints sizes and SHA-256 hashes.

.DESCRIPTION
  1. tools\Publish-Portable.ps1 -> artifacts\quill-<Version>-win-x64.zip (app + Whisper large-v3-turbo)
  2. tools\build-ai-pack.ps1    -> artifacts\quill-ai-pack-<Version>*.zip (AI models):
       quill-ai-pack-<Version>.zip            ai-pack.json, notices, Whisper small, speakers, embeddings
       quill-ai-pack-<Version>-llm-part1.zip  first half of the language model (Q4_K_M)
       quill-ai-pack-<Version>-llm-part2.zip  second half of the language model
     Every asset that the generated ai-pack.json names must exist, or the release stops.
  Then writes artifacts\SHA256SUMS-<Version>.txt and prints a Markdown table to paste into
  the GitHub release notes. Every asset must be under 2 GiB.

.EXAMPLE
  .\tools\release.ps1 -Version 1.4.0
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Version,
    [switch]$SkipAppZip,
    [switch]$SkipAiPack,
    [switch]$IncludeVad,
    [switch]$AcceptNewHashes
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "PackagingCommon.ps1")
Assert-QuillVersion -Version $Version
if ($Version -eq "dev") {
    throw "A release needs a real version such as 1.4.0."
}

$artifacts = Join-Path (Split-Path -Parent $PSScriptRoot) "artifacts"

if (-not $SkipAppZip) {
    & (Join-Path $PSScriptRoot "Publish-Portable.ps1") -Version $Version -IncludeVad:$IncludeVad
}

if (-not $SkipAiPack) {
    & (Join-Path $PSScriptRoot "build-ai-pack.ps1") -Version $Version -AcceptNewHashes:$AcceptNewHashes
}

$assetVolumes = @("main")
$modelsManifestPath = Join-Path $PSScriptRoot "ai-pack.models.json"
if (Test-Path -LiteralPath $modelsManifestPath) {
    $modelsDefinition = Get-Content -LiteralPath $modelsManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($entry in @(Get-QuillProperty $modelsDefinition "components")) {
        $volume = [string](Get-QuillProperty $entry "volume")
        if ($volume) { $assetVolumes += $volume }
        $split = Get-QuillProperty $entry "split"
        $size = [long](Get-QuillProperty $entry "size")
        $maxPartBytes = if ($null -ne $split) { [long](Get-QuillProperty $split "maxPartBytes") } else { 0L }
        if ($maxPartBytes -gt 0 -and $size -gt 0) {
            $partSizes = Get-QuillPartSizes -TotalBytes $size -MaxPartBytes $maxPartBytes
            for ($index = 0; $index -lt $partSizes.Count; $index++) {
                $assetVolumes += "$volume-part$($index + 1)"
            }
        }
    }
}
$assetVolumes = @($assetVolumes | Sort-Object -Unique)
$assets = @(
    Get-ChildItem -LiteralPath $artifacts -File -Filter "quill-$Version-win-x64.zip"
    Get-ChildItem -LiteralPath $artifacts -File -Filter "quill-ai-pack-*.zip" |
        Where-Object { Test-QuillVersionedAssetName -Name $_.Name -Version $Version -Volumes $assetVolumes }
)
if ($assets.Count -eq 0) {
    throw "No release assets for $Version in $artifacts."
}

# A split model is useless if one of its part ZIPs is missing: check every asset that the
# generated ai-pack.json names (written by build-ai-pack.ps1 next to the ZIPs).
$packManifestPath = Join-Path $artifacts "ai-pack-work-$Version\ai-pack.json"
if (-not $SkipAiPack -or (Test-Path -LiteralPath $packManifestPath)) {
    if (-not (Test-Path -LiteralPath $packManifestPath)) {
        throw "Missing $packManifestPath; run build-ai-pack.ps1 -Version $Version first."
    }

    $packManifest = Get-Content -LiteralPath $packManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $expected = @("quill-ai-pack-$Version.zip")
    foreach ($component in @(Get-QuillProperty $packManifest "components")) {
        $asset = Get-QuillProperty $component "asset"
        if ($asset) { $expected += [string]$asset }
        foreach ($part in @(Get-QuillProperty $component "parts")) {
            if ($null -ne $part) { $expected += [string](Get-QuillProperty $part "asset") }
        }
    }

    $present = @($assets | ForEach-Object { $_.Name })
    $absent = @($expected | Sort-Object -Unique | Where-Object { $present -notcontains $_ })
    if ($absent.Count -gt 0) {
        throw "AI pack assets missing from ${artifacts}: $($absent -join ', ')."
    }
}

$sums = @()
$table = @("| Asset | Size | SHA-256 |", "| --- | --- | --- |")
foreach ($asset in $assets) {
    Assert-QuillAssetSize -Path $asset.FullName
    $hash = Get-QuillSha256 -Path $asset.FullName
    $sums += "$hash  $($asset.Name)"
    $table += "| ``$($asset.Name)`` | $(Format-QuillSize -Bytes $asset.Length) ($($asset.Length) bytes) | ``$hash`` |"
}

$sumsPath = Join-Path $artifacts "SHA256SUMS-$Version.txt"
[System.IO.File]::WriteAllLines($sumsPath, [string[]]$sums, (New-Object System.Text.UTF8Encoding($false)))

Write-Host ""
Write-Host "Release assets for $Version (paste into the release notes):"
Write-Host ""
$table | ForEach-Object { Write-Host $_ }
Write-Host ""
Write-Host "Checksums written to $sumsPath (upload it with the ZIPs)."
