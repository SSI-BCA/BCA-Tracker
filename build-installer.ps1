# BCA-Tracker installer build script.
#
# Run from the repo root:
#     .\build-installer.ps1                    # version from .csproj
#     .\build-installer.ps1 -Version 0.16.0    # override
#
# Output:  installer\dist\BCA-Tracker-Setup-<version>.exe
#
# Prereqs:
#   - .NET 10 SDK
#   - Inno Setup 6 (https://jrsoftware.org/isdl.php)

[CmdletBinding()]
param(
    [string]$Version,
    [string]$Configuration = "Release",
    [switch]$SkipNetBirdDownload
)

$ErrorActionPreference = "Stop"

# Resolve paths
$repoRoot   = Split-Path -Parent $MyInvocation.MyCommand.Path
$csprojPath = Join-Path $repoRoot "BCA-Tracker\BCA-Tracker.csproj"
$publishDir = Join-Path $repoRoot "publish"
$bundleDir  = Join-Path $repoRoot "installer\bundle"
$issPath    = Join-Path $repoRoot "installer\BCA-Tracker.iss"
$msiPath    = Join-Path $bundleDir "netbird_installer.msi"

if (-not (Test-Path $csprojPath)) {
    throw "Couldn't find BCA-Tracker.csproj at $csprojPath. Run from the repo root."
}

# Resolve version
if (-not $Version) {
    Write-Host "Reading version from $csprojPath..." -ForegroundColor Cyan
    [xml]$csproj = Get-Content $csprojPath
    # Try <Version> first (manual override), then <VersionPrefix>+<VersionSuffix>
    # which is the convention used by .NET SDK for split prefix/suffix.
    $verNode = $csproj.Project.PropertyGroup.Version
    if (-not $verNode) {
        $prefix = $csproj.Project.PropertyGroup.VersionPrefix
        $suffix = $csproj.Project.PropertyGroup.VersionSuffix
        if ($prefix) {
            if ($suffix) {
                $verNode = "$prefix-$suffix"
            } else {
                $verNode = $prefix
            }
        }
    }
    if (-not $verNode) {
        $verNode = $csproj.Project.PropertyGroup.AssemblyVersion
    }
    if (-not $verNode) {
        $Version = "0.0.0"
        Write-Warning "No version info in csproj - defaulting to $Version."
    } else {
        $Version = ($verNode -as [string]).Trim()
    }
}
Write-Host "Building installer for version $Version" -ForegroundColor Green

# 1. Publish the tracker
Write-Host ""
Write-Host "[1/3] Publishing tracker..." -ForegroundColor Cyan
if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }

dotnet publish $csprojPath `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=embedded `
    -o $publishDir

if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

$exePath = Join-Path $publishDir "BCA-Tracker.exe"
if (-not (Test-Path $exePath)) {
    throw "Expected $exePath after publish, but it isn't there."
}
$exeSize = (Get-Item $exePath).Length / 1MB
Write-Host ("    BCA-Tracker.exe built ({0:N1} MB)" -f $exeSize) -ForegroundColor Green

# 2. Fetch the NetBird MSI
if (-not (Test-Path $bundleDir)) {
    New-Item -ItemType Directory -Path $bundleDir | Out-Null
}

if ($SkipNetBirdDownload -and (Test-Path $msiPath)) {
    Write-Host ""
    Write-Host "[2/3] Skipping NetBird download (using cached MSI)." -ForegroundColor Yellow
} else {
    Write-Host ""
    Write-Host "[2/3] Fetching latest NetBird MSI..." -ForegroundColor Cyan

    # IMPORTANT: pkgs.netbird.io/windows/x64 serves the *.exe* (NSIS)
    # installer, NOT the MSI. Passing it to msiexec /i fails with
    # "This installation package could not be opened" because Windows
    # Installer rejects EXE files. We want the actual .msi.
    #
    # Resolution strategy:
    #   1. Ask GitHub for the latest release tag.
    #   2. Build the canonical asset URL:
    #        netbird_installer_<version>_windows_amd64.msi
    #   3. Fall back to the direct package repository URL if the GitHub
    #      API is unreachable.
    $msiUrl     = $null
    $msiVersion = $null
    try {
        Write-Host "    Querying GitHub for the latest NetBird release..." -ForegroundColor DarkGray
        $rel = Invoke-RestMethod `
            -Uri "https://api.github.com/repos/netbirdio/netbird/releases/latest" `
            -UseBasicParsing `
            -Headers @{ "User-Agent" = "BCA-Tracker-build" }
        $tag = $rel.tag_name                 # e.g. "v0.71.2"
        $msiVersion = $tag.TrimStart("v")    # e.g. "0.71.2"
        $msiAsset = $rel.assets | Where-Object {
            $_.name -like "netbird_installer_*_windows_amd64.msi"
        } | Select-Object -First 1
        if ($msiAsset) {
            $msiUrl = $msiAsset.browser_download_url
            Write-Host ("    Using $($msiAsset.name) (v$msiVersion)") -ForegroundColor DarkGray
        } else {
            Write-Warning "GitHub release $tag has no MSI asset; falling back to direct URL."
        }
    } catch {
        Write-Warning "GitHub API unavailable ($_); falling back to direct URL."
    }

    if (-not $msiUrl) {
        # The package server hosts a rolling 'latest' MSI here.
        # Not version-pinned, but mirrors GitHub and is what NetBird
        # docs recommend for unattended fetches.
        $msiUrl = "https://pkgs.netbird.io/windows/msi/x64/netbird_installer_windows_amd64.msi"
        Write-Host "    Using direct package repository URL" -ForegroundColor DarkGray
    }

    Write-Host "    Downloading from $msiUrl" -ForegroundColor DarkGray
    try {
        Invoke-WebRequest -Uri $msiUrl -OutFile $msiPath -UseBasicParsing
    } catch {
        throw "Failed to download NetBird MSI from ${msiUrl}: $_"
    }

    # Sanity-check the download. If we got HTML (a redirect page,
    # error page, or login form), msiexec would fail at install time
    # with the inscrutable "package could not be opened" dialog.
    # Catching it here gives the build a clear error instead.
    $msiBytes = [System.IO.File]::ReadAllBytes($msiPath)
    if ($msiBytes.Length -lt 1024) {
        throw "Downloaded MSI is only $($msiBytes.Length) bytes - not a real installer. URL: $msiUrl"
    }
    # MSI files are OLE compound documents; the magic is D0 CF 11 E0 A1 B1 1A E1.
    if ($msiBytes[0] -ne 0xD0 -or $msiBytes[1] -ne 0xCF -or `
        $msiBytes[2] -ne 0x11 -or $msiBytes[3] -ne 0xE0) {
        $head = ($msiBytes[0..15] | ForEach-Object { "{0:X2}" -f $_ }) -join " "
        throw "Downloaded file isn't a valid MSI (first 16 bytes: $head). URL: $msiUrl"
    }
    $msiSize = (Get-Item $msiPath).Length / 1MB
    Write-Host ("    netbird_installer.msi downloaded ({0:N1} MB)" -f $msiSize) -ForegroundColor Green
}

# 3. Find and run Inno Setup
Write-Host ""
Write-Host "[3/3] Compiling installer with Inno Setup..." -ForegroundColor Cyan

$isccCandidates = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 5\ISCC.exe"
)
$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    throw "ISCC.exe not found. Install Inno Setup 6 from https://jrsoftware.org/isdl.php and re-run."
}
Write-Host "    Using $iscc" -ForegroundColor DarkGray

& $iscc /DAppVersion=$Version $issPath
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compile failed." }

$setupExe = Join-Path $repoRoot "installer\dist\BCA-Tracker-Setup-$Version.exe"
if (Test-Path $setupExe) {
    $setupSize = (Get-Item $setupExe).Length / 1MB
    Write-Host ""
    Write-Host ("OK: Built {0} ({1:N1} MB)" -f $setupExe, $setupSize) -ForegroundColor Green
} else {
    Write-Warning "Expected output at $setupExe wasn't found; check Inno Setup logs above."
}
