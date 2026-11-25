# Builds ADM's per-user installer, dist\installer\ADM-Setup-<version>.exe, with Inno Setup 6, and prints its path:
#   powershell -ExecutionPolicy Bypass -File scripts\build-installer.ps1 [-Version 1.2.3]
# The version is Directory.Build.props's unless one is given; test-installer.ps1 is run with an older one to upgrade from.
param([string]$Version = "")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

if (-not $Version) { $Version = ([xml](Get-Content (Join-Path $root "Directory.Build.props") -Raw)).Project.PropertyGroup.Version }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "The version '$Version' is not of the form 1.2.3." }

$iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
if (-not $iscc) {
    $iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") |
        Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $iscc) {
    throw "Inno Setup 6 was not found. Install it from https://jrsoftware.org/isdl.php, or with: winget install JRSoftware.InnoSetup --scope user"
}

$package = Join-Path $root "dist\package"
$installers = Join-Path $root "dist\installer"
$videoTools = Join-Path $root "dist\video-tools"
if (Test-Path $package) { Remove-Item $package -Recurse -Force }
& (Join-Path $PSScriptRoot "publish.ps1") -Out $package -Version $Version

# yt-dlp, Deno and ffmpeg go in the installer, so a site's video works without fetching them first; their licences and
# versions go beside ADM's.
& (Join-Path $PSScriptRoot "fetch-video-tools.ps1") -Out $videoTools
Copy-Item (Join-Path $videoTools "licenses") (Join-Path $package "licenses") -Recurse

& $iscc /Q "/DAppVersion=$Version" "/DSource=$package" "/DTools=$(Join-Path $videoTools 'tools')" "/O$installers" (Join-Path $root "installer\ADM.iss")
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed." }
Join-Path $installers "ADM-Setup-$Version.exe"
