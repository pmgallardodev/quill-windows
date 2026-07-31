[CmdletBinding()]
param(
    [string]$Destination = (Join-Path $PSScriptRoot "..\models\ggml-large-v3-q5_0.bin")
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$minimumBytes = 900MB
$uri = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-q5_0.bin"
$directory = Split-Path -Parent $Destination

if ((Test-Path -LiteralPath $Destination) -and ((Get-Item -LiteralPath $Destination).Length -ge $minimumBytes)) {
    Write-Host "Whisper large-v3 ya esta disponible: $Destination"
    exit 0
}

New-Item -ItemType Directory -Path $directory -Force | Out-Null
$partial = "$Destination.partial"
Remove-Item -LiteralPath $partial -Force -ErrorAction SilentlyContinue

Write-Host "Descargando Whisper large-v3 Q5_0 (aprox. 1.1 GB)..."
Invoke-WebRequest -Uri $uri -OutFile $partial

if ((Get-Item -LiteralPath $partial).Length -lt $minimumBytes) {
    Remove-Item -LiteralPath $partial -Force
    throw "La descarga del modelo esta incompleta."
}

Move-Item -LiteralPath $partial -Destination $Destination -Force
Write-Host "Modelo guardado en $Destination"
