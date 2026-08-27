[CmdletBinding()]
param(
    [switch]$SkipTelemetry,
    [string]$Version = "",
    [string]$LhsVersion = "",
    [string]$PresentMonVersion = "",
    [string]$OutputZip = ""
)

$ErrorActionPreference = "Stop"

# Versioned artifact name (the release standard): the zip is
# ModernWigiDash-v<semver>-win-x64.zip so every release has a distinct,
# immutable, sortable filename. When no -Version is given (local ad-hoc
# builds), fall back to the unversioned name.
if ([string]::IsNullOrWhiteSpace($OutputZip)) {
    $OutputZip = if ([string]::IsNullOrWhiteSpace($Version)) { "ModernWigiDash-win-x64.zip" } else { "ModernWigiDash-v$Version-win-x64.zip" }
}

$Root       = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$ReleaseDir = Join-Path $Root "release"
$CacheDir   = Join-Path $Root "scripts\.release-cache"
$Staging    = Join-Path ([System.IO.Path]::GetTempPath()) "wmd-release-staging"
$ZipDir     = Join-Path $Staging "ModernWigiDash-win-x64"
$ZipPath    = Join-Path $Root $OutputZip

# --- 0. Auto-resolve upstream telemetry versions (when pins are empty) ---
# Runs before the download map below so the map uses the resolved versions.
# Gated behind -SkipTelemetry: that switch is the documented offline dev path,
# and the resolved versions are only consumed by the telemetry bundle block.
function Get-LatestReleaseVersion([string]$Repo) {
    $json = & curl.exe -f -L -sS "https://api.github.com/repos/$Repo/releases/latest"
    if ($LASTEXITCODE -ne 0) { throw "Could not query latest release for $Repo" }
    $release = $json | ConvertFrom-Json
    return $release.tag_name.TrimStart("v")
}

if (-not $SkipTelemetry) {
    if ([string]::IsNullOrWhiteSpace($LhsVersion)) {
        $LhsVersion = Get-LatestReleaseVersion "epinter/LibreHardwareService"
        Write-Host "LHS: auto-resolved latest v$LhsVersion"
    }
    if ([string]::IsNullOrWhiteSpace($PresentMonVersion)) {
        $PresentMonVersion = Get-LatestReleaseVersion "GameTechDev/PresentMon"
        Write-Host "PresentMon: auto-resolved latest v$PresentMonVersion"
    }
}

$LhsRelease = "https://github.com/epinter/LibreHardwareService/releases/download/v$LhsVersion"
$PmRelease  = "https://github.com/GameTechDev/PresentMon/releases/download/v$PresentMonVersion"

$Downloads = [ordered]@{
    "LibreHardwareService.msi"             = "$LhsRelease/LibreHardwareService.msi"
    "PresentMon-v$PresentMonVersion.msi"   = "$PmRelease/PresentMon-v$PresentMonVersion.msi"
    "PresentMon-$PresentMonVersion-x64.exe" = "$PmRelease/PresentMon-$PresentMonVersion-x64.exe"
    "LHS-LICENSE.txt"                      = "https://raw.githubusercontent.com/epinter/LibreHardwareService/main/LICENSE"
    "PresentMon-LICENSE.txt"               = "https://raw.githubusercontent.com/GameTechDev/PresentMon/main/LICENSE.txt"
}

function Get-Download([string]$Url, [string]$Dest) {
    if (Test-Path -LiteralPath $Dest) {
        if ((Get-Item -LiteralPath $Dest).Length -gt 0) { Write-Host "  cached: $(Split-Path $Dest -Leaf)"; return }
        Remove-Item -LiteralPath $Dest -Force
    }
    Write-Host "  downloading $(Split-Path $Dest -Leaf)..."
    & curl.exe -f -L -sS -o "$Dest" "$Url"
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $Dest)) {
        if (Test-Path -LiteralPath $Dest) { Remove-Item -LiteralPath $Dest -Force }
        throw "Download failed: $Url"
    }
}

# --- 1. Publish the app (single-file, self-contained, R2R, no PDBs) ---
Write-Host "Publishing ModernWigiDash.App (single-file, self-contained)..."
$publishOut = Join-Path $Staging "publish"
if (Test-Path $Staging) { Remove-Item $Staging -Recurse -Force }
New-Item -ItemType Directory -Path $Staging -Force | Out-Null
$publishArgs = @(
    "-c", "Release", "-r", "win-x64", "--self-contained", "-o", $publishOut,
    "-p:PublishSingleFile=true", "-p:PublishReadyToRun=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true", "-p:EnableCompressionInSingleFile=true",
    "-p:DebugType=None", "-p:DebugSymbols=false"
)
if (-not [string]::IsNullOrWhiteSpace($Version)) {
    $publishArgs += "-p:InformationalVersion=$Version"
    # The csproj pins <Version>0.0.0</Version> so dev builds parse as
    # unversioned (AppVersion -> IsDevBuild -> updater off); that also pins
    # the Windows FileVersion resource to 0.0.0. Stamp it explicitly so
    # release exes show the real version in Explorer's Details tab.
    $publishArgs += "-p:FileVersion=$Version"
}
& dotnet publish (Join-Path $Root "ModernWigiDash.App\ModernWigiDash.App.csproj") @publishArgs | Out-Null
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
if (-not [string]::IsNullOrWhiteSpace($Version)) {
    # Enforce the stamp: a missing FileVersion (csproj 0.0.0) shipped once
    # as a silent regression. Fail the build instead of the eyeball pass.
    $fileVersion = (Get-Item (Join-Path $publishOut "ModernWigiDash.App.exe")).VersionInfo.FileVersion
    if ($fileVersion -ne $Version) { throw "Version stamp check failed: exe FileVersion is '$fileVersion', expected '$Version'" }
}

# --- 2. Assemble the zip root ---
New-Item -ItemType Directory -Path $ZipDir -Force | Out-Null

# App payload: the single-file exe + the Resources folder (fonts, theme, icons)
Copy-Item (Join-Path $publishOut "ModernWigiDash.App.exe") $ZipDir
Copy-Item (Join-Path $publishOut "Resources") (Join-Path $ZipDir "Resources") -Recurse

# --- 3. Telemetry bundle (unless skipped) ---
if (-not $SkipTelemetry) {
    Write-Host "Downloading telemetry installers + licenses..."
    New-Item -ItemType Directory -Path $CacheDir -Force | Out-Null
    $lhsDir = Join-Path $ZipDir "telemetry\LibreHardwareService"
    $pmDir  = Join-Path $ZipDir "telemetry\PresentMon"
    $licDir = Join-Path $ZipDir "telemetry\third-party-licenses"
    New-Item -ItemType Directory -Path $lhsDir, $pmDir, $licDir -Force | Out-Null

    foreach ($name in $Downloads.Keys) {
        $cached = Join-Path $CacheDir $name
        Get-Download -Url $Downloads[$name] -Dest $cached
    }

    Copy-Item (Join-Path $CacheDir "LibreHardwareService.msi") $lhsDir
    Copy-Item (Join-Path $CacheDir "PresentMon-v$PresentMonVersion.msi") $pmDir
    Copy-Item (Join-Path $CacheDir "PresentMon-$PresentMonVersion-x64.exe") $pmDir
    Copy-Item (Join-Path $CacheDir "LHS-LICENSE.txt") $lhsDir
    Copy-Item (Join-Path $CacheDir "PresentMon-LICENSE.txt") $pmDir
    Copy-Item (Join-Path $CacheDir "LHS-LICENSE.txt") (Join-Path $licDir "LHS-LICENSE.txt")
    Copy-Item (Join-Path $CacheDir "PresentMon-LICENSE.txt") (Join-Path $licDir "PresentMon-LICENSE.txt")

    $notices = @"
ModernWigiDash redistributes the following third-party components:

1. LibreHardwareService  v$LhsVersion
   License: MPL-2.0
   Source:  https://github.com/epinter/LibreHardwareService
   Files:   LibreHardwareService.msi, LICENSE (in LibreHardwareService/)

2. PresentMon Service    v$PresentMonVersion
   License: MIT
   Source:  https://github.com/GameTechDev/PresentMon
   Files:   PresentMon-v$PresentMonVersion.msi, PresentMon-$PresentMonVersion-x64.exe,
            LICENSE.txt (in PresentMon/)

Full license texts are included next to each component and in this folder.
MPL-2.0 requires that corresponding source be made available; both components'
source is public at the URLs above.
"@
[System.IO.File]::WriteAllText((Join-Path $licDir "NOTICES.txt"), $notices, [System.Text.UTF8Encoding]::new($false))
[System.IO.File]::WriteAllText(
    (Join-Path $licDir "telemetry-versions.txt"),
    "LibreHardwareService=$LhsVersion`r`nPresentMon=$PresentMonVersion`r`n",
    [System.Text.UTF8Encoding]::new($false))
}

# --- 4. Templates ---
$readme = Get-Content (Join-Path $ReleaseDir "README.txt") -Raw -Encoding UTF8
if (-not [string]::IsNullOrWhiteSpace($Version)) {
    # Stamp the version into the user-facing doc so the zip is self-describing.
    # (Match on the ASCII prefix so the em-dash in the template never matters;
    # regex Replace with count 1 - the -replace operator takes only two args.)
    $readme = [regex]::Replace($readme, "ModernWigiDash ", "ModernWigiDash v$Version ", 1)
    $readme = $readme -replace "== Quick Start \(3 steps\) ==",
        "== Version ==`r`n`r`n  This is release v$Version.`r`n`r`n== Quick Start (3 steps) =="
}
[System.IO.File]::WriteAllText((Join-Path $ZipDir "README.txt"), $readme, [System.Text.UTF8Encoding]::new($false))
Copy-Item (Join-Path $ReleaseDir "setup-telemetry.bat") $ZipDir
Copy-Item (Join-Path $Root "LICENSE") (Join-Path $ZipDir "LICENSE-ModernWigiDash.txt")

# --- 5. Zip (root folder included inside) ---
Write-Host "Zipping..."
if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
Compress-Archive -Path $ZipDir -DestinationPath $ZipPath
if (-not (Test-Path $ZipPath)) { throw "Zip creation failed" }

# --- 5b. Slim app-only zip (updater payload: exe + Resources + docs) ---
# The zip's root folder is named like the full zip's ("ModernWigiDash-win-x64")
# because the in-app updater's extraction resolves the exe at that fixed path.
Write-Host "Building app-only slim zip..."
$SlimDir = Join-Path $Staging "slim"
New-Item -ItemType Directory -Path $SlimDir -Force | Out-Null
$SlimPayload = Join-Path $SlimDir "ModernWigiDash-win-x64"
New-Item -ItemType Directory -Path $SlimPayload -Force | Out-Null
Copy-Item (Join-Path $publishOut "ModernWigiDash.App.exe") $SlimPayload
Copy-Item (Join-Path $publishOut "Resources") (Join-Path $SlimPayload "Resources") -Recurse
[System.IO.File]::WriteAllText((Join-Path $SlimPayload "README.txt"), $readme, [System.Text.UTF8Encoding]::new($false))
Copy-Item (Join-Path $Root "LICENSE") (Join-Path $SlimPayload "LICENSE-ModernWigiDash.txt")

$SlimZipPath = if ([string]::IsNullOrWhiteSpace($Version)) { Join-Path $Root "ModernWigiDash-app-only-win-x64.zip" } else { Join-Path $Root "ModernWigiDash-v$Version-app-only.zip" }
if (Test-Path $SlimZipPath) { Remove-Item $SlimZipPath -Force }
Compress-Archive -Path $SlimPayload -DestinationPath $SlimZipPath
if (-not (Test-Path $SlimZipPath)) { throw "Slim zip creation failed" }
Write-Host ""
Write-Host "Built $SlimZipPath"

# --- 6. Contents manifest + size ---
Write-Host ""
Write-Host "Built $ZipPath"
Get-ChildItem $ZipDir -Recurse -File | ForEach-Object {
    "{0,12:N0}  {1}" -f $_.Length, $_.FullName.Substring($ZipDir.Length + 1)
}
Write-Host ""
Write-Host ("Zip size: {0:N1} MB" -f ((Get-Item $ZipPath).Length / 1MB))
