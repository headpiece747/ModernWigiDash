# scan-staged-userdata.ps1 - refuse a commit that stages a user-data file.
#
# The app's per-user state lives in %LOCALAPPDATA%\ModernWigiDash (profile.json,
# app_theme.json, app_settings.json, calendar-credentials.bin, the display log,
# weather cache, media artwork). None of it belongs in the repo: profile.json
# carries the user's widget layout AND their ICS/CalDAV feed config, and the
# credential bin holds the DPAPI-wrapped CalDAV password. A stray `git add .`
# from inside that directory (or a copy into the tree) would push personal data
# to GitHub. This guard runs from the pre-commit hook over the STAGED path list
# and blocks the commit if any known user-data basename appears, so the
# "profile and theme are default before pushing" rule is enforced by structure
# instead of by remembering to check.
#
# Matching is on the BASENAME only (the data dir has no fixed path across
# machines), so a copy dropped anywhere in the tree is caught. Legitimate repo
# files never collide with these names (the source uses CalendarFeed.cs, not
# profile.json; tests use synthetic fixtures, never a real profile.json).
#
# Exit 0: nothing staged, or no staged path matches a user-data basename.
# Exit 1: at least one staged path does (the offending paths are printed).
#
# Pure ASCII on purpose: PS 5.1 mis-parses BOM-less non-ASCII bytes.

$ErrorActionPreference = 'Stop'
$root = (& git rev-parse --show-toplevel).Trim()

# The user-data basenames. Keep this list in step with the files the app writes
# under %LOCALAPPDATA%\ModernWigiDash (see CONTEXT.md: ThemeSettings.DefaultPath,
# AppSettingsStore, CalendarCredentialStore.DefaultPath, FileLog, WeatherCacheStore).
$blockedNames = @(
    'profile.json',
    'app_theme.json',
    'app_settings.json',
    'calendar-credentials.bin',
    'media_artwork.bin'
)
# Suffix rules for the display log and its rotated copies. FileLog names them
# display_device.log, display_device.log.1, ...; the exact rotation number varies,
# so match the stable prefix instead of every numbered name. (The per-instance
# weather cache weather_<guid>.json is left alone: it holds no identity or secret
# beyond a city query, and a deliberately committed test fixture must stay legal.)
$blockedPrefixes = @('display_device.log')

$prevEap = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
$null = & git -C $root rev-parse --verify HEAD 2>$null
$haveHead = ($LASTEXITCODE -eq 0)
$ErrorActionPreference = $prevEap
if ($haveHead) {
    # Staged delta (add/copy/modify/rename/typechange; deletes carry no new
    # bytes, but a rename-in still names the target, so ACMRT covers it).
    $staged = @((& git -C $root diff --cached --name-only --diff-filter=ACMRT) | Where-Object { $_ })
} else {
    # First commit (no HEAD yet): the whole index is the delta.
    $staged = @((& git -C $root ls-files) | Where-Object { $_ })
}
if ($staged.Count -eq 0) {
    Write-Output 'user-data guard: nothing staged - ok.'
    exit 0
}

$bad = @()
foreach ($f in $staged) {
    # Normalize separators to '/' so the basename works on Windows and POSIX.
    $norm = $f -replace '\\', '/'
    $base = ($norm -split '/')[-1]
    $hit = $false
    foreach ($n in $blockedNames) {
        if ($base -eq $n) { $hit = $true; break }
    }
    if (-not $hit) {
        foreach ($s in $blockedPrefixes) {
            if ($base.StartsWith($s, [System.StringComparison]::OrdinalIgnoreCase)) { $hit = $true; break }
        }
    }
    if ($hit) { $bad += $f }
}
if ($bad.Count -eq 0) {
    Write-Output ('user-data guard: no user-data files among ' + $staged.Count + ' staged path(s) - ok.')
    exit 0
}
Write-Output 'user-data guard: BLOCKED - a personal/user-data file is staged (it must never reach the repo or GitHub):'
foreach ($b in $bad) {
    Write-Output ('  ' + $b)
}
Write-Output 'Unstage it (git restore --staged <file>). These live in %LOCALAPPDATA%\ModernWigiDash and are machine-local by design; if you copied one into the tree, delete the copy instead.'
exit 1
