# RefCheck.Tests.ps1 - the stale-reference pre-pass's verdict surface.
#
# The contract (scripts\ref-check.ps1): backtick-quoted path-like spans in the
# house docs must exist on disk (repo root or the doc's own directory), with
# the dated exempt list for runtime-data names. Exit 0 clean, exit 1 with
# every stale reference named. Each case runs in a child powershell process
# (the script terminates with exit N). Doc bodies are single-quoted on
# purpose: a double-quoted string would eat the very backticks the checker
# looks for (PS 5.1 drops a backtick before an unrecognized character).
BeforeAll {
    $script:RefCheck = Join-Path (Split-Path -Parent $PSScriptRoot) 'ref-check.ps1'

    function New-TmpDoc {
        param([string]$Text)
        $p = Join-Path $env:TEMP ('wmd-ptest-refdoc-' + [guid]::NewGuid().ToString('N') + '.md')
        [System.IO.File]::WriteAllText($p, $Text + [Environment]::NewLine)
        return $p
    }

    function Invoke-RefCheck {
        param([string[]]$Files, [string[]]$Exempt)
        $inner = "& '$script:RefCheck' -Files @(" + (($Files | ForEach-Object { "'" + $_ + "'" }) -join ',') + ")"
        if ($Exempt -and $Exempt.Count -gt 0) {
            $inner += ' -Exempt @(' + (($Exempt | ForEach-Object { "'" + $_ + "'" }) -join ',') + ')'
        }
        $out = & powershell.exe -NoProfile -Command $inner 2>&1
        return @{ Code = $LASTEXITCODE; Out = ($out -join "`n") }
    }
}

Describe 'ref-check' {
    It 'passes a doc whose backticked references all exist' {
        $d = New-TmpDoc 'See `scripts/gate-guard.ps1` for the guard.'
        $r = Invoke-RefCheck -Files @($d)
        $r.Code | Should -Be 0
        $r.Out | Should -Match 'clean'
    }

    It 'fails a doc with a stale repo reference' {
        $d = New-TmpDoc 'See `scripts/definitely-missing-xyz.ps1` here.'
        $r = Invoke-RefCheck -Files @($d)
        $r.Code | Should -Be 1
        $r.Out | Should -Match 'definitely-missing-xyz.ps1'
    }

    It 'honors the default exempt list (runtime data files)' {
        $d = New-TmpDoc 'State lives in `profile.json`.'
        $r = Invoke-RefCheck -Files @($d)
        $r.Code | Should -Be 0
    }

    It 'honors an explicit -Exempt span' {
        $d = New-TmpDoc 'A `made-up-token-abc.md` is exempt here.'
        $r = Invoke-RefCheck -Files @($d) -Exempt @('made-up-token-abc.md')
        $r.Code | Should -Be 0
    }

    It 'ignores non-path backticks, URLs, and env-var spans' {
        $d = New-TmpDoc 'Types like `Foo.Bar`, links `http://x.example/a.md`, and `%LOCALAPPDATA%\x` pass.'
        $r = Invoke-RefCheck -Files @($d)
        $r.Code | Should -Be 0
    }

    It 'reports every stale reference, not just the first' {
        $d = New-TmpDoc '`nope-one.ps1` and `nope-two.ps1`.'
        $r = Invoke-RefCheck -Files @($d)
        $r.Code | Should -Be 1
        $r.Out | Should -Match 'nope-one.ps1'
        $r.Out | Should -Match 'nope-two.ps1'
        $r.Out | Should -Match '2 stale reference'
    }
}
