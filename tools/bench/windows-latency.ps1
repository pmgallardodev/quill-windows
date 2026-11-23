<#
.SYNOPSIS
  Measures Quill Next feasibility numbers on the real Windows machine (task F0).

.DESCRIPTION
  1. Whisper.net live-chunk latency (ggml-small and ggml-small-q5_1, CPU and Vulkan, 5 s / 10 s chunks)
     plus Silero VAD timing.
  2. sherpa-onnx diarization throughput (pyannote segmentation 3.0 + CAM++ embedding).
  3. Optional (-Llm): LLM summary latency with LLamaSharp on the corpus (1:1 = live-sized prompt,
     60-min messy meeting = final summary target), then scores the output.

  Models are downloaded to %LOCALAPPDATA%\quill-bench (outside the repo). Everything is printed and
  also written to tools\bench\results\windows-<timestamp>.txt. Send that file back.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\bench\windows-latency.ps1
  powershell -ExecutionPolicy Bypass -File tools\bench\windows-latency.ps1 -Llm -LlmModel qwen3-4b-instruct-2507
  powershell -ExecutionPolicy Bypass -File tools\bench\windows-latency.ps1 -Audio C:\path\to\session\mic.wav
#>
[CmdletBinding()]
param(
    [string]$Audio = "",
    [int]$Threads = 0,
    [switch]$Llm,
    [ValidateSet("qwen3-4b-instruct-2507", "gemma-3-4b-it", "phi-4-mini-instruct")]
    [string]$LlmModel = "qwen3-4b-instruct-2507",
    [string[]]$LlmBackends = @("vulkan", "cpu"),
    [string]$LlmMeetings = "m1,m5"
)

$ErrorActionPreference = "Stop"
$BenchDir = $PSScriptRoot
. (Join-Path $BenchDir "..\PackagingCommon.ps1")
$Cache = Join-Path $env:LOCALAPPDATA "quill-bench"
$Results = Join-Path $BenchDir "results"
$script:BenchFailures = 0
New-Item -ItemType Directory -Force -Path $Cache, $Results, "$Cache\whisper", "$Cache\sherpa", "$Cache\llm", "$Cache\audio" | Out-Null
$Log = Join-Path $Results ("windows-" + (Get-Date -Format "yyyyMMdd-HHmmss") + ".txt")

function Say([string]$Text) { Write-Host $Text; Add-Content -Path $Log -Value $Text -Encoding UTF8 }

function Fetch([string]$Url, [string]$Dest) {
    if ((Test-Path $Dest) -and (Get-Item $Dest).Length -gt 0) { return }
    Say "download $(Split-Path $Dest -Leaf)"
    & curl.exe -fL --retry 3 -o "$Dest.part" $Url
    if ($LASTEXITCODE -ne 0) { throw "download failed" }
    Move-Item -Force "$Dest.part" $Dest
}

function Run-Bench([string[]]$BenchArgs) {
    $operation = if ($BenchArgs.Count -gt 0) { $BenchArgs[0] } else { "unknown" }
    Say ("> Quill.Bench $operation")
    $timer = [System.Diagnostics.Stopwatch]::StartNew()
    $output = & $Exe @BenchArgs 2>&1 | ForEach-Object { "$_" }
    $timer.Stop()
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        $script:BenchFailures++
        Say "measurement failed: $operation"
        return $false
    }

    $safeLines = @(Get-QuillSafeMeasurementLines -Lines $output)
    if ($safeLines.Count -eq 0) {
        Say ("measurement elapsed: {0} ms" -f $timer.ElapsedMilliseconds)
        return $true
    }

    foreach ($line in $safeLines) {
        Say $line
    }
    return $true
}

# ---------------------------------------------------------------- environment
Say "== Quill Next F0 Windows measurements $(Get-Date -Format o)"
$cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
$gpus = Get-CimInstance Win32_VideoController | ForEach-Object { "$($_.Name) (driver $($_.DriverVersion))" }
$ram = [math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB, 1)
$dimms = (Get-CimInstance Win32_PhysicalMemory | Measure-Object).Count
Say "CPU: $($cpu.Name) cores=$($cpu.NumberOfCores) threads=$($cpu.NumberOfLogicalProcessors)"
Say "GPU: $($gpus -join '; ')"
Say "RAM: $ram GB in $dimms module(s)"
Say "OS: $([Environment]::OSVersion.VersionString)"
if ($Threads -le 0) { $Threads = [math]::Max(1, [math]::Min(8, $cpu.NumberOfCores)) }
Say "threads used: $Threads"
if (-not (Test-Path "$env:WINDIR\System32\vulkan-1.dll")) { Say "WARNING: vulkan-1.dll not found; GPU runs will fall back or fail" }

$sdk = & dotnet --list-sdks 2>$null
if (-not ($sdk -match "^10\.")) { throw ".NET 10 SDK is required (dotnet --list-sdks shows: $sdk)" }

# ---------------------------------------------------------------- models
$HF = "https://huggingface.co"
$GH = "https://github.com/k2-fsa/sherpa-onnx/releases/download"
Fetch "$HF/ggerganov/whisper.cpp/resolve/main/ggml-small.bin" "$Cache\whisper\ggml-small.bin"
Fetch "$HF/ggerganov/whisper.cpp/resolve/main/ggml-small-q5_1.bin" "$Cache\whisper\ggml-small-q5_1.bin"
Fetch "$HF/ggml-org/whisper-vad/resolve/main/ggml-silero-v5.1.2.bin" "$Cache\whisper\ggml-silero-v5.1.2.bin"
Fetch "$GH/speaker-segmentation-models/sherpa-onnx-pyannote-segmentation-3-0.tar.bz2" "$Cache\sherpa\seg.tar.bz2"
Fetch "$GH/speaker-recongition-models/3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx" "$Cache\sherpa\campplus.onnx"
if (-not (Test-Path "$Cache\sherpa\sherpa-onnx-pyannote-segmentation-3-0\model.onnx")) {
    & tar.exe -xjf "$Cache\sherpa\seg.tar.bz2" -C "$Cache\sherpa"
}

# ---------------------------------------------------------------- audio
Add-Type -AssemblyName System.Speech
function Synthesize([string]$Path, [object[]]$Turns) {
    $synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
    $format = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo(16000, [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen, [System.Speech.AudioFormat.AudioChannel]::Mono)
    $synth.SetOutputToWaveFile($Path, $format)
    foreach ($t in $Turns) { $synth.SelectVoice($t[0]); $synth.Speak($t[1]) }
    $synth.Dispose()
}
$voices = (New-Object System.Speech.Synthesis.SpeechSynthesizer).GetInstalledVoices() | Where-Object { $_.Enabled } | ForEach-Object { $_.VoiceInfo }
$es = @($voices | Where-Object { $_.Culture.Name -like "es-*" })
$any = @($voices)
Say "TTS voices: $(($voices | ForEach-Object { "$($_.Name) [$($_.Culture)]" }) -join ', ')"

if ($any.Count -eq 0) {
    throw "No enabled TTS voice is installed."
}

if ($es.Count -eq 0) {
    Say "WARNING: no Spanish TTS voice installed; using an installed fallback voice."
    $es = $any
}

$speech = "Buenos días a todos. Vamos a repasar el estado del proyecto antes de la entrega del jueves. El equipo de pagos ha terminado la integración con la nueva pasarela y las pruebas de carga han ido bien. Todavía tenemos un problema con las credenciales del entorno de pruebas del proveedor de mensajes, que caducaron la semana pasada. Elena se encarga de hablar con ellos hoy mismo. Por otro lado, la integración continua está tardando demasiado, casi cuarenta minutos por ejecución, y eso puede retrasar la publicación. Propongo congelar los cambios en la rama principal desde el miércoles por la tarde. Si nadie tiene objeciones, lo dejamos así y lo revisamos en la reunión del viernes."
if ($Audio -eq "") {
    $Audio = "$Cache\audio\es-speech.wav"
    Synthesize $Audio @(, @($es[0].Name, $speech))
}
$two = "$Cache\audio\two-speakers.wav"
$v1 = $es[0].Name
$v2 = if ($es.Count -gt 1) { $es[1].Name } elseif ($any.Count -gt 1) { ($any | Where-Object { $_.Name -ne $v1 } | Select-Object -First 1).Name } else { $v1 }
Synthesize $two @(
    @($v1, "Hola Jorge, ¿cómo va la revisión del módulo de notificaciones?"),
    @($v2, "Bastante bien, me faltan un par de pruebas de integración pero mañana lo tengo."),
    @($v1, "Perfecto. ¿Has visto el problema de las credenciales del proveedor?"),
    @($v2, "Sí, caducaron. Hoy escribo al soporte para que nos las renueven."),
    @($v1, "Genial, avísame cuando lo tengas y lo probamos juntos."),
    @($v2, "De acuerdo. Por cierto, la integración continua sigue lentísima."))
Say "diarization voices: $v1 / $v2"

# ---------------------------------------------------------------- build
Say "== build"
& dotnet build "$BenchDir\Quill.Bench" -c Release -r win-x64 -nologo -v q | Out-Null
if ($LASTEXITCODE -ne 0) { throw "build failed" }
$Exe = "$BenchDir\Quill.Bench\bin\Release\net10.0\win-x64\Quill.Bench.exe"

# ---------------------------------------------------------------- whisper
Say "== whisper live chunks (target: live transcript <= 10 s behind speech)"
foreach ($model in @("ggml-small-q5_1.bin", "ggml-small.bin")) {
    foreach ($gpu in @("false", "true")) {
        Run-Bench @("whisper", "--model", "$Cache\whisper\$model", "--audio", $Audio, "--vad", "$Cache\whisper\ggml-silero-v5.1.2.bin",
            "--chunks", "5,10", "--repeats", "5", "--threads", "$Threads", "--gpu", $gpu)
    }
}

# ---------------------------------------------------------------- diarization
Say "== sherpa-onnx diarization"
foreach ($loop in @(1, 10)) {
    Run-Bench @("diarize", "--audio", $two, "--segmentation", "$Cache\sherpa\sherpa-onnx-pyannote-segmentation-3-0\model.onnx",
        "--embedding", "$Cache\sherpa\campplus.onnx", "--threads", "$Threads", "--loop", "$loop")
}

# ---------------------------------------------------------------- llm
if ($Llm) {
    $files = @{
        "qwen3-4b-instruct-2507" = "unsloth/Qwen3-4B-Instruct-2507-GGUF/Qwen3-4B-Instruct-2507-Q4_K_M.gguf"
        "gemma-3-4b-it"          = "unsloth/gemma-3-4b-it-GGUF/gemma-3-4b-it-Q4_K_M.gguf"
        "phi-4-mini-instruct"    = "unsloth/Phi-4-mini-instruct-GGUF/Phi-4-mini-instruct-Q4_K_M.gguf"
    }
    $path = $files[$LlmModel]
    $repo = $path.Substring(0, $path.LastIndexOf("/"))
    $file = $path.Substring($path.LastIndexOf("/") + 1)
    Fetch "$HF/$repo/resolve/main/$file" "$Cache\llm\$file"
    Say "== LLM summaries ($LlmModel; targets: live refresh <= 60 s compute, final 60-min summary <= 5 min)"
    foreach ($backend in $LlmBackends) {
        Run-Bench @("summarize", "--model", "$Cache\llm\$file", "--label", "$LlmModel-win-$backend", "--only", $LlmMeetings,
            "--backend", $backend, "--out", "$Results")
    }
    Run-Bench @("score", "--results", "$Results", "--report", "$Results\scores-windows.md")
}

if ($script:BenchFailures -gt 0) {
    Say "== failed: $script:BenchFailures required measurement(s) failed"
    exit 1
}

Say "== done. Measurements completed."
