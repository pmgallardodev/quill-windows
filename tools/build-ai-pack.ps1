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

  Split models: an entry with "split": { "maxPartBytes": N } is cut into byte ranges
  <path>.part1..partK (Quill.Core.AiPack.SplitModelPlan, mirrored by Get-QuillPartSizes) because
  GitHub refuses assets of 2 GiB or more. Part K goes to volume "<volume>-partK", so the default
  LLM ships as quill-ai-pack-<Version>-llm-part1.zip and -llm-part2.zip. ai-pack.json then lists
  the parts under "splitFiles" with their sizes and SHA-256 plus the whole file's SHA-256 (the
  pinned upstream hash); Quill joins and verifies them on first start and deletes the parts.

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
    $split = Get-QuillProperty $entry "split"
    $maxPartBytes = [long]0
    if ($null -ne $split) {
        $maxPartBytes = [long](Get-QuillProperty $split "maxPartBytes")
    }

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

    if ($null -ne $split) {
        # Leave at least 1 MiB under the asset limit for the ZIP headers of each part.
        if ($maxPartBytes -le 0 -or $maxPartBytes -gt ($script:QuillMaxAssetBytes - 1MB)) {
            throw "Component '$component' split.maxPartBytes must be positive and at most 2 GiB - 1 MiB."
        }

        if ($size -le 0) {
            throw "Component '$component' is split, so it needs its exact 'size' to plan the parts."
        }

        if ($maxBytes -le 0 -or $maxBytes -gt 64GB) {
            throw "Component '$component' maxBytes must be positive and at most 64 GiB."
        }
    }
    elseif ($maxBytes -le 0 -or $maxBytes -ge $script:QuillMaxAssetBytes) {
        throw "Component '$component' maxBytes must be positive and below 2 GiB (or add a 'split' block)."
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
        Split       = ($null -ne $split)
        MaxPartBytes = $maxPartBytes
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

function Get-PartVolume {
    param([string]$Volume, [int]$PartNumber)

    return "$Volume-part$PartNumber"
}

# Fail before downloading gigabytes when a volume is already known to be too large.
$volumeEstimates = @{}
foreach ($model in $models) {
    if ($model.Split) {
        $partSizes = Get-QuillPartSizes -TotalBytes $model.Size -MaxPartBytes $model.MaxPartBytes
        for ($index = 0; $index -lt $partSizes.Count; $index++) {
            $partVolume = Get-PartVolume -Volume $model.Volume -PartNumber ($index + 1)
            $volumeEstimates[$partVolume] = [long]$volumeEstimates[$partVolume] + $partSizes[$index]
        }
    }
    else {
        $bytes = if ($model.Size -gt 0) { $model.Size } else { $model.MaxBytes }
        $volumeEstimates[$model.Volume] = [long]$volumeEstimates[$model.Volume] + $bytes
    }
}

foreach ($volumeName in $volumeEstimates.Keys) {
    if ($volumeEstimates[$volumeName] -ge $script:QuillMaxAssetBytes) {
        throw "Volume '$volumeName' would hold up to $($volumeEstimates[$volumeName]) bytes, over the 2 GiB asset limit. Move a model to another volume, split it, or choose a smaller one."
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

# --- 3. Split large models, generate ai-pack.json, notices and readme -------------------

$workDirectory = Join-Path $OutputDirectory "ai-pack-work-$Version"
Remove-Item -LiteralPath $workDirectory -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $workDirectory -Force | Out-Null
$partsDirectory = Join-Path $workDirectory "parts"
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

# Every file that goes into a ZIP: Volume, Source (local file) and Name (entry inside the ZIP).
$packEntries = @()
$files = [ordered]@{}
$splitFiles = [ordered]@{}
$componentRecords = @()
foreach ($model in $models) {
    $files[$model.Component] = $model.Path
    $record = [ordered]@{
        component  = $model.Component
        name       = $model.Name
        file       = $model.Path
        size       = $model.Size
        sha256     = $model.Sha256
        license    = $model.License
        licenseUrl = $model.LicenseUrl
        source     = $model.Source
    }

    if (-not $model.Split) {
        $record["asset"] = Get-AssetName -Volume $model.Volume
        $packEntries += [pscustomobject]@{ Volume = $model.Volume; Source = $model.LocalPath; Name = "$packFolder/$($model.Path)" }
        $componentRecords += $record
        continue
    }

    # Plan from the verified local size (equal to the pinned size when the hash is pinned).
    $partSizes = Get-QuillPartSizes -TotalBytes $model.Size -MaxPartBytes $model.MaxPartBytes
    $parts = @()
    for ($index = 0; $index -lt $partSizes.Count; $index++) {
        $number = $index + 1
        $partPath = "$($model.Path).part$number"      # SplitModelPlan.PartPath
        $parts += [pscustomobject]@{
            Number = $number
            File   = $partPath
            Path   = Join-Path $partsDirectory ($partPath.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
            Size   = [long]$partSizes[$index]
            Volume = Get-PartVolume -Volume $model.Volume -PartNumber $number
        }
    }

    Write-Host "Split    $($model.Component): $($parts.Count) parts"
    Split-QuillFile -Source $model.LocalPath -Parts $parts

    $partRecords = @()
    foreach ($part in $parts) {
        $actualSize = (Get-Item -LiteralPath $part.Path).Length
        if ($actualSize -ne $part.Size) {
            throw "Part $($part.File) is $actualSize bytes; expected $($part.Size)."
        }

        $partRecords += [ordered]@{
            file   = $part.File
            size   = $part.Size
            sha256 = Get-QuillSha256 -Path $part.Path
            asset  = Get-AssetName -Volume $part.Volume
        }
        $packEntries += [pscustomobject]@{ Volume = $part.Volume; Source = $part.Path; Name = "$packFolder/$($part.File)" }
    }

    # What ManifestAiPackLocator reads: the whole file's size and pinned SHA-256, and the parts.
    $splitFiles[$model.Component] = [ordered]@{
        size   = $model.Size
        sha256 = $model.Sha256
        parts  = @($partRecords | ForEach-Object { [ordered]@{ file = $_.file; size = $_.size; sha256 = $_.sha256 } })
    }
    $record["parts"] = $partRecords
    $componentRecords += $record
}

# "version", "files" and "splitFiles" are what ManifestAiPackLocator reads; the rest is
# informational. Quill versions without split support see a split model as missing.
$packManifest = [ordered]@{
    schema     = 2
    version    = $Version
    createdUtc = [DateTime]::UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
    files      = $files
    splitFiles = $splitFiles
    components = $componentRecords
}
[System.IO.File]::WriteAllText(
    (Join-Path $workDirectory $manifestFileName),
    ($packManifest | ConvertTo-Json -Depth 8),
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
    if ($model.Split) {
        [void]$notices.AppendLine("- Shipped as byte-exact parts ($packFolder/$($model.Path).part1, .part2, ...) that Quill joins and verifies against the SHA-256 above.")
    }
    [void]$notices.AppendLine("- License: $($model.License) - $($model.LicenseUrl)")
    [void]$notices.AppendLine("- Source: $($model.Source)")
    [void]$notices.AppendLine("- Downloaded from: $($model.Url)")
    if ($model.Attribution) {
        [void]$notices.AppendLine("- Attribution: $($model.Attribution)")
    }
}
[System.IO.File]::WriteAllText((Join-Path $workDirectory "THIRD-PARTY-NOTICES.md"), $notices.ToString(), $utf8NoBom)

$assetNames = @(@($packEntries | ForEach-Object { Get-AssetName -Volume $_.Volume }) + @(Get-AssetName -Volume "main") | Sort-Object -Unique)
$splitNote = ""
if ($splitFiles.Count -gt 0) {
    $splitNote = @"

Large models come in parts (*.part1, *.part2, ...) spread over several ZIPs. On first start
Quill joins them into one file, checks its SHA-256 and deletes the parts. This takes a few
minutes, needs about 2.5 GB of free disk space, and does not stop you from recording. If the
ai-pack folder is read-only, the joined model goes to %LOCALAPPDATA%\Quill\ai-pack instead.
"@
}

$readme = @"
Quill AI pack $Version

Extract every quill-ai-pack-$Version*.zip into the folder that contains quill.exe.
The result must be:

  <Quill folder>\quill.exe
  <Quill folder>\$packFolder\$manifestFileName
$splitNote
To keep the pack elsewhere, extract it there and set "ai": { "pack_path": "<folder>\\$packFolder" }
in config.json, or choose the folder in Quill's Settings.

Assets of this pack: $($assetNames -join ', ')
Quill reads the models from disk only and never downloads anything.
"@
[System.IO.File]::WriteAllText((Join-Path $workDirectory "README.txt"), $readme, $utf8NoBom)

foreach ($generated in @($manifestFileName, "THIRD-PARTY-NOTICES.md", "README.txt")) {
    $packEntries += [pscustomobject]@{ Volume = "main"; Source = (Join-Path $workDirectory $generated); Name = "$packFolder/$generated" }
}

# --- 4. Zip every volume ----------------------------------------------------------------

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
# Remove only this version's exact asset names. A wildcard prefix would make a stable build
# delete prerelease assets such as 1.4.0-beta.1.zip; the generated volume set also removes
# stale volumes such as the former -llm.zip without crossing the version boundary.
$assetVolumes = @("main") + @($models | ForEach-Object { [string]$_.Volume }) +
    @($packEntries | ForEach-Object { [string]$_.Volume } | Sort-Object -Unique)
Get-ChildItem -LiteralPath $OutputDirectory -File -Filter "quill-ai-pack-*.zip" |
    Where-Object { Test-QuillVersionedAssetName -Name $_.Name -Version $Version -Volumes $assetVolumes } |
    Remove-Item -Force

$assets = @()
foreach ($group in $packEntries | Group-Object -Property Volume) {
    $zip = Join-Path $OutputDirectory (Get-AssetName -Volume $group.Name)
    Write-Host "Zipping  $zip"
    New-QuillZip -Entries @($group.Group) -DestinationPath $zip
    Assert-QuillAssetSize -Path $zip
    $assets += Get-Item -LiteralPath $zip
}

# The parts are inside the ZIPs now; do not keep a second copy of the model on disk.
Remove-Item -LiteralPath $partsDirectory -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "AI pack $Version built:"
foreach ($asset in $assets) {
    Write-Host ("  {0}  {1}  sha256 {2}" -f $asset.Name, (Format-QuillSize -Bytes $asset.Length), (Get-QuillSha256 -Path $asset.FullName))
}
