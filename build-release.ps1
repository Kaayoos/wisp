<#
  build-release.ps1  -  Wisp release pipeline.

  Produces a Velopack installer (Wisp-win-Setup.exe) plus the update feed (.nupkg + releases json)
  in .\releaseSetup, ready to attach to the GitHub release at https://github.com/Kaayoos/wisp.
  The running app's Velopack auto-updater reads that feed to keep users on the newest version.

  Steps:
    1. Resolve the version from <Version> in Wisp.csproj (single source of truth; bump it per release).
    2. dotnet publish  - Release, win-x64, self-contained, NOT single-file, NOT ReadyToRun.
         * not single-file: Velopack needs a normal file layout to manage + delta-update the app.
         * not ReadyToRun : ships plain IL, so the published assemblies stay easy to verify against
                            this source (R2R would bake in CPU-specific native code).
    3. vpk pack  -> .\releaseSetup.

  Usage (from the repo root):
    ./build-release.ps1                 # version from csproj
    ./build-release.ps1 -Version 1.2.0  # override version

  First run on a fresh machine: the script restores the pinned tool (vpk) from
  .config/dotnet-tools.json automatically.
#>
[CmdletBinding()]
param(
    [string]$Version,
    # Wipe the output folder first. Use when RE-building a version you already packed (vpk refuses to
    # overwrite an existing release). Leave it OFF for normal sequential releases so vpk can read the
    # previous .nupkg and generate a small delta update against it.
    [switch]$Clean,
    [string]$OutputDir = "releaseSetup"
)

$ErrorActionPreference = "Stop"
$repo = $PSScriptRoot
Set-Location $repo

$PublishDir = "publish"

function Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }

# 1. Version: from csproj unless explicitly passed.
if (-not $Version) {
    $csproj = Join-Path $repo "Wisp.csproj"
    $match = Select-String -Path $csproj -Pattern '<Version>([0-9]+(?:\.[0-9]+){1,3})</Version>' | Select-Object -First 1
    if (-not $match) { throw "Could not find <Version> in Wisp.csproj. Pass -Version x.y.z explicitly." }
    $Version = $match.Matches[0].Groups[1].Value
}
Step "Wisp release $Version"

# 2. Restore the pinned build tool (vpk) from the manifest.
Step "Restoring build tools (vpk)..."
dotnet tool restore
if ($LASTEXITCODE -ne 0) { throw "dotnet tool restore failed." }

# 3. Clean + publish.
$pub = Join-Path $repo $PublishDir
if (Test-Path $pub) { Remove-Item $pub -Recurse -Force }
Step "Publishing (Release, win-x64, self-contained)..."
dotnet publish Wisp.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -p:PublishReadyToRun=false -p:Version=$Version `
    -o $pub
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

# 4. Pack the Velopack installer + update feed into the release folder.
$out = Join-Path $repo $OutputDir
New-Item -ItemType Directory -Force -Path $out | Out-Null
if ($Clean) {
    Step "Cleaning $OutputDir (re-build of an existing version)..."
    Get-ChildItem $out -Force | Remove-Item -Recurse -Force
}
Step "Packing Velopack installer into $OutputDir ..."
dotnet vpk pack `
    --packId Wisp `
    --packVersion $Version `
    --packDir $pub `
    --mainExe Wisp.exe `
    --packTitle "Wisp" `
    --packAuthors "MinimalPulse" `
    --icon "img\Wisp.ico" `
    --runtime win-x64 `
    --outputDir $out
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed." }

Step "Done. Installer + update feed in: $out"
Get-ChildItem $out | Select-Object Name, @{N='MB';E={[math]::Round($_.Length/1MB,1)}} | Format-Table -AutoSize
