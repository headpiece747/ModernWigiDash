[CmdletBinding()]
param(
    [string]$BuildDir = "",
    [string]$ResultsDir = "",
    [ValidateRange(0.0, 1.0)]
    [double]$MinLineCoverage = 0.70
)

$ErrorActionPreference = "Stop"

# The local coverage gate (no CI pipeline in this repo): runs the full suite
# with coverlet.MTP code coverage (--coverlet --coverlet-output-format
# cobertura --coverlet-file-prefix wmd; the coverlet.MTP package is referenced
# by the MSTest.Sdk test project and emits its report into --results-directory
# as "<prefix>.coverage.cobertura.<timestamp>.xml", verified reliable under the
# clean-build wipe + -p:BaseOutputPath redirect 2026-09-12) and fails with a
# non-zero exit when any gated module (the pure-policy layers: Sdk/Core/
# Hardware) drops below -MinLineCoverage. Output goes to a log under $ResultsDir;
# the console prints the per-project table, the gate verdict, and the suite's
# "Test run summary" line. The cobertura XML uses the same <package
# name="project"> + class/lines/line hits shape the parse+gate logic below
# expects. coverlet.MTP is the continuity choice over the Microsoft CodeCoverage
# engine (--coverage): it is the documented coverlet port for MTP and keeps the
# numbers comparable to the pre-MTP coverlet XPlat baseline; the two engines
# attribute lines slightly differently (sequence-point instrumentation vs the
# CodeCoverage mechanism), so absolute percentages differ a point or two between
# them - the gate floors are the contract, not the exact figure.
$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

if ([string]::IsNullOrWhiteSpace($BuildDir)) {
    # A coverage-specific temp output: the plain-test temp dir can be locked
    # by a running ModernWigiDash.App.exe instance. coverlet.MTP emits its
    # report correctly under this redirect (verified 2026-09-12).
    $BuildDir = Join-Path ([System.IO.Path]::GetTempPath()) "opencode\wmd-cov-build"
}
if ([string]::IsNullOrWhiteSpace($ResultsDir)) {
    $ResultsDir = Join-Path ([System.IO.Path]::GetTempPath()) "opencode\wmd-coverage-results"
}

$Sln       = Join-Path $Root "ModernWigiDash.slnx"
$Log       = Join-Path $ResultsDir "measure-coverage.log"
$GateNames = @("ModernWigiDash.Sdk", "ModernWigiDash.Core", "ModernWigiDash.Hardware")

New-Item -ItemType Directory -Path $ResultsDir -Force | Out-Null

# --- 1. Run the suite with MTP code coverage (output to log) ---
# A fresh results dir per invocation: the collector nests reports under
# TestResults\<guid> and accumulates forever, so a run that exits 0 without
# producing a report must FAIL the gate, never fall back to last run's data.
# The deletion is guarded to the disposable temp path - an explicitly
# supplied -ResultsDir outside it must never be silently erased.
$TempRoot = [System.IO.Path]::GetFullPath((Join-Path ([System.IO.Path]::GetTempPath()) "opencode")).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
$BuildRoot = [System.IO.Path]::GetFullPath($BuildDir)
$ResultsRoot = [System.IO.Path]::GetFullPath($ResultsDir)
# Only real CHILDREN of the disposable scratch root are erased - never the
# root itself (it holds other builds) and never a prefix-lookalike sibling.
# GetFullPath resolves '..' segments, so a crafted
# "<temp>\opencode\..\important" path cannot masquerade as a child.
$IsDisposable = $ResultsRoot.StartsWith($TempRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)
$BuildIsDisposable = $BuildRoot.StartsWith($TempRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)
$BuildsUnderResults = $BuildRoot.Equals($ResultsRoot, [System.StringComparison]::OrdinalIgnoreCase) `
    -or $BuildRoot.StartsWith($ResultsRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase) `
    -or $ResultsRoot.StartsWith($BuildRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)
if ($IsDisposable -and $BuildsUnderResults) {
    throw "ResultsDir and BuildDir must be distinct/non-nested - the fresh-results cleanup would delete the build output before the run"
}
# The build dir gets the same disposable-treatment. A stale bin output
# left by a previous run could let the run measure coverage over
# assemblies that are not the current source: the documented trap
# (hit live 2026-08-26) is that an incremental build can report
# UP-TO-DATE over changed content when its timestamp check is stale, and
# dotnet test has no --no-incremental flag to override that. Wiping the
# bin output first is the coverage-run equivalent: a missing output can
# never be judged up-to-date, so the build must recompile. An explicit
# -BuildDir outside the scratch root is left alone. (The intermediate
# obj\ state lives under the project trees, same as the gate's test
# stage; the wiped bin output forces the full build either way.)
if ($BuildIsDisposable -and (Test-Path $BuildDir)) {
    Remove-Item -Path $BuildDir -Recurse -Force
}
if ($IsDisposable -and (Test-Path $ResultsDir)) {
    Remove-Item -Path $ResultsDir -Recurse -Force
}
New-Item -ItemType Directory -Path $ResultsDir -Force | Out-Null
$runStart = Get-Date

Write-Host "Running the full suite with coverlet.MTP code coverage collection..."
# dotnet test (MTP mode) builds before testing (incrementally, recompiling
# changed content - the house temp-BaseOutputPath test shape: never --no-build,
# that would run a previous build's stale artifacts). The disposable BuildDir
# wipe above starts the bin output clean, so the coverage numbers can only
# measure this run's binaries. The trailing separator on the BaseOutputPath
# value is load-bearing: without it MSBuild concatenates
# $(BaseOutputPath)$(Configuration) into a "<dir>Release" SIBLING of the build
# dir, which the wipe would never reach. MTP forwards unrecognized tokens to
# the test app and exits 5 on them, so the VSTest-era flags (--nologo,
# -nodeReuse:false) are dropped; -p:BaseOutputPath still redirects the BUILD
# output in MTP mode (verified), keeping the locked-bin isolation. Coverage is
# collected via --coverlet --coverlet-output-format cobertura
# --coverlet-file-prefix wmd (the coverlet.MTP package referenced by the test
# csproj; emits reliably under the BaseOutputPath redirect, verified 2026-09-12).
# The report lands under --results-directory as
# "wmd.coverage.cobertura.<timestamp>.xml"; the prefix-scoped glob below picks
# it up (a bare *.cobertura.xml would MISS the name: the timestamp sits between
# ".cobertura." and ".xml", so the file does not end in ".cobertura.xml").
& dotnet test --solution $Sln -c Release `
    "-p:BaseOutputPath=$BuildDir\" `
    --results-directory $ResultsDir `
    --coverlet --coverlet-output-format cobertura --coverlet-file-prefix wmd *> $Log
if ($LASTEXITCODE -ne 0) { throw "dotnet test failed (exit $LASTEXITCODE); see $Log" }

# --- 2. Parse this invocation's cobertura result ---
# One test project => exactly one report today; if a second test project is
# ever added, each emits its own report and the gate must aggregate them:
# fail loudly on ambiguity instead of silently keeping the newest. coverlet.MTP
# names the report "<prefix>.coverage.cobertura.<timestamp>.xml" (here
# "wmd.coverage.cobertura.*.xml"), so the glob is scoped to that prefix and the
# LastWriteTime filter keeps only this run's file.
$Coverage = @(Get-ChildItem -Path $ResultsDir -Recurse -Filter "wmd.coverage.cobertura.*.xml" |
    Where-Object { $_.LastWriteTime -ge $runStart })
if ($Coverage.Count -eq 0) { throw "No wmd.coverage.cobertura.*.xml produced by this run under $ResultsDir" }
if ($Coverage.Count -gt 1) {
    throw "Multiple coverage reports produced ($($Coverage.Count)) - the gate does not aggregate; expected exactly one test project"
}
$Coverage = $Coverage[0]

[xml]$Cov = Get-Content $Coverage.FullName
$Packages = @{}
foreach ($p in $Cov.SelectNodes("//package")) { $Packages[$p.GetAttribute("name")] = $p }

function Get-LineCoverage([System.Xml.XmlElement]$Package) {
    # Count ONLY the class-level aggregate <lines> blocks: Cobertura emits each
    # method's lines twice (once under <methods>/<method>/<lines>, once in the
    # class aggregate), so a bare ".//line" would double-count every method
    # line and skew the rate.
    $lines = $Package.SelectNodes(".//class/lines/line")
    $valid = $lines.Count
    $covered = 0
    foreach ($l in $lines) { if ([double]$l.GetAttribute("hits") -gt 0) { $covered++ } }
    return [pscustomobject]@{
        Valid   = $valid
        Covered = $covered
        Rate    = if ($valid -gt 0) { $covered / $valid } else { 0.0 }
    }
}

# --- 3. Report per-project line coverage + overall ---
Write-Host ""
Write-Host ("{0,-28} {1,9} {2,9} {3,9}" -f "project", "line%", "covered", "valid")
$totalValid = 0.0; $totalCovered = 0.0
foreach ($name in ($Packages.Keys | Sort-Object)) {
    $m = Get-LineCoverage $Packages[$name]
    $totalValid += $m.Valid; $totalCovered += $m.Covered
    Write-Host ("{0,-28} {1,9:P1} {2,9:N0} {3,9:N0}" -f $name, $m.Rate, $m.Covered, $m.Valid)
}
Write-Host ("{0,-28} {1,9:P1} {2,9:N0} {3,9:N0}" -f "TOTAL",
    $(if ($totalValid -gt 0) { $totalCovered / $totalValid } else { 0.0 }),
    $totalCovered, $totalValid)

# --- 4. The gate: every gated module must clear -MinLineCoverage ---
$Below = @()
foreach ($name in $GateNames) {
    if (-not $Packages.ContainsKey($name)) { throw "Gated module '$name' not found in the coverage report" }
    $rate = (Get-LineCoverage $Packages[$name]).Rate
    if ($rate -lt $MinLineCoverage) { $Below += "$name ($('{0:P1}' -f $rate))" }
}

# MTP prints a multi-line "Test run summary" block; surface its header line
# (the one carrying Passed!/Failed!) plus the total/failed/succeeded counts so
# the console shows the suite verdict alongside the coverage table.
$summaryHeader = (Get-Content $Log | Select-String -Pattern "^Test run summary:").Line | Select-Object -Last 1
$countLines    = @(Get-Content $Log | Select-String -Pattern '^\s+(total|failed|succeeded|skipped):' | ForEach-Object { $_.Line.Trim() })
Write-Host ""
if ($null -ne $summaryHeader) { Write-Host $summaryHeader }
foreach ($cl in $countLines) { Write-Host ("  " + $cl) }

if ($Below.Count -gt 0) {
    Write-Host ""
    Write-Host ("GATE FAILED: below {0:P0} line coverage: {1}" -f $MinLineCoverage, ($Below -join ", "))
    Write-Host "Full run output: $Log"
    exit 1
}

Write-Host ""
Write-Host ("GATE PASSED: all gated modules >= {0:P0} line coverage ({1})" -f $MinLineCoverage, ($GateNames -join ", "))
Write-Host "Full run output: $Log"
