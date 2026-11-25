# Publishes ADM into a folder, ready to run or to pack: the app and its browser host, self-contained for 64-bit Windows;
# the host manifests for Chrome, Edge and Firefox; the extension for each, with a page on adding it; and the licence.
# install.ps1 and build-installer.ps1 both start here.
#   powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 -Out <folder> [-Configuration Release] [-Version 1.2.3]
param(
    [Parameter(Mandatory = $true)][string]$Out,
    [string]$Configuration = "Release",
    [string]$Version = ""
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$Out = [IO.Path]::GetFullPath($Out)

# Use a .NET 8 SDK from PATH, or the per-user one that Microsoft's dotnet-install.ps1 creates.
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not ($dotnet -and (& $dotnet --list-sdks | Select-String '^8\.'))) {
    $dotnet = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe"
    if (-not (Test-Path $dotnet)) { throw "The .NET 8 SDK was not found. Install it from https://dot.net/download." }
}

# Self-contained: the browser starts the native host with its own environment, so ADM
# must not depend on a .NET runtime being installed system-wide.
$versionArgument = @()
if ($Version) { $versionArgument = @("-p:Version=$Version") }
foreach ($project in "ADM.App", "ADM.NativeHost") {
    & $dotnet publish (Join-Path $root "src\$project\$project.csproj") -c $Configuration -r win-x64 --self-contained true -o $Out --nologo @versionArgument
    if ($LASTEXITCODE -ne 0) { throw "Publishing $project failed." }
}

# The extension ID is derived from the public key pinned in its manifest, so it is the
# same on every machine: the first 16 bytes of SHA-256(key), hex digits mapped 0-f to a-p.
$manifest = Get-Content (Join-Path $root "extension\manifest.json") -Raw | ConvertFrom-Json
$sha = [System.Security.Cryptography.SHA256]::Create()
$hash = $sha.ComputeHash([Convert]::FromBase64String($manifest.key))
$extensionId = -join ($hash[0..15] | ForEach-Object { "{0:x2}" -f $_ } | ForEach-Object { $_.ToCharArray() } |
    ForEach-Object { [char](97 + [Convert]::ToInt32([string]$_, 16)) })
$addonId = (Get-Content (Join-Path $root "extension\manifest.firefox.json") -Raw | ConvertFrom-Json).browser_specific_settings.gecko.id

# The host is named relative to its manifest, which Chrome, Edge and Firefox allow on Windows, so the same
# files serve wherever ADM is put.
$utf8 = New-Object Text.UTF8Encoding $false
$chromeHost = [ordered]@{
    name            = "com.adm.host"
    description     = "ADM browser integration"
    path            = "ADM.NativeHost.exe"
    type            = "stdio"
    allowed_origins = @("chrome-extension://$extensionId/")
}
[IO.File]::WriteAllText((Join-Path $Out "com.adm.host.json"), ($chromeHost | ConvertTo-Json), $utf8)
# Firefox allows the add-on by its id rather than by origin.
$firefoxHost = [ordered]@{
    name               = "com.adm.host"
    description        = "ADM browser integration"
    path               = "ADM.NativeHost.exe"
    type               = "stdio"
    allowed_extensions = @($addonId)
}
[IO.File]::WriteAllText((Join-Path $Out "com.adm.host.firefox.json"), ($firefoxHost | ConvertTo-Json), $utf8)

# The extension, for Chrome and Edge, and Firefox's copy: the same scripts with its own manifest (an event page
# instead of a service worker).
$chromeDir = Join-Path $Out "extension"
$firefoxDir = Join-Path $Out "extension-firefox"
foreach ($folder in $chromeDir, $firefoxDir) {
    if (Test-Path $folder) { Remove-Item $folder -Recurse -Force }
    New-Item -ItemType Directory -Path $folder | Out-Null
}
$scripts = Get-ChildItem (Join-Path $root "extension") -File | Where-Object { $_.Name -notlike "manifest*.json" }
$scripts | Copy-Item -Destination $chromeDir
$scripts | Copy-Item -Destination $firefoxDir
Copy-Item (Join-Path $root "extension\manifest.json") (Join-Path $chromeDir "manifest.json")
Copy-Item (Join-Path $root "extension\manifest.firefox.json") (Join-Path $firefoxDir "manifest.json")

Copy-Item (Join-Path $root "LICENSE"), (Join-Path $root "THIRD-PARTY-NOTICES.txt") -Destination $Out
# How to add the extension, step by step; it finds the extension's folder from where it is opened.
Copy-Item (Join-Path $root "installer\browser-setup.html") (Join-Path $Out "Browser setup.html")
