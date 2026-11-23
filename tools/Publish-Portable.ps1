[CmdletBinding()]
param(
    # Release version used in the asset name: artifacts\quill-<Version>-win-x64.zip.
    [string]$Version = "dev",
    [switch]$SkipModelDownload,
    # Descarga y empaqueta tambien el modelo Silero para poder activar "vad".
    [switch]$IncludeVad
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "PackagingCommon.ps1")
Assert-QuillVersion -Version $Version

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repositoryRoot "Windows\Quill.Windows\Quill.Windows.csproj"
$assetName = "quill-$Version-win-x64"
$publishDirectory = Join-Path $repositoryRoot "artifacts\$assetName"
$zip = Join-Path $repositoryRoot "artifacts\$assetName.zip"
$minimumWhisperBytes = 1MB
$minimumVadBytes = 500KB

# El nombre del modelo vive solo en el csproj; aqui se lee para no duplicarlo.
$modelOutput = dotnet msbuild $project -target:PrintWhisperModel -verbosity:minimal -nologo
if ($LASTEXITCODE -ne 0) {
    throw "No se pudo leer QuillWhisperModel del proyecto."
}

$modelName = ($modelOutput -join [Environment]::NewLine).Trim()
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

if ((Get-Item -LiteralPath $model).Length -lt $minimumWhisperBytes) {
    throw "La descarga de $model esta incompleta."
}
Assert-QuillPinnedWhisperModel -Name $modelName -Path $model

if ($IncludeVad) {
    $vad = Join-Path $repositoryRoot "models\ggml-silero-v5.1.2.bin"
    if (-not (Test-Path -LiteralPath $vad)) {
        throw "Falta $vad. El paquete portable debe incluir el modelo VAD solicitado."
    }

    if ((Get-Item -LiteralPath $vad).Length -lt $minimumVadBytes) {
        throw "La descarga de $vad esta incompleta."
    }

    Assert-QuillPinnedWhisperModel -Name "ggml-silero-v5.1.2.bin" -Path $vad
}

Remove-Item -LiteralPath $publishDirectory -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue
$assemblyVersion = if ($Version -eq "dev") { "0.0.0-dev" } else { $Version }
dotnet publish $project -c Release -r win-x64 --self-contained true "-p:Version=$assemblyVersion" -o $publishDirectory
if ($LASTEXITCODE -ne 0) {
    throw "No se pudo publicar Quill para win-x64."
}

# Native runtimes that must ship with the app. LLamaSharp picks a CPU variant by
# instruction set (noavx/avx/avx2/avx512) or the Vulkan build; Whisper.net has its own
# CPU and Vulkan builds (suffixed "-whisper"); sherpa-onnx needs its C API + ONNX Runtime.
# Layout observed in a net10.0 self-contained publish (LLamaSharp 0.30.0, Whisper.net
# 1.9.1, sherpa-onnx 2.0.3).
$requiredFiles = @(
    "quill.exe",
    "models\$modelName",
    "LLamaSharp.dll",
    "runtimes\win-x64\native\vulkan\llama.dll",
    "runtimes\win-x64\native\vulkan\ggml.dll",
    "runtimes\win-x64\native\vulkan\ggml-base.dll",
    "runtimes\win-x64\native\vulkan\ggml-vulkan.dll",
    "Whisper.net.dll",
    "runtimes\win-x64\whisper.dll",
    "runtimes\win-x64\ggml-cpu-whisper.dll",
    "runtimes\vulkan\win-x64\whisper.dll",
    "runtimes\vulkan\win-x64\ggml-vulkan-whisper.dll",
    "sherpa-onnx.dll",
    "sherpa-onnx-c-api.dll",
    "onnxruntime.dll"
)
foreach ($variant in @("noavx", "avx", "avx2", "avx512")) {
    foreach ($library in @("llama.dll", "ggml.dll", "ggml-base.dll", "ggml-cpu.dll")) {
        $requiredFiles += "runtimes\win-x64\native\$variant\$library"
    }
}

$missing = @($requiredFiles | Where-Object { -not (Test-Path -LiteralPath (Join-Path $publishDirectory $_)) })
if ($missing.Count -gt 0) {
    throw "The publish output is missing native runtime files:`n  $($missing -join "`n  ")"
}

Write-Host "Native runtimes present: $($requiredFiles.Count) required files checked."

# License and notices travel with the binaries.
foreach ($notice in @("THIRD_PARTY_NOTICES.md", "LICENSE")) {
    $source = Join-Path $repositoryRoot $notice
    if (Test-Path -LiteralPath $source) {
        Copy-Item -LiteralPath $source -Destination (Join-Path $publishDirectory $notice) -Force
    }
    elseif ($notice -eq "THIRD_PARTY_NOTICES.md") {
        throw "Falta $source."
    }
    else {
        Write-Warning "No LICENSE file at the repository root; the ZIP ships without one."
    }
}

# El ZIP representa la carpeta extraida: quill.exe queda en la raiz, no dentro de
# artifacts\quill-<version>-win-x64\.
New-QuillZip -Entries @(Get-QuillDirectoryZipEntries -Directory $publishDirectory) -DestinationPath $zip
Assert-QuillAssetSize -Path $zip
Write-Host "Paquete portable creado: $zip ($modelName, $(Format-QuillSize -Bytes (Get-Item -LiteralPath $zip).Length))"
