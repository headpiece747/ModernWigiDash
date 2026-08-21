# Post-sweep cosmetic fix: strip the single leading blank line left behind
# where a file's entire using block was removed. Touches only files whose
# first line is blank and second line is content - never files starting with
# a comment or two blank lines.
param(
    [Parameter(Mandatory)][string]$Root
)

$ErrorActionPreference = 'Stop'
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$projects = Get-ChildItem -Directory -LiteralPath $Root | Where-Object { $_.Name -like 'ModernWigiDash.*' }
$total = 0
foreach ($p in $projects) {
    $files = Get-ChildItem -Recurse -Filter *.cs -LiteralPath $p.FullName | Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' }
    foreach ($f in $files) {
        $lines = [System.IO.File]::ReadAllLines($f.FullName)
        if ($lines.Count -ge 2 -and $lines[0].Trim().Length -eq 0 -and $lines[1].Trim().Length -gt 0) {
            [System.IO.File]::WriteAllLines($f.FullName, $lines[1..($lines.Count - 1)], $utf8NoBom)
            $total++
        }
    }
}
Write-Output ("TOTAL files fixed under {0}: {1}" -f (Split-Path $Root -Leaf), $total)