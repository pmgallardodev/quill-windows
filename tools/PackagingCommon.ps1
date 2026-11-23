# Shared helpers for the release scripts. Dot-source it: . "$PSScriptRoot\PackagingCommon.ps1"
#
# Works on Windows PowerShell 5.1 and PowerShell 7 on a stock Windows 11 machine:
# - ZIPs are written with System.IO.Compression (part of .NET Framework 4.5+ and .NET),
#   not Compress-Archive, which fails on entries over 2 GB in Windows PowerShell 5.1 and
#   cannot store already-compressed model files without recompressing them.
# - Downloads use curl.exe, shipped in System32 since Windows 10 1803, because it can
#   enforce HTTPS-only redirects and a maximum file size before writing to disk.
#   Invoke-WebRequest is the fallback when curl.exe is missing.
# Downloaded files are only hashed, copied and zipped. Nothing downloaded is executed.

Set-StrictMode -Version Latest

# GitHub rejects release assets of 2 GiB or more.
$script:QuillMaxAssetBytes = [long]2147483648

# Files at least this large (models) are stored without recompression: GGUF/ONNX/ggml
# weights barely compress, and deflating gigabytes only costs build time.
$script:QuillStoreThresholdBytes = [long]64MB

$script:QuillAllowedDownloadHosts = @("huggingface.co", "github.com")

function Get-QuillProperty {
    # StrictMode-safe property read for objects returned by ConvertFrom-Json.
    param(
        [Parameter(Mandatory)] $Object,
        [Parameter(Mandatory)] [string]$Name
    )

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }

    return $property.Value
}

function Assert-QuillVersion {
    param([Parameter(Mandatory)] [string]$Version)

    if ($Version -notmatch '^(dev|\d+\.\d+\.\d+([-+][0-9A-Za-z.-]+)?)$') {
        throw "Invalid version '$Version'. Use SemVer such as 1.4.0 or 1.4.0-beta.1."
    }
}

function Get-QuillSha256 {
    param([Parameter(Mandatory)] [string]$Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Assert-QuillDownloadUri {
    param([Parameter(Mandatory)] [string]$Uri)

    $parsed = $null
    if (-not [System.Uri]::TryCreate($Uri, [System.UriKind]::Absolute, [ref]$parsed)) {
        throw "Not an absolute URL: $Uri"
    }

    if ($parsed.Scheme -ne "https") {
        throw "Only HTTPS downloads are allowed: $Uri"
    }

    if ($script:QuillAllowedDownloadHosts -notcontains $parsed.Host.ToLowerInvariant()) {
        throw "Download host '$($parsed.Host)' is not allowed. Allowed: $($script:QuillAllowedDownloadHosts -join ', ')."
    }
}

function Invoke-QuillDownload {
    # Downloads $Uri to $Destination through a .partial file. Enforces HTTPS (including
    # redirects), an upper size bound and, when given, the exact expected size. It does
    # not check the hash: callers decide what a hash mismatch means.
    param(
        [Parameter(Mandatory)] [string]$Uri,
        [Parameter(Mandatory)] [string]$Destination,
        [Parameter(Mandatory)] [long]$MaxBytes,
        [long]$ExpectedBytes = 0
    )

    Assert-QuillDownloadUri -Uri $Uri

    New-Item -ItemType Directory -Path (Split-Path -Parent $Destination) -Force | Out-Null
    $partial = "$Destination.partial"
    Remove-Item -LiteralPath $partial -Force -ErrorAction SilentlyContinue

    $curl = Get-Command -Name "curl.exe" -CommandType Application -ErrorAction SilentlyContinue
    if ($curl) {
        & $curl.Source --fail --location --silent --show-error `
            --proto "=https" --proto-redir "=https" --tlsv1.2 `
            --retry 3 --retry-delay 5 `
            --max-filesize $MaxBytes `
            --output $partial $Uri
        if ($LASTEXITCODE -ne 0) {
            Remove-Item -LiteralPath $partial -Force -ErrorAction SilentlyContinue
            throw "curl.exe failed ($LASTEXITCODE) downloading $Uri"
        }
    }
    else {
        # Windows PowerShell 5.1 defaults to TLS 1.0 on older .NET Framework settings.
        [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12
        $previousProgress = $ProgressPreference
        $ProgressPreference = "SilentlyContinue"
        try {
            Invoke-WebRequest -Uri $Uri -OutFile $partial -UseBasicParsing -MaximumRedirection 5
        }
        catch {
            Remove-Item -LiteralPath $partial -Force -ErrorAction SilentlyContinue
            throw
        }
        finally {
            $ProgressPreference = $previousProgress
        }
    }

    $length = (Get-Item -LiteralPath $partial).Length
    if ($length -gt $MaxBytes) {
        Remove-Item -LiteralPath $partial -Force
        throw "Download of $Uri is $length bytes, above the $MaxBytes byte limit."
    }

    if ($ExpectedBytes -gt 0 -and $length -ne $ExpectedBytes) {
        Remove-Item -LiteralPath $partial -Force
        throw "Download of $Uri is $length bytes; expected $ExpectedBytes."
    }

    Move-Item -LiteralPath $partial -Destination $Destination -Force
}

function Get-QuillDirectoryZipEntries {
    # Maps every file under $Directory to an entry name with forward slashes, optionally
    # under $Prefix. Returns objects with Source and Name.
    param(
        [Parameter(Mandatory)] [string]$Directory,
        [string]$Prefix = ""
    )

    $root = (Resolve-Path -LiteralPath $Directory).ProviderPath.TrimEnd('\', '/')
    foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -File -Force) {
        $relative = $file.FullName.Substring($root.Length + 1).Replace('\', '/')
        $name = if ($Prefix) { "$($Prefix.TrimEnd('/'))/$relative" } else { $relative }
        [pscustomobject]@{ Source = $file.FullName; Name = $name }
    }
}

function New-QuillZip {
    # Writes a ZIP from explicit (Source, Name) entries. Entry names use "/" so the
    # archive extracts identically with Explorer, tar.exe and 7-Zip.
    param(
        [Parameter(Mandatory)] [object[]]$Entries,
        [Parameter(Mandatory)] [string]$DestinationPath
    )

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $names = @{}
    foreach ($entry in $Entries) {
        if ($entry.Name -match '(^|/)\.\.(/|$)' -or $entry.Name.StartsWith("/") -or $entry.Name.Contains("\")) {
            throw "Invalid ZIP entry name: $($entry.Name)"
        }

        if ($names.ContainsKey($entry.Name)) {
            throw "Duplicate ZIP entry: $($entry.Name)"
        }

        $names[$entry.Name] = $true
    }

    New-Item -ItemType Directory -Path (Split-Path -Parent $DestinationPath) -Force | Out-Null
    Remove-Item -LiteralPath $DestinationPath -Force -ErrorAction SilentlyContinue

    $stream = [System.IO.File]::Open($DestinationPath, [System.IO.FileMode]::CreateNew)
    try {
        $archive = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($entry in $Entries) {
                $length = (Get-Item -LiteralPath $entry.Source).Length
                $level = if ($length -ge $script:QuillStoreThresholdBytes) {
                    [System.IO.Compression.CompressionLevel]::NoCompression
                }
                else {
                    [System.IO.Compression.CompressionLevel]::Optimal
                }

                [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                    $archive, $entry.Source, $entry.Name, $level)
            }
        }
        finally {
            $archive.Dispose()
        }
    }
    catch {
        $stream.Dispose()
        Remove-Item -LiteralPath $DestinationPath -Force -ErrorAction SilentlyContinue
        throw
    }
    finally {
        $stream.Dispose()
    }
}

function Get-QuillPartSizes {
    # Mirrors Quill.Core.AiPack.SplitModelPlan.PartSizes: the fewest parts of at most
    # $MaxPartBytes, balanced to differ by at most one byte, larger parts first.
    param(
        [Parameter(Mandatory)] [long]$TotalBytes,
        [Parameter(Mandatory)] [long]$MaxPartBytes
    )

    if ($TotalBytes -le 0 -or $MaxPartBytes -le 0) {
        throw "Part sizes need a positive total ($TotalBytes) and maximum ($MaxPartBytes)."
    }

    # Model sizes stay far below 2^53, so double arithmetic is exact here.
    $count = [long][Math]::Ceiling([double]$TotalBytes / [double]$MaxPartBytes)
    $size = [long][Math]::Floor([double]$TotalBytes / [double]$count)
    $remainder = $TotalBytes - ($size * $count)
    $sizes = New-Object 'System.Collections.Generic.List[long]'
    for ($index = 0; $index -lt $count; $index++) {
        if ($index -lt $remainder) { $sizes.Add($size + 1) } else { $sizes.Add($size) }
    }

    return , $sizes.ToArray()
}

function Split-QuillFile {
    # Copies $Source into consecutive byte ranges, one file per entry of $Parts (objects with
    # Path and Size). The sizes must add up to the source length exactly.
    param(
        [Parameter(Mandatory)] [string]$Source,
        [Parameter(Mandatory)] [object[]]$Parts
    )

    $buffer = New-Object byte[] (4MB)
    $reader = [System.IO.File]::OpenRead($Source)
    try {
        foreach ($part in $Parts) {
            New-Item -ItemType Directory -Path (Split-Path -Parent $part.Path) -Force | Out-Null
            $writer = [System.IO.File]::Open($part.Path, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write)
            try {
                $remaining = [long]$part.Size
                while ($remaining -gt 0) {
                    $want = [int][Math]::Min([long]$buffer.Length, $remaining)
                    $read = $reader.Read($buffer, 0, $want)
                    if ($read -le 0) {
                        throw "$Source ended before part $($part.Path) was complete."
                    }

                    $writer.Write($buffer, 0, $read)
                    $remaining -= $read
                }
            }
            finally {
                $writer.Dispose()
            }
        }

        if ($reader.ReadByte() -ne -1) {
            throw "$Source is longer than the sum of its parts."
        }
    }
    finally {
        $reader.Dispose()
    }
}

function Assert-QuillAssetSize {
    param([Parameter(Mandatory)] [string]$Path)

    $length = (Get-Item -LiteralPath $Path).Length
    if ($length -ge $script:QuillMaxAssetBytes) {
        throw "$Path is $length bytes; GitHub release assets must stay under 2 GiB ($($script:QuillMaxAssetBytes) bytes)."
    }
}

function Format-QuillSize {
    param([Parameter(Mandatory)] [long]$Bytes)

    return "{0:N1} MiB" -f ($Bytes / 1MB)
}
