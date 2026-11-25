# Builds ADM into .\dist and registers the browser native-messaging host for the
# current user (Chrome, Edge and Firefox). Run from any directory:
#   powershell -ExecutionPolicy Bypass -File scripts\install.ps1
# The installer (build-installer.ps1) packs the same build and registers its own copy.
param([string]$Configuration = "Release")

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root "dist"

# A build under the app's old name can't be cleared away below while it runs.
if (Get-Process FluxDM -ErrorAction SilentlyContinue) { throw "FluxDM, this app's old name, is running. Close it and run this again." }

& (Join-Path $PSScriptRoot "publish.ps1") -Out $dist -Configuration $Configuration

# What a build under the app's old name left: its files here, and its browser registration.
Remove-Item (Join-Path $dist "FluxDM*"), (Join-Path $dist "com.fluxdm.host*") -Force
foreach ($browser in "Google\Chrome", "Microsoft\Edge", "Mozilla") {
    Remove-Item "HKCU:\Software\$browser\NativeMessagingHosts\com.fluxdm.host" -ErrorAction SilentlyContinue
}

$hostManifestPath = Join-Path $dist "com.adm.host.json"
foreach ($browser in "Google\Chrome", "Microsoft\Edge") {
    $key = "HKCU:\Software\$browser\NativeMessagingHosts\com.adm.host"
    New-Item -Path $key -Force | Out-Null
    Set-Item -Path $key -Value $hostManifestPath
}
$firefoxHostPath = Join-Path $dist "com.adm.host.firefox.json"
$key = "HKCU:\Software\Mozilla\NativeMessagingHosts\com.adm.host"
New-Item -Path $key -Force | Out-Null
Set-Item -Path $key -Value $firefoxHostPath

$extensionId = ((Get-Content $hostManifestPath -Raw | ConvertFrom-Json).allowed_origins[0] -replace '^chrome-extension://|/$', '')
$addonId = (Get-Content $firefoxHostPath -Raw | ConvertFrom-Json).allowed_extensions[0]
Write-Host ""
Write-Host "ADM built to $dist"
Write-Host "Native host registered for Chrome and Edge (extension ID $extensionId) and Firefox (add-on $addonId)."
Write-Host ""
Write-Host "Next: open chrome://extensions (or edge://extensions), enable Developer mode,"
Write-Host "click 'Load unpacked' and choose $(Join-Path $root 'extension')."
Write-Host "In Firefox, open about:debugging, choose 'This Firefox', 'Load Temporary Add-on...'"
Write-Host "and pick $(Join-Path $dist 'extension-firefox\manifest.json')."
Write-Host "Then start $(Join-Path $dist 'ADM.exe')."
