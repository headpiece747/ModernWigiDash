# ref-check.ps1 - the deterministic stale-reference pre-pass for the house docs.
#
# The LLM drift passes (rules-check-drift, second-brain-audit) re-read whole
# docs; this script is the cheap half: it extracts backtick-quoted path-like
# references from the house doc set and reports the ones that no longer exist
# on disk. It is the rules-check-drift pre-pass (step 0) and can run any time.
#
# Why a house script instead of ctxlint (adopted in the same pass): ctxlint's
# stale-file-ref rule resolves references against the context file's own
# directory. This repo's agent context file lives at .opencode/AGENTS.md and
# its references are repo-root-relative, so ctxlint reports every one of them
# stale. The base-path model does not fit this layout (2026-08-23, ctxlint
# 1.1.3); the rule itself is still the right one, so it lives here with
# repo-root resolution.
#
# Path-likeness filter (a backtick span is checked only if it):
#   - has no placeholder/glob characters (< > * ( ) { } | ? space),
#   - is not a URL (http), a user-home path (~), a drive-letter path (C:),
#     or an env-var span (%...%), (machine-local / runtime paths are
#     deliberate and user-specific, never repo paths),
#   - is not relative-parent navigation (..), and does not start with a
#     separator (command-invocation tokens like /wayfinder, absolute paths),
#   - contains a separator only if the last segment carries a known
#     extension or the span is a directory reference (trailing slash); a
#     bare / in a name (Down/Move/Up, index-length/offset) is a name
#     separator, not a path separator,
#   - OR ends in a known extension with a non-empty basename.
# Directory references (trailing slash) are checked as directories.
#
# Exemptions: file-only names (no separator) that name runtime data files or
# machine-local runner files instead of repo paths. Each entry is a known
# false positive with its reason in the comment next to it.
#
# Exit 0 with a summary when clean; exit 1 listing every stale reference
# (file:line: reference). Pure ASCII (PS 5.1 mis-parses BOM-less non-ASCII).

param(
    [string[]]$Files = @(
        'CONTEXT.md',
        '.opencode/AGENTS.md',
        '.opencode/rules/dotnet-rules.md',
        'docs/agents/domain.md',
        'docs/agents/issue-tracker.md'
    ),
    [string[]]$Exempt = @(
        'app_theme.json',      # runtime data file (the app's data dir, not the repo)
        'profile.json',        # runtime data file (the persisted profile)
        'display_device.log',  # runtime log file
        'pending.ps1',         # machine-local no-UAC runner file (Temp\opencode\wmd-elevated)
        'result.txt',          # machine-local no-UAC runner file
        'run-elevated.ps1',    # machine-local runner file
        'elev-runner.ps1',   # machine-local runner file
        'PresentMonAPI2.dll', # runtime DLL, loaded from the PresentMon Service SDK dir (never shipped, by design)
        'playbooks/',        # skill-relative dir (poteto-mode/playbooks, named without its skill prefix)
        'hooks/',            # the Claude Code hooks dir, named in the "Not Installed" section
        'bin/',              # prose-gate exclusion vocabulary, generic build dirs
        'obj/',              # prose-gate exclusion vocabulary, generic build dirs
        'CONTEXT-MAP.md',    # upstream template token (docs/agents/domain.md names the multi-context layout); this repo is single-context, so it is absent by design
        'app_settings.json',       # runtime data file (the machine-local app settings in %LOCALAPPDATA%, not the repo)
        'app_theme.exe-dir.json',  # the harness backup name for the exe-dir theme copy (a test-artifact name, not a repo path)
        'autohotkey.exe',          # the user-supplied AutoHotkey interpreter (external, never bundled, ADR-0019)
        'scan-lone-cr.ps1',        # the retired temp-dir manual CR scan (obsolete: the pre-commit hook owns the scan now)
        'System.Text.Json'         # the .NET namespace/type name, not a file (ends in .Json, a known extension, by accident)
    )
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# Known file extensions. A separator-free span with no known extension is a
# token (a type name, a constant, a flag), not a path.
$knownExtensions = @(
    '.md', '.cs', '.csproj', '.sln', '.slnx', '.ps1', '.psm1', '.py',
    '.json', '.jsonc', '.tsv', '.props', '.targets', '.xml', '.yaml',
    '.yml', '.toml', '.txt', '.bat', '.cmd', '.sh', '.dll', '.exe',
    '.ttf', '.png', '.gif', '.jpg'
)

# Known bare file names: extension-less files the docs can reference (the
# git hooks). Without this list a `scripts/hooks/pre-commit` span looks
# like a token and escapes the staleness check.
$knownBareFilenames = @(
    'pre-commit', 'pre-push', 'post-commit', 'post-checkout', 'post-merge',
    'pre-receive', 'post-receive', 'update', 'commit-msg',
    'prepare-commit-msg', 'pre-applypatch', 'post-applypatch',
    'pre-merge-commit', 'push-to-checkout'
)

function Test-PathLike([string]$span) {
    if ($span -match '[<>*(){}|? ]') { return $false }
    if ($span -match '^https?://') { return $false }
    if ($span.StartsWith('~')) { return $false }
    if ($span -match '^[A-Za-z]:') { return $false }
    if ($span.Contains('%')) { return $false }
    if ($span -match '^[/\\]') { return $false }
    if ($span -match '\.\.') { return $false }
    $lastSeg = ($span -split '[/\\]')[-1]
    $dot = $lastSeg.LastIndexOf('.')
    $hasKnownExt = $false
    if ($dot -gt 0) {
        $ext = $lastSeg.Substring($dot).ToLowerInvariant()
        $base = $lastSeg.Substring(0, $dot)
        $hasKnownExt = ($knownExtensions -contains $ext) -and ($base.Length -gt 0)
    }
    if ($span -match '[/\\]') {
        # A path separator: the last segment must name a file (a known
        # extension or a known bare file name like a git hook) or the
        # span must be a directory reference.
        return $hasKnownExt
            -or ($knownBareFilenames -contains $lastSeg.ToLowerInvariant())
            -or $span.EndsWith('/')
    }
    # File-only name. Manual extension split on purpose:
    # [System.IO.Path]::GetExtension throws on prose characters that are
    # illegal Windows path characters.
    return $hasKnownExt
}

$stale = @()
# One tree walk: bare file names (IModernWidget.cs, TestDoubles.cs) name a
# file that may live anywhere in the repo, not just the root. The map makes
# that case resolve instead of report.
$fileNameMap = @{}
Get-ChildItem -Path $root -Recurse -File -Force -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch '\\(\.git|bin|obj|node_modules)\\' } |
    ForEach-Object { $fileNameMap[$_.Name.ToLowerInvariant()] = $true }

foreach ($rel in $Files) {
    $full = Join-Path $root $rel
    if (-not (Test-Path -LiteralPath $full)) {
        Write-Output ("ref-check: skipping, doc not found: $rel")
        continue
    }
    $docDir = Split-Path -Parent $full
    $lineNo = 0
    foreach ($line in [System.IO.File]::ReadLines($full)) {
        $lineNo++
        $matches2 = [regex]::Matches($line, '`([^`]+)`')
        foreach ($m in $matches2) {
            $span = $m.Groups[1].Value.Trim()
            if (-not (Test-PathLike $span)) { continue }
            if ($span -match '^[^/\\]+$') {
                # File-only name: exempt list first, then the repo root.
                $name = $span.Replace('\', '/')
                if ($Exempt -contains $name) { continue }
                $candidate = Join-Path $root $name
                if ((Test-Path -LiteralPath $candidate) -or $fileNameMap.ContainsKey($name.ToLowerInvariant())) { continue }
                $stale += ("{0}:{1}: {2} (no separator; not a known repo path or exempt data file)" -f $rel, $lineNo, $span)
                continue
            }
            $norm = $span.Replace('\', '/')
            if ($Exempt -contains $norm) { continue }
            $probe = $norm.TrimEnd('/')
            $hits = @()
            foreach ($base in @($root, $docDir)) {
                if (Test-Path -LiteralPath (Join-Path $base $probe)) { $hits += $base }
            }
            if ($hits.Count -eq 0) {
                $stale += ("{0}:{1}: {2}" -f $rel, $lineNo, $span)
            }
        }
    }
}

if ($stale.Count -gt 0) {
    Write-Output 'ref-check: STALE REFERENCES:'
    foreach ($s in $stale) { Write-Output ("  " + $s) }
    Write-Output ("ref-check: {0} stale reference(s). Fix the reference or move the file back (rules-check-drift step 3 flags a now-false map entry)." -f $stale.Count)
    exit 1
}
Write-Output ("ref-check: clean, {0} docs checked, no stale references." -f $Files.Count)
exit 0