[CmdletBinding()]
param(
    [switch]$SkipModelDownload,
    # Descarga y empaqueta tambien el modelo Silero para poder activar "vad".
    [switch]$IncludeVad
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repositoryRoot "Windows\Quill.Windows\Quill.Windows.csproj"
$publishDirectory = Join-Path $repositoryRoot "artifacts\quill-win-x64"
$zip = Join-Path $repositoryRoot "artifacts\quill-win-x64.zip"
$minimumModelBytes = 1MB

# El nombre del modelo vive solo en el csproj; aqui se lee para no duplicarlo.
$modelName = (dotnet msbuild $project -target:PrintWhisperModel -verbosity:minimal -nologo).Trim()
if (-not $modelName) {
    throw "No se pudo leer QuillWhisperModel del proyecto."
}

$model = Join-Path $repositoryRoot "models\$modelName"

if (-not $SkipModelDownload -and (-not (Test-Path -LiteralPath $model) -or $IncludeVad)) {
    & (Join-Path $PSScriptRoot "Get-WhisperModel.ps1") -Model $modelName -IncludeVad:$IncludeVad
}

if (-not (Test-Path -LiteralPath $model)) {
    throw "Falta $model. El paquete portable debe incluir el modelo Whisper."
}

if ((Get-Item -LiteralPath $model).Length -lt $minimumModelBytes) {
    throw "La descarga de $model esta incompleta."
}

if ($IncludeVad) {
    $vad = Join-Path $repositoryRoot "models\ggml-silero-v5.1.2.bin"
    if (-not (Test-Path -LiteralPath $vad)) {
        throw "Falta $vad. El paquete portable debe incluir el modelo VAD solicitado."
    }
}

Remove-Item -LiteralPath $publishDirectory -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue
dotnet publish $project -c Release -r win-x64 --self-contained true -o $publishDirectory
Compress-Archive -Path $publishDirectory -DestinationPath $zip
Write-Host "Paquete portable creado: $zip ($modelName)"
