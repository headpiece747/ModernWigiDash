# Sweep: delete file-level usings that restate project globals. The global set
# is read from the project's csproj <Using Include="..."> items (single source
# of truth). Exact full-line match on plain `using <Ns>;` only - aliases
# (`using X = Y;`), `using static`, and `using var` are untouched. Re-runnable:
# files whose usings are all kept are not rewritten.
#
# ImplicitUsings baseline handling: the SDK implicit namespaces (System,
# System.IO, ...) are also removed ONLY for non-WPF projects. WPF projects
# (UseWPF=true) compile a generated "wpftmp" temporary project for the XAML
# markup pass that does not reliably apply ImplicitUsings, so stripping
# implicit usings there breaks the build (e.g. HttpClient in UpdateService.cs).
# Rule is derived from the csproj itself, so no mode parameter is needed.
param(
    [Parameter(Mandatory)][string]$ProjectDir
)

$ErrorActionPreference = 'Stop'
$proj = Get-ChildItem -Filter *.csproj -LiteralPath $ProjectDir | Select-Object -First 1
if (-not $proj) { throw "no csproj found under $ProjectDir" }
$csprojXml = [System.IO.File]::ReadAllText($proj.FullName)
$globals = [regex]::Matches($csprojXml, '<Using\s+Include="([^"]+)"\s*/>') | ForEach-Object { $_.Groups[1].Value }
if (-not $globals) { Write-Output "no Using items in $($proj.Name) - nothing to do"; exit 0 }
$useWpf = [regex]::Match($csprojXml, '<UseWPF>\s*true\s*</UseWPF>').Success

$implicit = @('System','System.Collections.Generic','System.IO','System.Linq','System.Net.Http','System.Threading','System.Threading.Tasks')
$base = if ($useWpf) { @() } else { $implicit }
$set = [System.Collections.Generic.HashSet[string]]::new([string[]](@($globals) + $base), [System.StringComparer]::Ordinal)
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

$joined = [string]::Join(', ', $globals)
Write-Output ("globals from {0}: {1}" -f $proj.Name, $joined)
Write-Output ("implicit baseline removal: {0}" -f $(if ($useWpf) { 'SKIPPED (UseWPF)' } else { 'enabled' }))
$files = Get-ChildItem -Recurse -Filter *.cs -LiteralPath $ProjectDir | Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' }
$total = 0
foreach ($f in $files) {
    $lines = [System.IO.File]::ReadAllLines($f.FullName)
    $keep = New-Object System.Collections.Generic.List[string]
    $removed = 0
    foreach ($l in $lines) {
        $t = $l.Trim()
        if ($t -match '^using [A-Za-z0-9_.]+;$') {
            $ns = $t.Substring(6).TrimEnd(';')
            if ($set.Contains($ns)) { $removed++; continue }
        }
        $keep.Add($l)
    }
    if ($removed -gt 0) {
        [System.IO.File]::WriteAllLines($f.FullName, $keep, $utf8NoBom)
        $total += $removed
        Write-Output ("{0,-80} -{1}" -f $f.Name, $removed)
    }
}
Write-Output ("TOTAL removed under {0}: {1}" -f (Split-Path $ProjectDir -Leaf), $total)