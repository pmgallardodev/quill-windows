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

# Umbral de cordura, no el tamano exacto: large-v3 q5_0 ocupa 1,1 GB y
# large-v3-turbo q5_0 unos 547 MB. Solo descarta descargas cortadas.
$minimumBytes = 100MB
$baseUri = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main"

function Get-Model {
    param(
        [string]$Name,
        [string]$Target,
        [long]$Minimum
    )

    if ((Test-Path -LiteralPath $Target) -and ((Get-Item -LiteralPath $Target).Length -ge $Minimum)) {
        Write-Host "Ya disponible: $Target"
        return
    }

    New-Item -ItemType Directory -Path (Split-Path -Parent $Target) -Force | Out-Null
    $partial = "$Target.partial"
    Remove-Item -LiteralPath $partial -Force -ErrorAction SilentlyContinue

    Write-Host "Descargando $Name..."
    Invoke-WebRequest -Uri "$baseUri/$Name" -OutFile $partial

    if ((Get-Item -LiteralPath $partial).Length -lt $Minimum) {
        Remove-Item -LiteralPath $partial -Force
        throw "La descarga de $Name esta incompleta."
    }

    Move-Item -LiteralPath $partial -Destination $Target -Force
    Write-Host "Guardado en $Target"
}

Get-Model -Name $Model -Target $Destination -Minimum $minimumBytes

if ($IncludeVad) {
    $vad = "ggml-silero-v5.1.2.bin"
    Get-Model -Name $vad -Target (Join-Path $modelsDirectory $vad) -Minimum 1MB
}
