[CmdletBinding()]
param(
    # Nombre del archivo en https://huggingface.co/ggerganov/whisper.cpp
    [string]$Model = "ggml-large-v3-turbo-q5_0.bin",
    [string]$Destination,
    # Descarga tambien el modelo Silero que necesita "vad": true en config.json.
    [switch]$IncludeVad
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$modelsDirectory = Join-Path $PSScriptRoot "..\models"
if (-not $Destination) {
    $Destination = Join-Path $modelsDirectory $Model
}

# Umbral comun para todos los modelos Whisper, incluidos los modelos tiny. No es
# un tamano exacto: solo descarta respuestas claramente truncadas.
$minimumWhisperBytes = 1MB
$minimumVadBytes = 500KB
$baseUri = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main"
$vadBaseUri = "https://huggingface.co/sandrohanea/whisper.net/resolve/v4/vad"

. (Join-Path $PSScriptRoot "PackagingCommon.ps1")

function Get-Model {
    param(
        [string]$Name,
        [string]$Target,
        [long]$Minimum,
        [string]$BaseUri
    )

    if ((Test-Path -LiteralPath $Target) -and ((Get-Item -LiteralPath $Target).Length -ge $Minimum)) {
        if (Test-QuillPinnedWhisperModel -Name $Name -Path $Target) {
            Write-Host "Ya disponible: $Target"
            return
        }

        Write-Warning "El hash de $Target no coincide; se descarga de nuevo."
    }

    New-Item -ItemType Directory -Path (Split-Path -Parent $Target) -Force | Out-Null
    $partial = "$Target.partial"
    Remove-Item -LiteralPath $partial -Force -ErrorAction SilentlyContinue

    Write-Host "Descargando $Name..."
    # Windows PowerShell 5.1 can default to TLS 1.0; Hugging Face requires TLS 1.2+.
    [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12
    $ProgressPreference = "SilentlyContinue"
    Invoke-WebRequest -Uri "$BaseUri/$Name" -OutFile $partial -UseBasicParsing

    if ((Get-Item -LiteralPath $partial).Length -lt $Minimum) {
        Remove-Item -LiteralPath $partial -Force
        throw "La descarga de $Name esta incompleta."
    }

    if (-not (Test-QuillPinnedWhisperModel -Name $Name -Path $partial)) {
        Remove-Item -LiteralPath $partial -Force
        throw "El SHA-256 de $Name no coincide con el valor fijado."
    }

    Move-Item -LiteralPath $partial -Destination $Target -Force
    Write-Host "Guardado en $Target"
}

Get-Model -Name $Model -Target $Destination -Minimum $minimumWhisperBytes -BaseUri $baseUri

if ($IncludeVad) {
    $vad = "ggml-silero-v5.1.2.bin"
    # The VAD model is hosted by Whisper.net and is smaller than 1 MB.
    Get-Model -Name $vad -Target (Join-Path $modelsDirectory $vad) -Minimum $minimumVadBytes -BaseUri $vadBaseUri
}
