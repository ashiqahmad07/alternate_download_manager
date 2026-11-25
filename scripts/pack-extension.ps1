# Packs the browser extension into files to install or to hand to a store: Firefox's add-on (.xpi) and a zip for
# Chrome and Edge. Both carry the same scripts from the extension folder, each with its own manifest.
#
# Release Firefox keeps only add-ons Mozilla has signed; an unsigned one loads as a temporary add-on, or stays in
# Firefox Developer Edition or Nightly with xpinstall.signatures.required set to false. With -Sign, web-ext has
# Mozilla sign it for handing out yourself (unlisted). That needs Node.js, and an addons.mozilla.org API key in
# WEB_EXT_API_KEY and WEB_EXT_API_SECRET (https://addons.mozilla.org/developers/addon/api/key/). Mozilla signs each
# version once, so raise the version in both manifests before signing again.
#   powershell -ExecutionPolicy Bypass -File scripts\pack-extension.ps1 [-Out <folder>] [-Sign]
param(
    [string]$Out = "",
    [switch]$Sign
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
if (-not $Out) { $Out = Join-Path $root "dist\packages" }
$Out = [IO.Path]::GetFullPath($Out)
New-Item -ItemType Directory -Force -Path $Out | Out-Null
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

$source = Join-Path $root "extension"
if (Get-ChildItem $source -Directory) { throw "The extension has a subfolder, which this script and publish.ps1 don't copy." }
$version = (Get-Content (Join-Path $source "manifest.json") -Raw | ConvertFrom-Json).version
$files = Get-ChildItem $source -File | Where-Object { $_.Name -notlike "manifest*.json" }

# A zip with the manifest at its root, as both browsers need. Each entry is named here: Windows PowerShell's
# Compress-Archive can write backslashes into names, which Firefox refuses.
function Write-Package([string]$Path, [string]$Manifest) {
    if (Test-Path $Path) { Remove-Item $Path -Force }
    $zip = [IO.Compression.ZipFile]::Open($Path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $Manifest, "manifest.json")
        foreach ($file in $files) { [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $file.Name) }
    } finally {
        $zip.Dispose()
    }
}

$chromeZip = Join-Path $Out "ADM-chrome-$version.zip"
$unsigned = Join-Path $Out "ADM-firefox-$version-unsigned.xpi"
Write-Package $chromeZip (Join-Path $source "manifest.json")
Write-Package $unsigned (Join-Path $source "manifest.firefox.json")
Write-Host "Chrome and Edge: $chromeZip"
Write-Host "Firefox, unsigned: $unsigned"

if ($Sign) {
    if (-not ($env:WEB_EXT_API_KEY -and $env:WEB_EXT_API_SECRET)) {
        throw "Signing needs an addons.mozilla.org API key in WEB_EXT_API_KEY and WEB_EXT_API_SECRET."
    }
    $npx = (Get-Command npx.cmd -ErrorAction SilentlyContinue).Source
    if (-not $npx) { throw "Signing needs Node.js, for npx." }
    $work = Join-Path ([IO.Path]::GetTempPath()) ("adm-sign-" + [Guid]::NewGuid().ToString("n"))
    $signedDir = Join-Path $work "signed"
    try {
        # web-ext signs a folder: the unsigned package, unpacked, so Mozilla signs exactly what was packed.
        [IO.Compression.ZipFile]::ExtractToDirectory($unsigned, (Join-Path $work "source"))
        & $npx --yes web-ext@8 sign --channel unlisted --source-dir (Join-Path $work "source") --artifacts-dir $signedDir
        if ($LASTEXITCODE -ne 0) { throw "Mozilla didn't sign the add-on; web-ext says why above." }
        $result = Get-ChildItem $signedDir -Filter *.xpi -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $result) { throw "web-ext finished without a signed add-on." }
        $signed = Join-Path $Out "ADM-firefox-$version.xpi"
        Move-Item $result.FullName $signed -Force
        Write-Host "Firefox, signed by Mozilla: $signed"
    } finally {
        if (Test-Path $work) { Remove-Item $work -Recurse -Force }
    }
}
