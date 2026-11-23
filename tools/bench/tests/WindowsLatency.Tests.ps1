Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
. (Join-Path $root "tools\PackagingCommon.ps1")

function Assert-Condition([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "FAIL: $Message" }
}

$latency = Get-Content -LiteralPath (Join-Path $root "tools\bench\windows-latency.ps1") -Raw
$audio = Get-Content -LiteralPath (Join-Path $root "tools\bench\Quill.Bench\AudioChecks.cs") -Raw

Assert-Condition ($latency.Contains('$script:BenchFailures = 0')) "benchmark must track required child failures"
Assert-Condition ($latency.Contains('$exitCode = $LASTEXITCODE')) "benchmark must inspect child exit status"
Assert-Condition ($latency.Contains('measurement failed: $operation')) "benchmark must emit only a safe failure summary"
Assert-Condition ($latency.Contains('exit 1')) "benchmark must return failure when a required child fails"
Assert-Condition (-not $latency.Contains('($BenchArgs -join " ")')) "benchmark must not log raw child arguments"
Assert-Condition ($latency.Contains('if ($es.Count -eq 0)')) "benchmark must provide a non-Spanish voice fallback"
Assert-Condition ($audio.Contains('audio duration:')) "audio measurements must report only aggregate duration"
Assert-Condition (-not $audio.Contains('Truncate(text')) "audio measurements must not report recognized speech"
Assert-Condition (-not $audio.Contains('model: {Path.GetFileName')) "audio measurements must not report model paths"

$safe = @(Get-QuillSafeMeasurementLines -Lines @(
    "chunk 5s: median 120 ms, min 100, max 150, RTF 0.02",
    "native warning: C:\Users\private\session.wav",
    'recognized text: "secret transcript"',
    "speaker_17 0.00-1.00"
))
Assert-Condition ($safe.Count -eq 1 -and $safe[0].StartsWith("chunk 5s:")) "safe filter must keep only known measurement fields"

Write-Host "Windows latency tests passed."
