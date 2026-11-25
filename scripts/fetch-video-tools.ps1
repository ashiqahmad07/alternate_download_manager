# Fetches the programs a site's video needs, yt-dlp, Deno and ffmpeg, so the installer can carry them and nobody has to
# fetch them on first use. They come from where ADM's own "Set up video downloads" fetches them (ToolSources.Official in
# src\ADM.Core\VideoTools.cs, which VideoToolsSourcesTests keeps this script in step with), and each is checked against
# the SHA-256 its publisher posts, as ADM checks it.
#   powershell -ExecutionPolicy Bypass -File scripts\fetch-video-tools.ps1 -Out <folder>
# It writes <folder>\tools\yt-dlp.exe, deno.exe and ffmpeg.exe, and <folder>\licenses\ with ffmpeg's licence and the
# versions fetched. A fetch less than a day old is used again, so building two installers in a row fetches once.
param([Parameter(Mandatory = $true)][string]$Out)
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
Add-Type -AssemblyName System.IO.Compression.FileSystem

$YtDlp = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe"
$YtDlpSums = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/SHA2-256SUMS"
$Deno = "https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip"
$DenoSum = "https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip.sha256sum"
$Ffmpeg = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-lgpl.zip"
$FfmpegSums = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/checksums.sha256"

$Out = [IO.Path]::GetFullPath($Out)
$tools = Join-Path $Out "tools"
$licenses = Join-Path $Out "licenses"
$stamp = Join-Path $Out "fetched"
$programs = "yt-dlp.exe", "deno.exe", "ffmpeg.exe"

$fresh = (Test-Path -LiteralPath $stamp) -and ((Get-Date) - (Get-Item -LiteralPath $stamp).LastWriteTime).TotalDays -lt 1 -and
    -not ($programs | Where-Object { -not (Test-Path -LiteralPath (Join-Path $tools $_)) })
if ($fresh) { Write-Host "Video tools fetched less than a day ago are in $tools."; return }

if (Test-Path -LiteralPath $Out) { Remove-Item -LiteralPath $Out -Recurse -Force }
New-Item -ItemType Directory -Path $tools, $licenses | Out-Null
$work = Join-Path $Out "download"
New-Item -ItemType Directory -Path $work | Out-Null

# The SHA-256 a sums file gives for $name, or, with no name, the first one in it: Deno's is Get-FileHash's output.
function Sum-For([string]$sums, [string]$name) {
    $text = (Invoke-WebRequest -Uri $sums -UseBasicParsing).Content
    if ($text -is [byte[]]) { $text = [Text.Encoding]::UTF8.GetString($text) }
    foreach ($line in $text -split "`n") {
        $fields = @($line.Trim() -split '\s+' | Where-Object { $_ })
        if ($name -and ($fields.Count -lt 2 -or $fields[-1].TrimStart('*') -ne $name)) { continue }
        if ($line -match '\b[0-9a-fA-F]{64}\b') { return $Matches[0].ToLowerInvariant() }
    }
    throw "The published SHA-256 at $sums for '$name' could not be found."
}

function Fetch([string]$address, [string]$file, [string]$expected) {
    Write-Host "Fetching $address"
    Invoke-WebRequest -Uri $address -OutFile $file -UseBasicParsing
    $actual = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expected) { throw "$address did not match its published SHA-256 ($actual, expected $expected)." }
}

# The first entry named $name, wherever it is in the archive.
function Extract([string]$zip, [string]$name, [string]$to) {
    $archive = [IO.Compression.ZipFile]::OpenRead($zip)
    try {
        $entry = $archive.Entries | Where-Object { $_.Name -ieq $name } | Select-Object -First 1
        if (-not $entry) { throw "$zip has no $name in it." }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $to, $true)
    }
    finally { $archive.Dispose() }
}

Fetch $YtDlp (Join-Path $tools "yt-dlp.exe") (Sum-For $YtDlpSums "yt-dlp.exe")

$denoZip = Join-Path $work "deno.zip"
Fetch $Deno $denoZip (Sum-For $DenoSum "")
Extract $denoZip "deno.exe" (Join-Path $tools "deno.exe")

$ffmpegZip = Join-Path $work "ffmpeg.zip"
Fetch $Ffmpeg $ffmpegZip (Sum-For $FfmpegSums ([IO.Path]::GetFileName($Ffmpeg)))
Extract $ffmpegZip "ffmpeg.exe" (Join-Path $tools "ffmpeg.exe")
Extract $ffmpegZip "LICENSE.txt" (Join-Path $licenses "ffmpeg-LICENSE.txt")

Remove-Item -LiteralPath $work -Recurse -Force

# Each runs, and says which version it is: ffmpeg's names the FFmpeg commit its source is at, as its LGPL asks.
function First-Line([string]$program, [string]$argument) {
    $ErrorActionPreference = "Continue"
    $output = @(& (Join-Path $tools $program) $argument 2>$null)
    if ($LASTEXITCODE -ne 0 -or -not $output) { throw "$program did not run." }
    $output[0]
}
$versions = @(
    "The video tools ADM's installer carries, put in %LOCALAPPDATA%\ADM\tools where none is there yet.",
    "",
    "yt-dlp $(First-Line 'yt-dlp.exe' '--version')",
    "  from $YtDlp",
    "  The Unlicense; source at https://github.com/yt-dlp/yt-dlp",
    "$(First-Line 'deno.exe' '--version')",
    "  from $Deno",
    "  MIT licence (in THIRD-PARTY-NOTICES.txt); source at https://github.com/denoland/deno",
    "$(First-Line 'ffmpeg.exe' '-version')",
    "  from $Ffmpeg",
    "  GNU LGPL (ffmpeg-LICENSE.txt); source at https://github.com/FFmpeg/FFmpeg, at the commit in the version above,",
    "  built by https://github.com/BtbN/FFmpeg-Builds"
)
[IO.File]::WriteAllLines((Join-Path $licenses "video-tools.txt"), $versions)
$versions[2..$versions.Count] | Where-Object { $_ -notmatch '^\s' } | ForEach-Object { Write-Host $_ }

New-Item -ItemType File -Path $stamp | Out-Null
