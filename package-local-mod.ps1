# Packages AuraPlugin into a Thunderstore-layout zip that r2modman can import directly
# via "Settings > Profile > Import Local Mod" (or drag-and-drop onto that screen).
# Run this after building (AuraPlugin.dll must already exist in bin\).

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$dll = Join-Path $root "bin\AuraPlugin.dll"

if (-not (Test-Path $dll)) {
    throw "bin\AuraPlugin.dll not found - build the project first (msbuild AuraPlugin.csproj)."
}

$manifest = Get-Content (Join-Path $root "manifest.json") | ConvertFrom-Json
$version = $manifest.version_number
$outZip = Join-Path $root "AuraPlugin-$version.zip"

if (Test-Path $outZip) { Remove-Item $outZip -Force }

# Build the archive directly via .NET (rather than Compress-Archive, which writes invalid
# backslash-separated entry names on Windows). Everything sits flat at the zip root, including
# aura.png - r2modman's Import Local Mod doesn't reliably preserve nested subfolders on
# extraction, which previously left aura.png missing on a fresh install even though it was
# correctly present in the zip.
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$filesToAdd = @{
    "manifest.json"  = (Join-Path $root "manifest.json")
    "icon.png"       = (Join-Path $root "icon.png")
    "README.md"      = (Join-Path $root "README.md")
    "AuraPlugin.dll" = $dll
    "aura.png"       = (Join-Path $root "aura.png")
}

# Check everything up front - CreateEntryFromFile throws partway through otherwise, which
# would leave a half-written zip on disk that still looks like a shippable release artifact.
$missing = $filesToAdd.Values | Where-Object { -not (Test-Path $_) }
if ($missing) {
    throw "Missing file(s) needed for packaging:`n  $($missing -join "`n  ")"
}

$fs = $null
$archive = $null
try {
    $fs = [System.IO.File]::Open($outZip, [System.IO.FileMode]::Create)
    $archive = New-Object System.IO.Compression.ZipArchive($fs, [System.IO.Compression.ZipArchiveMode]::Create)
    foreach ($entryName in $filesToAdd.Keys) {
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $filesToAdd[$entryName], $entryName) | Out-Null
    }
}
finally {
    # Must dispose in this order (archive flushes into the stream), and must happen even on
    # failure - a leaked handle keeps the zip locked, so the next run can't delete/replace it.
    if ($archive) { $archive.Dispose() }
    if ($fs) { $fs.Dispose() }
}

Write-Host "Packaged: $outZip"
Write-Host "In r2modman, use Settings > Profile > Import Local Mod and select this zip (or drag it onto that screen)."
