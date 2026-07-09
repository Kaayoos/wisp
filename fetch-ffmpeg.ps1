<#
  fetch-ffmpeg.ps1  -  Downloads FFmpeg into Resources\ffmpeg.exe for local builds.

  Wisp embeds FFmpeg as a resource (see Wisp.csproj) and runs it as its own separate process; it is
  never linked into Wisp, so this download doesn't affect Wisp's own GPLv3 status (see NOTICES.txt).
  The binary itself isn't committed to git - it's around 100 MB and would bloat every clone - so run
  this once before your first build.

  Fetches gyan.dev's "essentials" Windows build (GPLv3, --enable-gpl --enable-version3, includes
  libx264/libx265 - the same build family the official Wisp releases ship). This grabs whatever the
  current release is; see NOTICES.txt for the exact version + build flags the last official Wisp
  release was actually built against.

  Usage:
    ./fetch-ffmpeg.ps1
#>
[CmdletBinding()]
param(
    [string]$Url = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip"
)

$ErrorActionPreference = "Stop"
$repo = $PSScriptRoot
$dest = Join-Path $repo "Resources\ffmpeg.exe"
$tmpZip = Join-Path $env:TEMP "wisp-ffmpeg-fetch.zip"
$tmpDir = Join-Path $env:TEMP "wisp-ffmpeg-fetch"

Write-Host "Downloading FFmpeg (essentials build) from $Url ..." -ForegroundColor Cyan
Invoke-WebRequest -Uri $Url -OutFile $tmpZip

if (Test-Path $tmpDir) { Remove-Item $tmpDir -Recurse -Force }
Expand-Archive -Path $tmpZip -DestinationPath $tmpDir -Force

$exe = Get-ChildItem -Path $tmpDir -Filter "ffmpeg.exe" -Recurse | Select-Object -First 1
if (-not $exe) { throw "Couldn't find ffmpeg.exe inside the downloaded archive." }

New-Item -ItemType Directory -Force -Path (Split-Path $dest) | Out-Null
Copy-Item $exe.FullName $dest -Force
Remove-Item $tmpZip, $tmpDir -Recurse -Force

$verLine = (& $dest -version 2>$null | Select-Object -First 1)
Write-Host "Done: $dest" -ForegroundColor Green
Write-Host "  $verLine" -ForegroundColor DarkGray
Write-Host "GPLv3 build (see NOTICES.txt); Wisp runs it as its own process and never links it." -ForegroundColor DarkGray
