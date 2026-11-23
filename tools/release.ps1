<#
.SYNOPSIS
  Builds every Quill release asset for one version and prints sizes and SHA-256 hashes.

.DESCRIPTION
  1. tools\Publish-Portable.ps1 -> artifacts\quill-<Version>-win-x64.zip (app + Whisper large-v3-turbo)
  2. tools\build-ai-pack.ps1    -> artifacts\quill-ai-pack-<Version>*.zip (AI models)
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

$assets = @(
    Get-ChildItem -LiteralPath $artifacts -File -Filter "quill-$Version-win-x64.zip"
    Get-ChildItem -LiteralPath $artifacts -File -Filter "quill-ai-pack-$Version*.zip" |
        Where-Object { $_.Name -eq "quill-ai-pack-$Version.zip" -or $_.Name -match "^quill-ai-pack-$([regex]::Escape($Version))-[a-z0-9-]+\.zip$" }
)
if ($assets.Count -eq 0) {
    throw "No release assets for $Version in $artifacts."
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
