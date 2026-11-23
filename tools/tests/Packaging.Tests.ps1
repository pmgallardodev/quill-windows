Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $root "tools\PackagingCommon.ps1")

function Assert-Condition([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "FAIL: $Message" }
}

try {
    $temp = Join-Path ([System.IO.Path]::GetTempPath()) ("quill-packaging-tests-" + [guid]::NewGuid())
    New-Item -ItemType Directory -Path $temp -Force | Out-Null
    $fixture = Join-Path $temp "fixture.bin"
    [System.IO.File]::WriteAllBytes($fixture, [byte[]](1, 2, 3, 5, 8))
    $fixtureHash = Get-QuillSha256 -Path $fixture
    $script:QuillPinnedWhisperSha256["fixture.bin"] = $fixtureHash

    Assert-Condition (Test-QuillPinnedWhisperModel -Name "fixture.bin" -Path $fixture) "pinned fixture should verify"
    [System.IO.File]::WriteAllBytes($fixture, [byte[]](1, 2, 3, 5, 9))
    Assert-Condition (-not (Test-QuillPinnedWhisperModel -Name "fixture.bin" -Path $fixture)) "changed fixture should fail its pinned digest"

    $volumes = @("main", "llm", "llm-part1")
    Assert-Condition (Test-QuillVersionedAssetName -Name "quill-ai-pack-1.4.0.zip" -Version "1.4.0" -Volumes $volumes) "main stable asset should match"
    Assert-Condition (Test-QuillVersionedAssetName -Name "quill-ai-pack-1.4.0-llm-part1.zip" -Version "1.4.0" -Volumes $volumes) "known stable volume should match"
    Assert-Condition (-not (Test-QuillVersionedAssetName -Name "quill-ai-pack-1.4.0-beta.1.zip" -Version "1.4.0" -Volumes $volumes)) "stable cleanup must not match prerelease"
    Assert-Condition (Test-QuillVersionedAssetName -Name "quill-ai-pack-1.4.0-beta.1.zip" -Version "1.4.0-beta.1" -Volumes $volumes) "selected prerelease must match itself"

    $build = Get-Content -LiteralPath (Join-Path $root "tools\build-ai-pack.ps1") -Raw
    $release = Get-Content -LiteralPath (Join-Path $root "tools\release.ps1") -Raw
    Assert-Condition ($build.Contains("Test-QuillVersionedAssetName")) "AI pack cleanup must use exact asset matching"
    Assert-Condition ($release.Contains("Test-QuillVersionedAssetName")) "release inventory must use exact asset matching"
    Write-Host "Packaging tests passed."
}
finally {
    if ($temp -and (Test-Path -LiteralPath $temp)) {
        Remove-Item -LiteralPath $temp -Recurse -Force
    }
}
