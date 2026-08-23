# gate-guard.ps1 - the pre-commit guard: a commit needs a green AND current gate.
#
# Reads the last row of the gate trail (.audit/gates.tsv, written by
# run-gates.ps1) and blocks the commit when:
#   - the trail is missing (no evidence of any gate run),
#   - the last row is not ok in build/test/format/prose,
#   - the gate row's sha != current HEAD (the tree moved after the gate:
#     pull, rebase, or a commit made elsewhere),
#   - the gate run is older than -MaxAgeMinutes (default 60).
#
# Install once per clone (the hook file is committed; the activation is local
# git config):
#   git config core.hooksPath scripts/hooks
#
# Deliberate escape, per invocation only (never committed, never ambient):
#   $env:WMD_GATE_GUARD_SKIP = '1'
#
# Pure ASCII on purpose: PS 5.1 mis-parses BOM-less non-ASCII bytes.

param(
    [int]$MaxAgeMinutes = 60,
    [string]$GatesFile = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($GatesFile)) {
    $GatesFile = Join-Path $root '.audit\gates.tsv'
}

if ($env:WMD_GATE_GUARD_SKIP -eq '1') {
    Write-Output 'gate guard: skipped (WMD_GATE_GUARD_SKIP=1).'
    exit 0
}

function Block([string]$reason) {
    Write-Output ('commit blocked: ' + $reason)
    exit 1
}

if (-not (Test-Path -LiteralPath $GatesFile)) {
    Block 'no gate trail (.audit\gates.tsv). Run scripts\run-gates.ps1 first: a gate is evidence, not a commit-message claim.'
}

$lines = [System.IO.File]::ReadAllLines($GatesFile) | Where-Object { $_.Trim() -ne '' }
if ($lines.Count -lt 2) {
    Block 'the gate trail has no rows. Run scripts\run-gates.ps1 first.'
}
$cols = @($lines[-1] -split "`t")
if ($cols.Count -lt 11) {
    Block ('unparseable gate row (expected 11 columns): ' + $lines[-1] + '. Re-run scripts\run-gates.ps1 (the trail predates the prose stage).')
}

$ts = $cols[0]
$sha = $cols[1]
$notOk = @()
if ($cols[3] -ne 'ok') { $notOk += ('build=' + $cols[3]) }
if ($cols[6] -ne 'ok') { $notOk += ('test=' + $cols[6]) }
if ($cols[9] -ne 'ok') { $notOk += ('format=' + $cols[9]) }
# 'n/a' is the honest legacy value (the prose stage did not exist in that
# run); the four-stage gate never emits it, so post-upgrade runs are strict.
if ($cols[10] -ne 'ok' -and $cols[10] -ne 'n/a') { $notOk += ('prose=' + $cols[10]) }
if ($notOk.Count -gt 0) {
    Block ('the last gate is not green (' + ($notOk -join ', ') + '). Fix the failure and re-run scripts\run-gates.ps1.' )
}

$head = & git -C $root rev-parse --short HEAD
if ($sha -ne $head) {
    Block ('the last gate ran at ' + $sha + ' but HEAD is ' + $head + ': the tree moved after the gate (pull/rebase). Re-run scripts\run-gates.ps1.' )
}

# ParseExact + ToUniversalTime on purpose: PS 5.1 parses the Z-suffixed trail
# timestamp onto the LOCAL clock (the instant is right, the Kind is Local),
# and a Local-Kind minus a Utc-Kind subtraction drifts by the machine's
# offset. Normalizing to UTC makes the age exact on any timezone.
$gateTime = [DateTime]::ParseExact($ts, 'yyyy-MM-ddTHH:mm:ssZ', [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::AssumeUniversal).ToUniversalTime()
$age = [DateTime]::UtcNow - $gateTime
if ($age -gt [TimeSpan]::FromMinutes($MaxAgeMinutes)) {
    Block ('the last green gate is ' + [int]$age.TotalMinutes + ' min old (limit ' + $MaxAgeMinutes + ' min). Re-run scripts\run-gates.ps1.' )
}

Write-Output ('gate guard: green gate at ' + $ts + ' (' + $sha + ', ' + [int]$age.TotalMinutes + ' min old) - commit allowed.')
exit 0