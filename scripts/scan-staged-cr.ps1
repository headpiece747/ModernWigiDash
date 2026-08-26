# scan-staged-cr.ps1 - refuse a commit whose staged TEXT content carries a lone CR.
#
# git 2.55 with `text=auto` classifies a file containing a lone CR (a 0x0D
# not followed by 0x0A) as binary (i/-text w/-text), so `git add` stores raw
# bytes and the diff becomes a whole-file EOL change. This scan runs from the
# pre-commit hook over the STAGED BLOBS (the index, not the working tree):
# a file is checked only when git itself would treat it as text (no 0x00
# byte in the first 8000 bytes, git's own binary heuristic), and every byte
# of the staged blob is then checked for a CR not followed by LF.
#
# Exit 0: nothing staged, or no staged text file carries a lone CR.
# Exit 1: at least one staged text file does (file + byte offset printed),
#         or a staged blob could not be read (a blocked commit is the safe
#         side; git status tells the rest).
#
# Pure ASCII on purpose: PS 5.1 mis-parses BOM-less non-ASCII bytes.

$ErrorActionPreference = 'Stop'
$root = (& git rev-parse --show-toplevel).Trim()

& git -C $root rev-parse --verify HEAD 2>$null | Out-Null
$haveHead = ($LASTEXITCODE -eq 0)
if ($haveHead) {
    # Staged delta (add/copy/modify/rename/typechange; deletes carry no new
    # bytes).
    $staged = @((& git -C $root diff --cached --name-only --diff-filter=ACMRT) | Where-Object { $_ })
} else {
    # First commit (no HEAD yet): the whole index is the delta.
    $staged = @((& git -C $root ls-files) | Where-Object { $_ })
}
if ($staged.Count -eq 0) {
    Write-Output 'lone-CR guard: nothing staged - ok.'
    exit 0
}

$bad = @()
foreach ($f in $staged) {
    $tmp = Join-Path $env:TEMP ('wmd-crs-' + [guid]::NewGuid().ToString('N') + '.bin')
    try {
        # Raw capture of the staged blob (no PowerShell re-encoding): git
        # stdout is copied byte-for-byte into a temp file.
        $p = New-Object System.Diagnostics.Process
        $p.StartInfo.FileName = 'git'
        $p.StartInfo.Arguments = '-C "' + $root + '" cat-file blob :"' + $f + '"'
        $p.StartInfo.UseShellExecute = $false
        $p.StartInfo.RedirectStandardOutput = $true
        $p.StartInfo.RedirectStandardError = $true
        [void]$p.Start()
        $fs = [System.IO.File]::Create($tmp)
        [void]$p.StandardOutput.BaseStream.CopyTo($fs)
        $fs.Close()
        $p.WaitForExit()
        if ($p.ExitCode -ne 0) {
            Write-Output ('lone-CR guard: cannot read the staged blob of ' + $f + ' - commit blocked (git status will show why).')
            exit 1
        }
        $bytes = [System.IO.File]::ReadAllBytes($tmp)
        $head = [Math]::Min(8000, $bytes.Length)
        $isBinary = $false
        for ($i = 0; $i -lt $head; $i++) {
            if ($bytes[$i] -eq 0) { $isBinary = $true; break }
        }
        if ($isBinary) { continue }
        for ($i = 0; $i -lt $bytes.Length - 1; $i++) {
            if ($bytes[$i] -eq 13 -and $bytes[$i + 1] -ne 10) {
                $bad += ($f + ' : lone CR at byte ' + $i)
                break
            }
        }
    } finally {
        if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Force }
    }
}
if ($bad.Count -eq 0) {
    Write-Output ('lone-CR guard: no lone CR in ' + $staged.Count + ' staged file(s) - ok.')
    exit 0
}
Write-Output 'lone-CR guard: blocked - a staged file would be classified binary by git (the text=auto lone-CR rule) and stored as a whole-file diff:'
foreach ($b in $bad) {
    Write-Output ('  ' + $b)
}
Write-Output 'Fix the file (every CR must be followed by LF, or the CR removed), then re-stage it.'
exit 1