# GateGuard.Tests.ps1 - the commit guard's verdict surface, pinned without a commit.
#
# The guard's contract (scripts\gate-guard.ps1): a commit needs a gate trail
# (.audit\gates.tsv) whose last row is green in build/test/format, whose sha
# equals the current HEAD, and whose timestamp is inside the age budget.
# Every case runs the guard in a child powershell process: the guard
# terminates with exit N, which would kill the Pester process if dot-sourced.
BeforeAll {
    $script:Guard = Join-Path (Split-Path -Parent $PSScriptRoot) 'gate-guard.ps1'
    $script:RepoHead = (& git rev-parse --short HEAD).Trim()
    $script:Header = @('ts', 'sha', 'label', 'build', 'warnings', 'errors', 'test', 'passed', 'failed', 'format') -join "`t"

    function New-TsRow {
        param([string]$Sha, [string]$Build, [string]$Test, [string]$Format, [TimeSpan]$Age)
        $ts = [DateTime]::UtcNow.Add($Age).ToString('yyyy-MM-ddTHH:mm:ssZ')
        return (@($ts, $Sha, 't', $Build, '0', '0', $Test, '5', '0', $Format) -join "`t")
    }

    function New-GatesFile {
        param([string[]]$Rows)
        $p = Join-Path $env:TEMP ('wmd-ptest-gg-' + [guid]::NewGuid().ToString('N') + '.tsv')
        $text = $script:Header + "`r`n" + ($Rows -join "`r`n") + "`r`n"
        [System.IO.File]::WriteAllText($p, $text)
        return $p
    }

    function Invoke-Guard {
        param([string]$GatesFile, [int]$MaxAge = 60, [switch]$Skip)
        $inner = "& '$script:Guard' -GatesFile '$GatesFile' -MaxAgeMinutes $MaxAge"
        if ($Skip) {
            $inner = "`$env:WMD_GATE_GUARD_SKIP='1'; " + $inner
        }
        $out = & powershell.exe -NoProfile -Command $inner 2>&1
        return @{ Code = $LASTEXITCODE; Out = ($out -join "`n") }
    }
}

Describe 'gate-guard' {
    It 'blocks when the trail is missing' {
        $r = Invoke-Guard -GatesFile (Join-Path $env:TEMP 'wmd-ptest-gg-absent.tsv')
        $r.Code | Should -Be 1
        $r.Out | Should -Match 'no gate trail'
    }

    It 'blocks when the trail has no rows' {
        $p = Join-Path $env:TEMP ('wmd-ptest-gg-' + [guid]::NewGuid().ToString('N') + '.tsv')
        [System.IO.File]::WriteAllText($p, $script:Header + "`r`n")
        $r = Invoke-Guard -GatesFile $p
        $r.Code | Should -Be 1
        $r.Out | Should -Match 'has no rows'
    }

    It 'blocks when the last row is not green' {
        $r = Invoke-Guard -GatesFile (New-GatesFile @(New-TsRow $script:RepoHead 'error' 'ok' 'ok' ([TimeSpan]::FromMinutes(-5))))
        $r.Code | Should -Be 1
        $r.Out | Should -Match 'not green'
    }

    It 'blocks when the row sha is not the current HEAD' {
        $r = Invoke-Guard -GatesFile (New-GatesFile @(New-TsRow 'deadbee' 'ok' 'ok' 'ok' ([TimeSpan]::FromMinutes(-5))))
        $r.Code | Should -Be 1
        $r.Out | Should -Match 'tree moved after the gate'
    }

    It 'blocks when the green gate is older than the budget' {
        $r = Invoke-Guard -GatesFile (New-GatesFile @(New-TsRow $script:RepoHead 'ok' 'ok' 'ok' ([TimeSpan]::FromMinutes(-70))))
        $r.Code | Should -Be 1
        $r.Out | Should -Match 'min old'
    }

    It 'allows a green gate at the current HEAD within the budget' {
        $r = Invoke-Guard -GatesFile (New-GatesFile @(New-TsRow $script:RepoHead 'ok' 'ok' 'ok' ([TimeSpan]::FromMinutes(-5))))
        $r.Code | Should -Be 0
        $r.Out | Should -Match 'commit allowed'
    }

    It 'honors the per-invocation escape even without a trail' {
        $r = Invoke-Guard -GatesFile (Join-Path $env:TEMP 'wmd-ptest-gg-absent.tsv') -Skip
        $r.Code | Should -Be 0
        $r.Out | Should -Match 'skipped'
    }
}
