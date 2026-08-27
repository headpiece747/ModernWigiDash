# ps-hygiene.ps1 - the harness ps1 lint + test layer (opt-in, not a gate stage).
#
# The ADR-0010 precedent: the mechanical prose gate made a 45k-error wall, so
# this layer runs when the harness surface changes (and before a release), not
# on every commit. Three checks over scripts\ + .opencode\skills\:
#
#   1. Pure ASCII: no byte >= 0x80 in any repo .ps1 (PS 5.1 mis-parses
#      BOM-less non-ASCII; a BOM is a mis-parse too).
#   2. PSScriptAnalyzer with psa-settings.psd1 (every allow-list disable
#      carries a dated reason; a new finding is a regression to fix).
#   3. Pester over scripts\tests\ (the gate-guard, ref-check, lone-CR-scan,
#      and harness parse regression pins).
#
# Exit 0 all green; exit 1 with the named failures.

param(
    [switch]$SkipPester
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$failed = 0

$ps1s = @()
foreach ($d in @((Join-Path $root 'scripts'), (Join-Path $root '.opencode\skills'))) {
    $ps1s += @(Get-ChildItem -Path $d -Recurse -File -Filter *.ps1 -ErrorAction SilentlyContinue)
}

# --- 1. pure ASCII sweep ---
$nonAscii = 0
foreach ($f in $ps1s) {
    $b = [System.IO.File]::ReadAllBytes($f.FullName)
    for ($i = 0; $i -lt $b.Length; $i++) {
        if ($b[$i] -ge 128) {
            Write-Output ('non-ASCII: {0} @ byte {1}' -f $f.FullName, $i)
            $nonAscii++
        }
    }
}
if ($nonAscii -gt 0) {
    $failed = 1
    Write-Output ('ps-hygiene: {0} non-ASCII byte(s) in repo ps1.' -f $nonAscii)
} else {
    Write-Output ('ps-hygiene: {0} ps1 files pure ASCII.' -f $ps1s.Count)
}

# --- 2. PSScriptAnalyzer ---
$mod = Get-Module -ListAvailable PSScriptAnalyzer | Sort-Object Version -Descending | Select-Object -First 1
if (-not $mod) {
    Write-Output 'ps-hygiene: PSScriptAnalyzer not installed (Install-Module PSScriptAnalyzer -Scope CurrentUser). Skipping the analyzer pass.'
} else {
    Import-Module $mod.Path -ErrorAction Stop
    $settings = Join-Path $PSScriptRoot 'psa-settings.psd1'
    $hits = @()
    foreach ($f in $ps1s) {
        # PS 5.1 quirk (probed 2026-08-27): a single-file -Recurse:$false call
        # returns a flat array of DiagnosticRecord, or $null when the file is
        # clean.
        $r = Invoke-ScriptAnalyzer -Path $f.FullName -Settings $settings -Recurse:$false
        if ($null -ne $r) { $hits += @($r) }
    }
    if ($hits.Count -gt 0) {
        $failed = 1
        Write-Output ('ps-hygiene: {0} analyzer finding(s):' -f $hits.Count)
        foreach ($h in $hits) {
            Write-Output ('  {0} L{1}: {2}' -f $h.RuleName, $h.Line, $h.Message)
        }
    } else {
        Write-Output 'ps-hygiene: analyzer clean.'
    }
}

# --- 3. Pester ---
if (-not $SkipPester) {
    $pmod = Get-Module -ListAvailable Pester | Where-Object { $_.Version.Major -ge 5 } | Sort-Object Version -Descending | Select-Object -First 1
    if (-not $pmod) {
        Write-Output 'ps-hygiene: Pester 5 not installed (Install-Module Pester -Scope CurrentUser -SkipPublisherCheck). Skipping the test pass.'
    } else {
        Import-Module $pmod.Path -ErrorAction Stop
        $testFiles = @(Get-ChildItem -Path (Join-Path $PSScriptRoot 'tests') -Filter *.Tests.ps1 -File | ForEach-Object { $_.FullName })
        $result = Invoke-Pester -Path $testFiles -PassThru -Output Normal
        if ($result.FailedCount -gt 0) {
            $failed = 1
            Write-Output ('ps-hygiene: {0} test(s) failed.' -f $result.FailedCount)
        } else {
            Write-Output ('ps-hygiene: {0} test(s) passed.' -f $result.TotalCount)
        }
    }
}

if ($failed -ne 0) { exit 1 }
Write-Output 'ps-hygiene: GREEN.'
