# ScanStagedUserData.Tests.ps1 - the staged user-data scan pinned against a scratch repo.
#
# The contract (scripts\scan-staged-userdata.ps1): a commit is blocked when any
# STAGED path's basename is a known user-data file (profile.json, the theme/
# settings JSON, the CalDAV credential bin, media artwork) or matches the
# display-log prefix (display_device.log and its .N rotated copies). Matching is
# on the basename only, so a copy dropped anywhere in the tree is caught. Legit
# repo files (source, synthetic test fixtures, the per-instance weather cache)
# must pass. Each case drives its own scratch git repo in TEMP through a child
# powershell process (the scan exits with a code, which would kill the Pester
# process if dot-sourced); one fresh repo per case keeps the index from leaking
# a staged file between verdicts.
BeforeAll {
    $script:Scan = Join-Path (Split-Path -Parent $PSScriptRoot) 'scan-staged-userdata.ps1'
    $script:TempRepos = New-Object System.Collections.Generic.List[string]

    function New-FreshRepo {
        $r = Join-Path $env:TEMP ('wmd-ptest-ud-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $r | Out-Null
        & git -C $r init -q 2>$null
        & git -C $r config user.email 'wmd-test@example.com' 2>$null
        & git -C $r config user.name 'wmd test' 2>$null
        $script:TempRepos.Add($r)
        $script:Repo = $r
    }

    function Invoke-Scan {
        $p = Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoProfile', '-File', $script:Scan) -WorkingDirectory $script:Repo -Wait -PassThru
        return $p.ExitCode
    }

    function Stage-Text {
        param([string]$Name)
        $f = Join-Path $script:Repo $Name
        Set-Content -LiteralPath $f -Value 'x' -Encoding ascii
        & git -C $script:Repo add -- $Name
    }
}

AfterAll {
    foreach ($r in $script:TempRepos) {
        Remove-Item -Recurse -Force -LiteralPath $r -ErrorAction SilentlyContinue
    }
}

Describe 'scan-staged-userdata' {
    It 'passes with nothing staged in a fresh repo' {
        New-FreshRepo
        Invoke-Scan | Should -Be 0
    }

    It 'blocks each known user-data basename' {
        foreach ($name in @('profile.json', 'app_theme.json', 'app_settings.json', 'calendar-credentials.bin', 'media_artwork.bin')) {
            New-FreshRepo
            Stage-Text $name
            $code = Invoke-Scan
            $code | Should -Be 1 "staging $name must block"
        }
    }

    It 'blocks the display log and its rotated copies' {
        foreach ($name in @('display_device.log', 'display_device.log.1', 'display_device.log.7')) {
            New-FreshRepo
            Stage-Text $name
            $code = Invoke-Scan
            $code | Should -Be 1 "staging $name must block"
        }
    }

    It 'catches a user-data file nested in a subdirectory (basename match)' {
        New-FreshRepo
        # A copy of profile.json dropped under a folder is still personal data.
        $sub = Join-Path $script:Repo 'some/dir'
        New-Item -ItemType Directory -Path $sub | Out-Null
        $f = Join-Path $sub 'profile.json'
        Set-Content -LiteralPath $f -Value '{}' -Encoding ascii
        & git -C $script:Repo add -- 'some/dir/profile.json'
        Invoke-Scan | Should -Be 1
    }

    It 'passes legit repo files that merely resemble the names' {
        foreach ($name in @('CalendarFeed.cs', 'my_profile.json', 'profile.json.bak', 'weather_90532e81-1c56-4438-9b33-ffddcde5f661.json')) {
            New-FreshRepo
            Stage-Text $name
            $code = Invoke-Scan
            $code | Should -Be 0 "staging $name must not block"
        }
    }

    It 'blocks a user-data file modified on top of a committed base' {
        # Covers the diff --cached path (a repo that already has a HEAD).
        New-FreshRepo
        Stage-Text 'README.md'
        & git -C $script:Repo commit -q -m base
        Stage-Text 'app_theme.json'
        Invoke-Scan | Should -Be 1
    }
}
