<#
.SYNOPSIS
  Builds the Quill AI pack release asset(s) from tools/ai-pack.models.json.

.DESCRIPTION
  Runs on a developer machine at release time only; the app itself never downloads.
  For each model in the models manifest it downloads the pinned URL (HTTPS only, size
  bounded), verifies the pinned SHA-256, lays the files out under an "ai-pack" folder as
  Quill.Core.AiPack.ManifestAiPackLocator expects, writes ai-pack.json and the model
  notices, and zips every volume:

    main volume   -> artifacts\quill-ai-pack-<Version>.zip          (ai-pack.json, notices, models)
    other volumes -> artifacts\quill-ai-pack-<Version>-<volume>.zip (models only)

  Every ZIP holds paths starting with "ai-pack/", so extracting all of them into the folder
  that contains quill.exe produces <app>\ai-pack\ai-pack.json. Each ZIP must stay under
  2 GiB (GitHub release asset limit); the script fails otherwise.

  Hashes: an entry whose sha256 is "TO_FILL_ON_FIRST_BUILD" is downloaded, its SHA-256 and
  size are printed, and packaging stops unless -AcceptNewHashes is given. Copy the printed
  values into tools/ai-pack.models.json and commit them so later builds are pinned.

  Downloaded files are never executed: they are hashed, read and copied into ZIPs only.

.EXAMPLE
  .\tools\build-ai-pack.ps1 -Version 1.4.0
.EXAMPLE
  .\tools\build-ai-pack.ps1 -Version 1.4.0 -AcceptNewHashes
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Version,
    [string]$ModelsManifest,
    # Downloaded models are kept here, keyed by hash, so a rebuild does not download again.
    [string]$CacheDirectory,
    [string]$OutputDirectory,
    [switch]$AcceptNewHashes
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "PackagingCommon.ps1")
Assert-QuillVersion -Version $Version

$repositoryRoot = Split-Path -Parent $PSScriptRoot
if (-not $ModelsManifest) { $ModelsManifest = Join-Path $PSScriptRoot "ai-pack.models.json" }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repositoryRoot "artifacts" }
if (-not $CacheDirectory) { $CacheDirectory = Join-Path $OutputDirectory "ai-pack-cache" }

$placeholderHash = "TO_FILL_ON_FIRST_BUILD"
$packFolder = "ai-pack"                 # ManifestAiPackLocator.DefaultFolderName
$manifestFileName = "ai-pack.json"      # ManifestAiPackLocator.ManifestFileName
# camelCase AiPackComponent names (Quill.Core/Ports/AiPack.cs), the keys the locator reads.
$knownComponents = @("languageModel", "liveWhisper", "diarizationSegmentation", "speakerEmbedding", "textEmbedding")

function Get-AssetName {
    param([string]$Volume)

    if ($Volume -eq "main") {
        return "quill-ai-pack-$Version.zip"
    }

    return "quill-ai-pack-$Version-$Volume.zip"
}

# --- 1. Read and validate the models manifest -------------------------------------------

$definition = Get-Content -LiteralPath $ModelsManifest -Raw -Encoding UTF8 | ConvertFrom-Json
if ((Get-QuillProperty $definition "schema") -ne 1) {
    throw "$ModelsManifest must declare ""schema"": 1."
}

$models = @()
$seenComponents = @{}
$seenPaths = @{}
foreach ($entry in @(Get-QuillProperty $definition "components")) {
    $component = [string](Get-QuillProperty $entry "component")
    $path = [string](Get-QuillProperty $entry "path")
    $volume = [string](Get-QuillProperty $entry "volume")
    $url = [string](Get-QuillProperty $entry "url")
    $sha256 = ([string](Get-QuillProperty $entry "sha256")).Trim()
    $size = [long](Get-QuillProperty $entry "size")
    $maxBytes = [long](Get-QuillProperty $entry "maxBytes")

    if ($knownComponents -cnotcontains $component) {
        throw "Unknown component '$component'. Expected one of: $($knownComponents -join ', ')."
    }

    if ($seenComponents.ContainsKey($component)) {
        throw "Component '$component' is listed twice."
    }

    $seenComponents[$component] = $true

    # Relative, forward slashes, no traversal: the locator rejects anything else.
    if ($path -notmatch '^[A-Za-z0-9._-]+(/[A-Za-z0-9._-]+)*$' -or $path -match '(^|/)\.\.?(/|$)') {
        throw "Component '$component' has an invalid path '$path'. Use a relative path such as llm/model.gguf."
    }

    if ($path -eq $manifestFileName -or $seenPaths.ContainsKey($path.ToLowerInvariant())) {
        throw "Component '$component' path '$path' collides with another file."
    }

    $seenPaths[$path.ToLowerInvariant()] = $true

    if ($volume -notmatch '^[a-z0-9]+(-[a-z0-9]+)*$') {
        throw "Component '$component' has an invalid volume '$volume'."
    }

    Assert-QuillDownloadUri -Uri $url

    $pinned = $sha256 -ne $placeholderHash
    if ($pinned -and $sha256 -notmatch '^[0-9a-f]{64}$') {
        throw "Component '$component' sha256 must be 64 lowercase hex characters or $placeholderHash."
    }

    if ($maxBytes -le 0 -or $maxBytes -ge $script:QuillMaxAssetBytes) {
        throw "Component '$component' maxBytes must be positive and below 2 GiB."
    }

    if ($size -lt 0 -or $size -gt $maxBytes) {
        throw "Component '$component' size $size is outside 0..maxBytes ($maxBytes)."
    }

    foreach ($field in @("name", "license", "licenseUrl", "source")) {
        if (-not [string](Get-QuillProperty $entry $field)) {
            throw "Component '$component' needs a non-empty '$field'."
        }
    }

    $models += [pscustomobject]@{
        Component   = $component
        Name        = [string](Get-QuillProperty $entry "name")
        Path        = $path
        Volume      = $volume
        Url         = $url
        Sha256      = $sha256
        Pinned      = $pinned
        Size        = $size
        MaxBytes    = $maxBytes
        License     = [string](Get-QuillProperty $entry "license")
        LicenseUrl  = [string](Get-QuillProperty $entry "licenseUrl")
        Source      = [string](Get-QuillProperty $entry "source")
        Attribution = [string](Get-QuillProperty $entry "attribution")
        LocalPath   = $null
    }
}

$missingComponents = @($knownComponents | Where-Object { -not $seenComponents.ContainsKey($_) })
if ($missingComponents.Count -gt 0) {
    throw "The models manifest lacks: $($missingComponents -join ', ')."
}

if (-not ($models | Where-Object { $_.Volume -eq "main" })) {
    throw "At least one model must be in the 'main' volume."
}

# Fail before downloading gigabytes when a volume is already known to be too large.
foreach ($group in $models | Group-Object -Property Volume) {
    $estimate = ($group.Group | ForEach-Object { if ($_.Size -gt 0) { $_.Size } else { $_.MaxBytes } } |
        Measure-Object -Sum).Sum
    if ($estimate -ge $script:QuillMaxAssetBytes) {
        throw "Volume '$($group.Name)' would hold up to $estimate bytes, over the 2 GiB asset limit. Move a model to another volume or choose a smaller one."
    }
}

# --- 2. Download and verify ---------------------------------------------------------------

$newHashes = @()
foreach ($model in $models) {
    $fileName = Split-Path -Leaf $model.Path
    if ($model.Pinned) {
        $cached = Join-Path $CacheDirectory (Join-Path $model.Sha256 $fileName)
        if ((Test-Path -LiteralPath $cached) -and (Get-QuillSha256 -Path $cached) -eq $model.Sha256) {
            Write-Host "Cached   $($model.Component): $fileName"
        }
        else {
            Write-Host "Download $($model.Component): $($model.Url)"
            Invoke-QuillDownload -Uri $model.Url -Destination $cached -MaxBytes $model.MaxBytes -ExpectedBytes $model.Size
            $actual = Get-QuillSha256 -Path $cached
            if ($actual -ne $model.Sha256) {
                Remove-Item -LiteralPath $cached -Force
                throw "SHA-256 mismatch for $($model.Component): expected $($model.Sha256), got $actual. The file was deleted."
            }
        }
    }
    else {
        $cached = Join-Path $CacheDirectory (Join-Path "unpinned-$($model.Component)" $fileName)
        if (-not (Test-Path -LiteralPath $cached)) {
            Write-Host "Download $($model.Component) (unpinned): $($model.Url)"
            Invoke-QuillDownload -Uri $model.Url -Destination $cached -MaxBytes $model.MaxBytes -ExpectedBytes $model.Size
        }

        $model.Sha256 = Get-QuillSha256 -Path $cached
        $newHashes += $model
    }

    $model.Size = (Get-Item -LiteralPath $cached).Length
    $model.LocalPath = $cached
}

if ($newHashes.Count -gt 0) {
    Write-Host ""
    Write-Host "New hashes (copy into $ModelsManifest and commit):"
    foreach ($model in $newHashes) {
        Write-Host ("  {0}: ""size"": {1}, ""sha256"": ""{2}""" -f $model.Component, $model.Size, $model.Sha256)
    }

    if (-not $AcceptNewHashes) {
        throw "Refusing to package unpinned models. Verify the hashes above against the publisher, pin them, or rerun with -AcceptNewHashes."
    }

    Write-Warning "Packaging with hashes accepted on first download (-AcceptNewHashes)."
}

# --- 3. Generate ai-pack.json, notices and readme ----------------------------------------

$workDirectory = Join-Path $OutputDirectory "ai-pack-work-$Version"
Remove-Item -LiteralPath $workDirectory -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $workDirectory -Force | Out-Null
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

$files = [ordered]@{}
$componentRecords = @()
foreach ($model in $models) {
    $files[$model.Component] = $model.Path
    $componentRecords += [ordered]@{
        component  = $model.Component
        name       = $model.Name
        file       = $model.Path
        size       = $model.Size
        sha256     = $model.Sha256
        license    = $model.License
        licenseUrl = $model.LicenseUrl
        source     = $model.Source
        asset      = Get-AssetName -Volume $model.Volume
    }
}

# "version" and "files" are what ManifestAiPackLocator reads; the rest is informational.
$packManifest = [ordered]@{
    version    = $Version
    createdUtc = [DateTime]::UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
    files      = $files
    components = $componentRecords
}
[System.IO.File]::WriteAllText(
    (Join-Path $workDirectory $manifestFileName),
    ($packManifest | ConvertTo-Json -Depth 6),
    $utf8NoBom)

$notices = New-Object System.Text.StringBuilder
[void]$notices.AppendLine("# Quill AI pack $Version - third-party notices")
[void]$notices.AppendLine("")
[void]$notices.AppendLine("This pack redistributes the following models unchanged, each under its own license.")
[void]$notices.AppendLine("The full license text of each model is at its license URL.")
foreach ($model in $models) {
    [void]$notices.AppendLine("")
    [void]$notices.AppendLine("## $($model.Name)")
    [void]$notices.AppendLine("")
    [void]$notices.AppendLine("- Used for: $($model.Component)")
    [void]$notices.AppendLine("- File: $packFolder/$($model.Path) (SHA-256 $($model.Sha256))")
    [void]$notices.AppendLine("- License: $($model.License) - $($model.LicenseUrl)")
    [void]$notices.AppendLine("- Source: $($model.Source)")
    [void]$notices.AppendLine("- Downloaded from: $($model.Url)")
    if ($model.Attribution) {
        [void]$notices.AppendLine("- Attribution: $($model.Attribution)")
    }
}
[System.IO.File]::WriteAllText((Join-Path $workDirectory "THIRD-PARTY-NOTICES.md"), $notices.ToString(), $utf8NoBom)

$assetNames = @($models | ForEach-Object { Get-AssetName -Volume $_.Volume } | Sort-Object -Unique)
$readme = @"
Quill AI pack $Version

Extract every quill-ai-pack-$Version*.zip into the folder that contains quill.exe.
The result must be:

  <Quill folder>\quill.exe
  <Quill folder>\$packFolder\$manifestFileName

To keep the pack elsewhere, extract it there and set "ai": { "pack_path": "<folder>\\$packFolder" }
in config.json, or choose the folder in Quill's Settings.

Assets of this pack: $($assetNames -join ', ')
Quill reads the models from disk only and never downloads anything.
"@
[System.IO.File]::WriteAllText((Join-Path $workDirectory "README.txt"), $readme, $utf8NoBom)

# --- 4. Zip every volume ----------------------------------------------------------------

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
foreach ($assetName in $assetNames) {
    Remove-Item -LiteralPath (Join-Path $OutputDirectory $assetName) -Force -ErrorAction SilentlyContinue
}

$assets = @()
foreach ($group in $models | Group-Object -Property Volume) {
    $entries = @($group.Group | ForEach-Object {
            [pscustomobject]@{ Source = $_.LocalPath; Name = "$packFolder/$($_.Path)" }
        })

    if ($group.Name -eq "main") {
        foreach ($generated in @($manifestFileName, "THIRD-PARTY-NOTICES.md", "README.txt")) {
            $entries += [pscustomobject]@{ Source = (Join-Path $workDirectory $generated); Name = "$packFolder/$generated" }
        }
    }

    $zip = Join-Path $OutputDirectory (Get-AssetName -Volume $group.Name)
    Write-Host "Zipping  $zip"
    New-QuillZip -Entries $entries -DestinationPath $zip
    Assert-QuillAssetSize -Path $zip
    $assets += Get-Item -LiteralPath $zip
}

Write-Host ""
Write-Host "AI pack $Version built:"
foreach ($asset in $assets) {
    Write-Host ("  {0}  {1}  sha256 {2}" -f $asset.Name, (Format-QuillSize -Bytes $asset.Length), (Get-QuillSha256 -Path $asset.FullName))
}
