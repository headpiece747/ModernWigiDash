# run-gates.ps1 - the house verification gate with a durable trail row.
#
# Runs the three house gates in order, stopping at the first failure:
#   1. dotnet build  (Release, --nologo)
#   2. dotnet test   (the house temp-BaseOutputPath command - NEVER
#                     --no-build with that path: it would run the previous
#                     build's stale artifacts instead of the changed tree)
#   3. dotnet format --verify-no-changes
# Every run appends one TSV row to .audit/gates.tsv so a gate is evidence,
# not a commit-message claim. Exit code 0 only when all three are green.
#
# Usage (from the repo root):
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-gates.ps1 [-Label "what this gate covers"]
#
# Pure ASCII on purpose: PS 5.1 mis-parses BOM-less scripts with non-ASCII
# bytes under the ANSI code page (a trap hit twice during the usings sweep).

param(
    [string]$Label = 'manual'
)

$ErrorActionPreference = 'Stop'
$root      = Split-Path -Parent $PSScriptRoot
$sln       = Join-Path $root 'ModernWigiDash.slnx'
$outDir    = Join-Path $env:TEMP 'opencode\wmd-build'
$gatesFile = Join-Path $root '.audit\gates.tsv'
$utf8Bom   = New-Object System.Text.UTF8Encoding($true)
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

# Sanitize the label: one line, no tabs (TSV column separator).
$Label = (($Label -replace '[\r\n\t]', ' ').Trim())
if ([string]::IsNullOrWhiteSpace($Label)) { $Label = 'manual' }

function Add-GateRow {
    param(
        [string]$l, [string]$build, [string]$warn, [string]$err,
        [string]$test, [string]$passed, [string]$failed, [string]$fmt
    )
    $ts  = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    $sha = & git -C $root rev-parse --short HEAD
    $row = @($ts, $sha, $l, $build, $warn, $err, $test, $passed, $failed, $fmt) -join "`t"
    if (-not (Test-Path -LiteralPath $gatesFile)) {
        $header = @('ts', 'sha', 'label', 'build', 'warnings', 'errors', 'test', 'passed', 'failed', 'format') -join "`t"
        [System.IO.File]::WriteAllText($gatesFile, $header + [Environment]::NewLine, $utf8Bom)
    }
    [System.IO.File]::AppendAllText($gatesFile, $row + [Environment]::NewLine, $utf8NoBom)
    Write-Output ('gate row appended: ' + $row)
}

# --- 1. build ---
$buildOut = dotnet build $sln -c Release --nologo 2>&1 | Out-String
$buildOk  = ($LASTEXITCODE -eq 0)
$bw = 0; $be = 0
if ($buildOut -match '(\d+) Warning\(s\)') { $bw = [int]$Matches[1] }
if ($buildOut -match '(\d+) Error\(s\)')    { $be = [int]$Matches[1] }
if (-not $buildOk) {
    Write-Output $buildOut
    Add-GateRow -l $Label -build FAIL -warn $bw -err $be -test SKIP -passed 'n/a' -failed 'n/a' -fmt SKIP
    Write-Output 'GATE FAILED at build.'
    exit 1
}

# --- 2. test (fresh artifacts via the temp BaseOutputPath) ---
$testOut = dotnet test $sln -c Release --nologo -p:BaseOutputPath=$outDir -nodeReuse:false -v q 2>&1 | Out-String
$testOk  = ($LASTEXITCODE -eq 0)
$tp = 'n/a'; $tf = 'n/a'
if ($testOut -match 'Failed:\s*(\d+)') { $tf = $Matches[1] }
if ($testOut -match 'Passed:\s*(\d+)') { $tp = $Matches[1] }
if (-not $testOk) {
    Write-Output $testOut
    Add-GateRow -l $Label -build ok -warn $bw -err $be -test FAIL -passed $tp -failed $tf -fmt SKIP
    Write-Output 'GATE FAILED at test.'
    exit 1
}

# --- 3. format ---
$fmtOut = dotnet format $sln --verify-no-changes --verbosity quiet 2>&1 | Out-String
$fmtOk  = ($LASTEXITCODE -eq 0)
if (-not $fmtOk) {
    Write-Output $fmtOut
    Add-GateRow -l $Label -build ok -warn $bw -err $be -test ok -passed $tp -failed $tf -fmt FAIL
    Write-Output 'GATE FAILED at format.'
    exit 1
}

Add-GateRow -l $Label -build ok -warn $bw -err $be -test ok -passed $tp -failed $tf -fmt ok
Write-Output ('GATES GREEN: build (' + $bw + ' warnings, ' + $be + ' errors), tests (' + $tp + ' passed, ' + $tf + ' failed), format clean.')
exit 0