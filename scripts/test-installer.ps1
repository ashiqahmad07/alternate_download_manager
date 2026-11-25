# Installs, upgrades and uninstalls ADM with two builds of its setup, and checks each step (Phase 11, Task 3):
#   powershell -ExecutionPolicy Bypass -File scripts\test-installer.ps1 -Old dist\installer\ADM-Setup-0.0.1.exe -New dist\installer\ADM-Setup-0.1.0.exe
# It installs into a temporary folder whose name has a space and a non-ASCII letter, as a user name like Jose Garcia's
# would. It refuses to start where ADM's state or an installed ADM is found, puts back the browser registration and the
# startup entry it found, and deletes only the files it made, so a developer's machine is left as it was.
param(
    [Parameter(Mandatory = $true)][string]$Old,
    [Parameter(Mandatory = $true)][string]$New
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

$UninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{4A719596-AE3E-4AF6-A276-515B2060EBF0}_is1"
$BrowserKeys = [ordered]@{
    Chrome  = "HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.adm.host"
    Edge    = "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\com.adm.host"
    Firefox = "HKCU:\Software\Mozilla\NativeMessagingHosts\com.adm.host"
}
$RunKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$StateDir = Join-Path $env:LOCALAPPDATA "ADM"
$State = Join-Path $StateDir "state.json"
$Tools = Join-Path $StateDir "tools"
$Shortcut = Join-Path ([Environment]::GetFolderPath("Programs")) "Alternate Download Manager.lnk"
$GuideShortcut = Join-Path ([Environment]::GetFolderPath("Programs")) "Add ADM to your browser.lnk"
$TestRoot = Join-Path ([IO.Path]::GetTempPath()) "adm-installer-test"
$Dir = Join-Path $TestRoot ("Programs " + [char]0x00DC + "\ADM")

function VersionOf([string]$setup) {
    if ((Split-Path $setup -Leaf) -notmatch '^ADM-Setup-(\d+\.\d+\.\d+)\.exe$') { throw "$setup is not named ADM-Setup-<version>.exe." }
    $Matches[1]
}
$OldVersion = VersionOf $Old
$NewVersion = VersionOf $New
foreach ($setup in $Old, $New) { if (-not (Test-Path -LiteralPath $setup)) { throw "$setup was not found." } }

if (Test-Path -LiteralPath $State) { throw "ADM's state is at $State. Run this where ADM hasn't run, or move the state away first." }
# Setup leaves tools that are there alone, and the uninstall takes the folder: someone's own would be lost.
if (Test-Path -LiteralPath $Tools) { throw "ADM's video tools are at $Tools. Run this where ADM hasn't run, or move them away first." }
if (Test-Path $UninstallKey) { throw "ADM is installed. Uninstall it first." }
if (Get-Process ADM -ErrorAction SilentlyContinue) { throw "ADM is running. Close it first." }

# The extension's ID comes from the public key in its manifest; the add-on's id is written in its own.
$extension = Get-Content (Join-Path $root "extension\manifest.json") -Raw | ConvertFrom-Json
$hash = [Security.Cryptography.SHA256]::Create().ComputeHash([Convert]::FromBase64String($extension.key))
$ExtensionId = -join ($hash[0..15] | ForEach-Object { "{0:x2}" -f $_ } | ForEach-Object { $_.ToCharArray() } |
    ForEach-Object { [char](97 + [Convert]::ToInt32([string]$_, 16)) })
$AddonId = (Get-Content (Join-Path $root "extension\manifest.firefox.json") -Raw | ConvertFrom-Json).browser_specific_settings.gecko.id

$script:failed = 0
function Check([string]$name, [bool]$ok, [string]$detail = "") {
    if ($ok) { Write-Host "PASS $name" } else { $script:failed++; Write-Host "FAIL $name $detail" }
}

function Wait-For([scriptblock]$condition, [int]$seconds) {
    $end = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $end) { if (& $condition) { return $true }; Start-Sleep -Milliseconds 250 }
    return [bool](& $condition)
}

function Run-Setup([string]$setup) {
    $log = Join-Path $TestRoot ("setup-" + (Split-Path $setup -Leaf) + ".log")
    $arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /DIR=`"$Dir`" /LOG=`"$log`""
    (Start-Process -FilePath $setup -ArgumentList $arguments -Wait -PassThru).ExitCode
}

function DefaultValue([string]$key) {
    $item = Get-Item -LiteralPath $key -ErrorAction SilentlyContinue
    if ($item) { $item.GetValue("") } else { $null }
}

function RunValue {
    $values = Get-ItemProperty -LiteralPath $RunKey -ErrorAction SilentlyContinue
    if ($values -and ($values.PSObject.Properties.Name -contains "ADM")) { $values.ADM } else { $null }
}

# The first line a program prints when asked its version, or $null when it isn't there or doesn't run.
function Tool-Version([string]$program, [string]$argument) {
    if (-not (Test-Path -LiteralPath $program)) { return $null }
    $ErrorActionPreference = "Continue"
    $output = @(& $program $argument 2>$null)
    if ($LASTEXITCODE -ne 0 -or -not $output) { return $null }
    $output[0]
}

function InstalledVersion { (Get-ItemProperty -LiteralPath $UninstallKey -ErrorAction SilentlyContinue).DisplayVersion }
function FileVersion { (Get-Item -LiteralPath (Join-Path $Dir "ADM.exe")).VersionInfo.FileVersion }

# One message to the native host, framed as a browser frames it: a 32-bit length, then UTF-8 JSON. It goes through
# files, as Process.StandardInput in Windows PowerShell writes the console encoding's byte order mark ahead of it.
function Ask-Host([string]$exe, [string]$json) {
    $request = Join-Path $TestRoot "host-request.bin"
    $reply = Join-Path $TestRoot "host-reply.bin"
    $body = [Text.Encoding]::UTF8.GetBytes($json)
    [IO.File]::WriteAllBytes($request, [byte[]]([BitConverter]::GetBytes([int]$body.Length) + $body))
    $process = Start-Process -FilePath $exe -ArgumentList "chrome-extension://$ExtensionId/" -NoNewWindow -PassThru `
        -RedirectStandardInput $request -RedirectStandardOutput $reply
    if (-not $process.WaitForExit(15000)) { $process.Kill(); throw "The native host gave no answer in 15 s." }
    $bytes = [IO.File]::ReadAllBytes($reply)
    if ($bytes.Length -lt 4) { throw "The native host closed its output without answering." }
    [Text.Encoding]::UTF8.GetString($bytes, 4, [BitConverter]::ToInt32($bytes, 0))
}

# Each browser's key names an installed manifest, which names the installed host, for this extension.
function Check-Registration([string]$when) {
    $nativeHost = Join-Path $Dir "ADM.NativeHost.exe"
    foreach ($browser in $BrowserKeys.Keys) {
        $manifestPath = DefaultValue $BrowserKeys[$browser]
        $expected = Join-Path $Dir $(if ($browser -eq "Firefox") { "com.adm.host.firefox.json" } else { "com.adm.host.json" })
        Check "$when`: $browser's key names the installed manifest" ($manifestPath -eq $expected) "(it names '$manifestPath')"
        if (-not ($manifestPath -and (Test-Path -LiteralPath $manifestPath))) { continue }
        $manifest = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
        $resolved = [IO.Path]::GetFullPath((Join-Path (Split-Path $manifestPath) $manifest.path))
        Check "$when`: $browser's manifest names the installed host" (($resolved -eq $nativeHost) -and (Test-Path -LiteralPath $resolved)) "(it names '$($manifest.path)')"
        Check "$when`: $browser's manifest is com.adm.host, over stdio" (($manifest.name -eq "com.adm.host") -and ($manifest.type -eq "stdio"))
        if ($browser -eq "Firefox") {
            Check "$when`: Firefox's manifest allows the add-on $AddonId" (@($manifest.allowed_extensions) -contains $AddonId)
        }
        else {
            Check "$when`: $browser's manifest allows the extension $ExtensionId" (@($manifest.allowed_origins) -contains "chrome-extension://$ExtensionId/")
        }
    }
}

# What was here before, to put back.
$keysBefore = @{}
foreach ($browser in $BrowserKeys.Keys) { $keysBefore[$browser] = DefaultValue $BrowserKeys[$browser] }
$runBefore = RunValue
$stateDirBefore = Test-Path -LiteralPath $StateDir
$filesBefore = @(if ($stateDirBefore) { Get-ChildItem -LiteralPath $StateDir -Recurse -File | ForEach-Object { $_.FullName } })
$adm = $null

try {
    if (Test-Path -LiteralPath $TestRoot) { Remove-Item -LiteralPath $TestRoot -Recurse -Force }
    New-Item -ItemType Directory -Path $TestRoot | Out-Null

    # 1. The old version installs.
    $code = Run-Setup $Old
    Check "the old setup ($OldVersion) installs" ($code -eq 0) "(exit $code)"
    foreach ($file in "ADM.exe", "ADM.NativeHost.exe", "hostfxr.dll", "extension\manifest.json", "extension-firefox\manifest.json", "LICENSE",
        "THIRD-PARTY-NOTICES.txt", "Browser setup.html", "licenses\ffmpeg-LICENSE.txt", "licenses\video-tools.txt") {
        Check "it installs $file" (Test-Path -LiteralPath (Join-Path $Dir $file))
    }
    # The video tools, where ADM looks for them, so a site's video needs no setting up; each one runs.
    $toolVersions = [ordered]@{ "yt-dlp.exe" = "--version"; "deno.exe" = "--version"; "ffmpeg.exe" = "-version" }
    foreach ($tool in $toolVersions.GetEnumerator()) {
        $version = Tool-Version (Join-Path $Tools $tool.Key) $tool.Value
        Check "it puts $($tool.Key) in ADM's tools folder, and it runs" ([bool]$version) "($version)"
    }
    $guide = (New-Object -ComObject WScript.Shell).CreateShortcut($GuideShortcut)
    Check "the Start menu has Add ADM to your browser, for the installed page" ((Test-Path -LiteralPath $GuideShortcut) -and ($guide.TargetPath -eq (Join-Path $Dir "Browser setup.html"))) "(it opens '$($guide.TargetPath)')"
    Check-Registration "installed"
    $reply = $null
    try { $reply = Ask-Host (Join-Path $Dir "ADM.NativeHost.exe") '{"type":"config"}' | ConvertFrom-Json } catch { Write-Host "  $_" }
    Check "the installed host answers a browser" ($reply -and $reply.ok -and $reply.config)
    $link = (New-Object -ComObject WScript.Shell).CreateShortcut($Shortcut)
    Check "the Start menu has Alternate Download Manager, for the installed ADM" ((Test-Path -LiteralPath $Shortcut) -and ($link.TargetPath -eq (Join-Path $Dir "ADM.exe"))) "(it starts '$($link.TargetPath)')"
    Check "Windows lists version $OldVersion" ((InstalledVersion) -eq $OldVersion) "(it lists '$(InstalledVersion)')"
    Check "ADM.exe is version $OldVersion" ((FileVersion) -eq "$OldVersion.0") "(it is '$(FileVersion)')"

    # 2. A running ADM stops the upgrade, and nothing is replaced.
    $adm = Start-Process -FilePath (Join-Path $Dir "ADM.exe") -ArgumentList "--background" -PassThru
    Wait-For { Test-Path -LiteralPath $State } 20 | Out-Null
    $code = Run-Setup $New
    Check "a running ADM stops the upgrade" ($code -ne 0) "(exit $code)"
    Check "a running ADM stops the upgrade: Windows still lists $OldVersion" ((InstalledVersion) -eq $OldVersion) "(it lists '$(InstalledVersion)')"
    Check "a running ADM stops the upgrade: ADM.exe is still $OldVersion" ((FileVersion) -eq "$OldVersion.0") "(it is '$(FileVersion)')"
    # A setup that wrongly went ahead may have closed it already.
    Stop-Process -Id $adm.Id -Force -ErrorAction SilentlyContinue
    $adm.WaitForExit()
    $adm = $null
    if (-not (Test-Path -LiteralPath $State)) { [IO.File]::WriteAllText($State, '{"written by":"test-installer"}') }
    $stateHash = (Get-FileHash -LiteralPath $State).Hash
    # ADM keeps its own yt-dlp up to date, so a yt-dlp that is there stays; a tool that has gone comes back.
    $ytDlp = Join-Path $Tools "yt-dlp.exe"
    [IO.File]::WriteAllText($ytDlp, "a yt-dlp ADM updated")
    $ytDlpHash = (Get-FileHash -LiteralPath $ytDlp).Hash
    Remove-Item -LiteralPath (Join-Path $Tools "ffmpeg.exe")

    # 3. The new version installs over it, and the download list is kept.
    $code = Run-Setup $New
    Check "the new setup ($NewVersion) installs over the old" ($code -eq 0) "(exit $code)"
    Check "Windows lists version $NewVersion" ((InstalledVersion) -eq $NewVersion) "(it lists '$(InstalledVersion)')"
    Check "ADM.exe is version $NewVersion" ((FileVersion) -eq "$NewVersion.0") "(it is '$(FileVersion)')"
    Check "the upgrade keeps state.json byte for byte" ((Get-FileHash -LiteralPath $State).Hash -eq $stateHash)
    Check "the upgrade leaves the yt-dlp ADM updated" ((Test-Path -LiteralPath $ytDlp) -and ((Get-FileHash -LiteralPath $ytDlp).Hash -eq $ytDlpHash))
    Check "the upgrade puts back a missing ffmpeg.exe" ([bool](Tool-Version (Join-Path $Tools "ffmpeg.exe") "-version"))
    Check-Registration "upgraded"

    # 4. Uninstalling removes what was installed, the Run value Start with Windows writes included, and the video
    # tools, setup's and those fetched since; it keeps the list.
    New-ItemProperty -Path $RunKey -Name ADM -Value "`"$(Join-Path $Dir 'ADM.exe')`" --background" -PropertyType String -Force | Out-Null
    New-Item -ItemType Directory -Force -Path $Tools | Out-Null
    Set-Content -LiteralPath (Join-Path $Tools "yt-dlp.exe") -Value "a stand-in" -Encoding ascii
    $uninstaller = (Get-ItemProperty -LiteralPath $UninstallKey).UninstallString.Trim('"')
    Start-Process -FilePath $uninstaller -ArgumentList "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART" -Wait
    # The uninstaller hands over to a copy of itself and returns at once.
    Wait-For { -not (Test-Path -LiteralPath $Dir) -and -not (Test-Path $UninstallKey) } 60 | Out-Null
    Check "uninstalling removes the folder" (-not (Test-Path -LiteralPath $Dir))
    foreach ($browser in $BrowserKeys.Keys) { Check "uninstalling removes $browser's key" (-not (Test-Path $BrowserKeys[$browser])) }
    Check "uninstalling removes the Start menu entry" (-not (Test-Path -LiteralPath $Shortcut))
    Check "uninstalling removes the Start menu's Add ADM to your browser" (-not (Test-Path -LiteralPath $GuideShortcut))
    Check "uninstalling removes the entry in Settings > Apps" (-not (Test-Path $UninstallKey))
    Check "uninstalling: the Run value goes" ($null -eq (RunValue))
    Check "uninstalling removes the video tools" (-not (Test-Path -LiteralPath $Tools))
    Check "uninstalling keeps state.json byte for byte" ((Test-Path -LiteralPath $State) -and ((Get-FileHash -LiteralPath $State).Hash -eq $stateHash))
}
finally {
    if ($adm -and -not $adm.HasExited) { Stop-Process -Id $adm.Id -Force -ErrorAction SilentlyContinue; $adm.WaitForExit() }
    # An install a failure left behind.
    if (Test-Path $UninstallKey) {
        $uninstaller = (Get-ItemProperty -LiteralPath $UninstallKey).UninstallString.Trim('"')
        Start-Process -FilePath $uninstaller -ArgumentList "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART" -Wait
        Wait-For { -not (Test-Path $UninstallKey) } 60 | Out-Null
    }
    foreach ($browser in $BrowserKeys.Keys) {
        $key = $BrowserKeys[$browser]
        if ($null -eq $keysBefore[$browser]) { Remove-Item -LiteralPath $key -ErrorAction SilentlyContinue }
        else { New-Item -Path $key -Force | Out-Null; Set-Item -LiteralPath $key -Value $keysBefore[$browser] }
    }
    if ($null -eq $runBefore) { Remove-ItemProperty -Path $RunKey -Name ADM -ErrorAction SilentlyContinue }
    else { New-ItemProperty -Path $RunKey -Name ADM -Value $runBefore -PropertyType String -Force | Out-Null }
    if (Test-Path -LiteralPath $StateDir) {
        Get-ChildItem -LiteralPath $StateDir -Recurse -File | Where-Object { $filesBefore -notcontains $_.FullName } | Remove-Item -Force
        if (-not $stateDirBefore) { Remove-Item -LiteralPath $StateDir -Recurse -Force }
    }
    if (Test-Path -LiteralPath $TestRoot) { Remove-Item -LiteralPath $TestRoot -Recurse -Force -ErrorAction SilentlyContinue }
}

Write-Host ""
if ($script:failed) { Write-Host "$script:failed check(s) failed."; exit 1 }
Write-Host "Every check passed."
