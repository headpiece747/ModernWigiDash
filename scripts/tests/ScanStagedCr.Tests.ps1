# ScanStagedCr.Tests.ps1 - the staged-blob lone-CR scan pinned against a scratch repo.
#
# The contract (scripts\scan-staged-cr.ps1): a staged TEXT blob (no 0x00 byte
# in the first 8000 bytes, git's own binary heuristic) must not carry a CR
# not followed by LF, or the commit is blocked. The scan runs from the
# working directory's repo root, so each case drives its own scratch git
# repo in TEMP through a child powershell process (the scan exits with a
# code, which would kill the Pester process if dot-sourced). One fresh repo
# per case: the index is cumulative within a repo, so a shared repo would
# leak a staged file from an earlier case into a later verdict.
BeforeAll {
    $script:Scan = Join-Path (Split-Path -Parent $PSScriptRoot) 'scan-staged-cr.ps1'
    $script:TempRepos = New-Object System.Collections.Generic.List[string]

    function New-FreshRepo {
        $r = Join-Path $env:TEMP ('wmd-ptest-crs-' + [guid]::NewGuid().ToString('N'))
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

    function Stage-Bytes {
        param([string]$Name, [byte[]]$Bytes)
        $f = Join-Path $script:Repo $Name
        [System.IO.File]::WriteAllBytes($f, $Bytes)
        & git -C $script:Repo add -- $Name
    }
}

AfterAll {
    foreach ($r in $script:TempRepos) {
        Remove-Item -Recurse -Force -LiteralPath $r -ErrorAction SilentlyContinue
    }
}

Describe 'scan-staged-cr' {
    It 'passes with nothing staged in a fresh repo' {
        New-FreshRepo
        Invoke-Scan | Should -Be 0
    }

    It 'passes a clean CRLF text file' {
        New-FreshRepo
        # "abc" CRLF "def" CRLF
        Stage-Bytes 'clean.txt' ([byte[]](0x61, 0x62, 0x63, 0x0D, 0x0A, 0x64, 0x65, 0x66, 0x0D, 0x0A))
        Invoke-Scan | Should -Be 0
    }

    It 'blocks a text file with a lone CR' {
        New-FreshRepo
        # "abc" CR "def" CRLF: the CR at byte 3 is not followed by LF.
        Stage-Bytes 'lone-cr.txt' ([byte[]](0x61, 0x62, 0x63, 0x0D, 0x64, 0x65, 0x66, 0x0D, 0x0A))
        Invoke-Scan | Should -Be 1
    }

    It 'skips a binary file (git NUL heuristic) even with a lone CR' {
        New-FreshRepo
        # NUL at byte 0, lone CR at byte 2: git treats the blob as binary.
        Stage-Bytes 'binary.bin' ([byte[]](0x00, 0x01, 0x0D, 0x02, 0x0A, 0x03))
        Invoke-Scan | Should -Be 0
    }

    It 'blocks a lone CR in a file modified on top of a committed base' {
        # Covers the diff --cached path (a repo that already has a HEAD):
        # commit a clean base, then stage a new file with the lone CR.
        New-FreshRepo
        Stage-Bytes 'clean.txt' ([byte[]](0x61, 0x62, 0x63, 0x0D, 0x0A, 0x64, 0x65, 0x66, 0x0D, 0x0A))
        & git -C $script:Repo commit -q -m base
        Stage-Bytes 'lone-cr.txt' ([byte[]](0x61, 0x62, 0x0D, 0x64, 0x65, 0x66, 0x0D, 0x0A))
        Invoke-Scan | Should -Be 1
    }
}
