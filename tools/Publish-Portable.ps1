[CmdletBinding()]
param(
    [switch]$SkipModelDownload
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repositoryRoot "Windows\Quill.Windows\Quill.Windows.csproj"
$model = Join-Path $repositoryRoot "models\ggml-large-v3-q5_0.bin"
$publishDirectory = Join-Path $repositoryRoot "artifacts\quill-win-x64"
$zip = Join-Path $repositoryRoot "artifacts\quill-win-x64.zip"

if (-not $SkipModelDownload -and -not (Test-Path -LiteralPath $model)) {
    & (Join-Path $PSScriptRoot "Get-WhisperModel.ps1")
}

if (-not (Test-Path -LiteralPath $model)) {
    throw "Falta $model. El paquete portable debe incluir Whisper large-v3."
}

Remove-Item -LiteralPath $publishDirectory -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue
dotnet publish $project -c Release -r win-x64 --self-contained true -o $publishDirectory
Compress-Archive -Path $publishDirectory -DestinationPath $zip
Write-Host "Paquete portable creado: $zip"
