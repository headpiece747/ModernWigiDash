# ModernWigiDash: OpenCode Configuration

This project uses a curated subset of [dotnet-claude-kit](https://github.com/codewithmukesh/dotnet-claude-kit)
for .NET development intelligence. Skills, agents, and rules are installed
under `.opencode/` and adapted for this project (WPF desktop app +
USB/Hardware + Widget plugin libraries + MSTest tests).

## Agent skills

### Issue tracker

Issues and specs live as GitHub Issues (`headpiece747/ModernWigiDash`); all operations go through the `gh` CLI. See `docs/agents/issue-tracker.md`.

### Domain docs

Single-context: `CONTEXT.md` at the repo root + ADRs in `docs/adr/`. See `docs/agents/domain.md`.

## Skills

Installed under `.opencode/skills/`. Load by name when the task matches.

### .NET Domain Skills
- **modern-csharp**: C# 14 language features for .NET 10 (primary constructors, collection expressions, `field` keyword, records, patterns, spans)
- **project-structure**: .slnx, Directory.Build.props, Directory.Packages.props (CPM), global usings, naming conventions
- **httpclient-factory**: IHttpClientFactory / typed clients / resilience / testing (for PriceFeedManager HTTP paths)
- **testing**: test strategy, AAA, naming, seam injection (NOTE: this repo uses MSTest, not xUnit)

### Workflow Skills
- **build-fix**: autonomous loops to drive a broken build or failing tests to green
- **verify**: 7-phase verification pipeline (build, diagnostics, antipatterns, tests, security, formatting, diff)
- **code-review**: multi-dimensional review with blast-radius prioritization
- **arch-check**: architecture conformance against the CONTEXT.md layering
- **health-check**: 8-dimension project assessment with letter grades
- **outdated**: dependency health report (staleness, vulnerabilities, license traps)
- **security-scan**: 6-layer security scanning (packages, secrets, OWASP patterns, auth, data protection)
- **convention-learner**: detect and enforce project conventions (writes to CONTEXT.md / AGENTS.md)
- **hardware-e2e-validation**: the physical-WigiDash loop: elevated launch, UIA driving, full update-cycle smoke; every on-device finding becomes a regression test

### Tool Mapping
The kit's RoslynNavigator MCP server is **not** installed. Glider (already wired
in `opencode.json`) provides the equivalent tools. Each skill and agent carries a
mapping note near its top (e.g. `find_dead_code` → `glider_find_unused_symbols`,
`get_diagnostics` → `glider_get_diagnostics`, `get_project_graph` →
`glider_get_project_graph`, `detect_antipatterns` → `glider_semantic_query`).
The `outdated` skill's `get_nuget_packages` has no Glider equivalent. Inventory
with `dotnet list package` and `Directory.Packages.props`.

Verified tooling quirks (2026-08-25 sweep session; each one hit live): the
`glob` tool never matches files under dot-directories (`.opencode/**` returns
nothing even from a root `**/` pattern; use `Get-ChildItem` or `grep` with an
explicit path there). `glider_get_structure` lists no members without
`includeMembers=true` (a `kinds` filter alone returns the container with zero
members). `ocr_review` in workspace mode selects only tracked diffs; an
all-untracked changeset reports "no items selected", so commit first or pass
an explicit `from`/`to` range. A solution-wide `-p:BaseIntermediateOutputPath`
breaks multi-TFM builds (NETSDK1005: one project.assets.json per TFM, the
last restore wins); use a temp `-p:BaseOutputPath` for temp output instead.
git 2.55 with `text=auto` classifies a file containing a lone CR (a CR not
followed by LF) as binary (`i/-text w/-text`), so `git add` stores raw bytes
and the diff becomes a whole-file EOL change: the pre-commit hook now scans
the staged blobs for a stray 0x0D not followed by 0x0A
(`scripts/scan-staged-cr.ps1`, skipping files git itself treats as binary
via its NUL-in-first-8KB heuristic) and refuses the commit, so the manual
scan step is retired (the temp-dir `scan-lone-cr.ps1`, which scanned the
working tree instead of the index, is obsolete). The PowerShell tool
transport strips backticks and mangles `$` variables inside inline commands;
for multi-step byte/regex work, write a `.ps1` to the temp dir and run it
with `-File`. Verified 2026-08-26 (close-to-tray verification session; each
hit live): `display_device.log` timestamps are UTC while file mtimes print
local (that machine is UTC-4), so compare `LastWriteTimeUtc` against log
lines and grep log windows in UTC (a local-time window silently misses the
event); an inline `-Command` through the elevated runner fails to parse when
the inner string carries `"` and `|` together (a regex; "The string is
missing the terminator"), the same `.ps1` + `-File` rule as above;
PowerShell double-quoted strings: `"$var: text"` throws
`InvalidVariableReferenceWithDrive` (the `:` parses as a drive qualifier;
write `${var}:`), and `$Pid` is a read-only automatic variable (never a
`param` or assignment target); `PostMessage`/`WM_CLOSE` to the elevated app
from a normal-IL shell returns False (UIPI) and says nothing, so the harness
`stop` needs the elevated runner for its clean close (it now clean-closes
first because a force-kill wedges the display pipe for up to 30 s on the next
launch); the title-bar badge dot and border (`UsbStatusDot`/`UsbBadgeBorder`)
expose no UIA peer (only the `TxtUsbStatus` text does), so assert the dot by
pixel sampling at (textLeft-12, textTop+8±4) (the 8 px dot sits 8 px left of
the text, 12 px inside the border padding; a Connected badge on the default
theme samples exactly `#10B981`); UIA top-level queries can transiently miss
the main window right after a close/reopen (retry once before concluding);
app state locations: all three state files (`profile.json`,
`app_settings.json`, `app_theme.json`) live in
`%LOCALAPPDATA%\ModernWigiDash` (the theme moved there with ADR-0021: the
former exe-dir location carried a stale-copy hazard, a stale dev-machine copy
in `bin\Release` once silently overrode every color, a stale
`AccentGreen=#12141D` hid the "green" badge behind the amber `Connected`
label). The exe-dir `app_theme.json` copy is now a read-only one-time
migration source (absent state file + parseable legacy copy migrates and
logs one line); `wmd-verify.ps1` backup/restore-profile covers both
locations, the exe copy as `app_theme.exe-dir.json`); a PowerShell
here-string's closing `"@` must sit
at column 0 (a leading space keeps the string open, so everything after it,
including whole `function` definitions, is silently swallowed into the
string content: the script still parses with zero errors and runs, the
swallowed function just does not exist, "The term X is not recognized";
diagnose with `[Parser]::ParseFile` and enumerate the
`FunctionDefinitionAst` nodes, or check that every `"@` line starts with
bytes 34,64; hit live in `wmd-verify.ps1` where one space before the
WmdUser32 terminator deleted `Ensure-WinMsg`); and Windows PowerShell 5.1's
`Add-Type -MemberDefinition` compiles with a C# 5-era compiler, so an
expression-bodied method (`public static bool F() => Expr;`) fails with
"; expected" at the `=>`: use a regular method body (lambdas and `var` are
C# 3 and compile fine; hit live in the harness's `WmdWinMsg` Add-Type).
Verified 2026-08-26 (manager-parity session; each hit live): an incremental
`dotnet build` reports 0 warnings for UP-TO-DATE projects and can even
report UP-TO-DATE over changed content when its timestamp check is stale
(a real CS8600 hid that way): the gate's build stage now force-recompiles
(`--no-incremental`, 2026-08-26), and a manual warning-clean claim on a
changed project still needs `dotnet build MyProject.csproj -c Release
--no-incremental`. `dotnet format` enforces trailing whitespace, final
newlines, and line endings but never indentation (a deliberately
de-indented collection-expression line went uncorrected; two indent
defects shipped in `9d9eae3` and were fixed in `c15797f`): after any edit
that touches a multi-line statement or collection expression, verify the
edited region's leading whitespace by byte inspection, and do not trust
`git show | Select-Object` for that (the tool output mangles leading
spaces). The `edit` tool's mid-line `oldString` match can consume or add
indentation around the match; verify the region after the edit the same
way. The `write` tool omits the final newline (the format gate catches it
in `.md`; add it for scripts). The `grep` tool's path parameter is
unreliable here; use `bash` + `Select-String` / `Get-Content`. PS 5.1 has
no `Set-Content -Encoding UTF8NoBOM` (a .NET Core alias); use the `edit`
tool or `[System.IO.File]::WriteAllText` with `UTF8Encoding($false)`. The
drive-qualifier trap also bites `$_`: `"LINE$_: text"` is a ParserError,
not just `"$var: text"`; use `${var}:` or string concatenation. `FileLog`
cadence-flushes (8 KB / 250 ms), so a window test that reads the
redirected log must `FileLog.Flush()` first and open the file with
`FileShare.ReadWrite`. `HwndSource.HandleMessage` is internal: a window
test posts a Win32 message (e.g. `WM_HOTKEY`) with a `PostMessage`
P/Invoke and pumps the dispatcher with a `DispatcherFrame`/`PushFrame`
loop. `HotkeyActionExecutor.ParseVirtualKey` uppercases the main key (a
`ctrl+x` test expects `'X'`), and the WM_HOTKEY modifier word carries
`MOD_NOREPEAT` (`0x4000`) because it always rides the registration (a
Ctrl+Alt+Shift chord reads `16391`). A collection expression cannot target
a non-generic `ICollection` (CS9174; use `new[]`), and a cref like
`<c>Process.Start</c>` is ambiguous (CS0419; qualify it). The analyzer set
is strict about shape: `MA0006` wants `string.Equals(a, b,
StringComparison.Ordinal)` for string compares, `MA0158` wants the
`System.Threading.Lock` struct over an `object` gate, and `S3218` refuses a
variable that shadows an outer one (a test's `Handle` became `Hwnd`).
Verified 2026-08-26 (hotkey entry-point crash; each hit live): a
`[DllImport]` without an explicit `EntryPoint` binds against the *method
name*, so an extern named `RegisterHotKeyPInvoke` resolves an export that
does not exist and throws `EntryPointNotFoundException` on the first real
call. Every hotkey test injects `FakeHotkeyApi`, so the production
`HotkeyApi.Default` binding was the one unexercised surface, and the crash
only surfaced in the on-device loop (the app died at every launch's startup
refresh because the persisted `Ctrl+P` chord made startup register for
real). The audit found the same shape in 20 of the 22 src `DllImport`s
(`WinUsbNative`, `TrackedTargetResolver`, `SendInput`,
`DwmSetWindowAttribute`). It is now pinned at the gate (ADR-0020):
`DebtGuardTests` requires every src `[DllImport]` to spell its
`EntryPoint`, and `PInvokeBindingTests` probes every spelled (dll, entry
point) against the real DLL (`GetModuleHandleW`/`LoadLibraryW` +
`GetProcAddress`, an export-table lookup that never calls the imported
function), so a binding miss fails the gate, not the device loop. New
`[DllImport]`s are covered automatically (the probe sweeps all src).
Convention for the next OS-boundary feature: land the production-adapter
pin with the feature (invoke the production binding in a test; the
real-registry `RegistryAutostartStoreTests` and real-DPAPI
`TwitchTokenStoreTests` precedents) - the device loop verifies, it does
not debug. Note for the probe's own externs: `GetProcAddress` takes an
`LPCSTR`, so its pin needs `CharSet.Ansi` (the W variants of the other
two take `LPCWSTR` and match the default marshaling).
Verified 2026-08-26 (audit-fix session; each hit live): a WPF `KeyEventArgs`
built through the only public ctor (`new(Keyboard.PrimaryDevice,
PresentationSource.FromVisual(el), 0, key)`) carries `RoutedEvent` null, so
`RaiseEvent` throws `InvalidOperationException` (Every RoutedEventArgs must
have a non-null RoutedEvent); the synthesizable shape is to set
`press.RoutedEvent = UIElement.PreviewKeyDownEvent` before the raise (the
setter throws only while an event is mid-route, a fresh instance never is).
`Key`/`SystemKey` are otherwise read-only, so a `Key.System` press is not
synthesizable and its routing (System to the event's system key) is pinned
through the pure `ResolvePressKey` seam instead (`KeyCaptureEditorTests`).
`dotnet format` given a bare directory name (no `.slnx`/`.csproj`
extension) prints the command help instead of formatting; the file name is
required. The `edit` tool's indent mangle and the `write` tool's missing
final newline (both documented above) bit again this session on
`PriceFeedManager.cs` / `KeyCaptureEditorTests.cs`; the gate's format stage
caught both (the backstop works, but verify the edited region's leading
whitespace and a new file's final newline before the gate). The audit's
claim list itself existed only as a temp-dir script plus scrollback, which
forced a reconstruction of its E1-E6 test-gap subset; it is now persisted
at `docs/reports/2026-08-26-audit-findings.md` with the re-runnable
`docs/reports/2026-08-26-audit-verify-claims.ps1` (a claim list that lives
 only in scrollback is a bug: the session-lifecycle rule).

Verified 2026-08-27 (theme relocation + v0.6.8 release session; each hit
live): a .NET 10 `System.Text.Json` strictness: a `JsonValue.Create(value)`
without options holds a customized value that refuses to write under a
different `JsonSerializerOptions` (`JsonObject.ToJsonString` throws
`InvalidOperationException_JsonSerializerOptionsNoTypeInfoResolverSpecified`
only at runtime, the 4 test failures the compiler and the first build both
missed); the house pattern is to serialize the payload with the target's own
options and `JsonNode.Parse` the result into a plain node
(`ProfileExportTheme.WithTheme`; the round-trip pins sit in
`ProfileExportThemeTests`). Lockstep pins between two compile-time constants
are MSTEST0032 always-true (the pin is tautological at the analyzer level):
spell the lockstep against a derived value instead (a `StartsWith` over the
composed path, not `Assert.AreEqual(ConstA, ConstB)`, `ThemeSettingsTests`),
and drop the `!` after `Assert.IsNotNull` (S8969: the MSTest asserts carry
the null-flow attribute). The `write` tool again wrote bare-LF endings and no
final newline into new files: the format gate caught the missing newline on
`ProfileExportTheme.cs` (the backstop works), and the new ADR landed as 98
bare LFs on this CRLF checkout (git normalizes at commit, but byte-normalize
a new file to CRLF before the gate to keep the working tree uniform). `gh`
through this transport: a `--jq` expression with `\(` string interpolation is
mangled ("accepts at most 1 arg(s), received 5"), use a plain object
projection (`{name: .name, size: .size}`) and read the JSON; `gh --json ...
--jq '.[0]'` returns a JSON string in PowerShell, so `$run.databaseId` is
empty (the run id printed blank and `gh run watch` showed usage),
`ConvertFrom-Json` first or drop the `--jq`; git/gh push remote chatter goes
to stderr, so `2>&1` paints a successful push as a NativeCommandError, judge
the `old..new` line and `$LASTEXITCODE` instead of the error color (both the
master push and the v0.6.8 tag push read like failures). `rg` output through
the bash tool is plain strings, not MatchInfo: `$_ .Line` is $null
("cannot call a method on a null-valued expression"), use `-match` on the
string. A PowerShell statement cannot be piped: `for (...) { if (...)
{ Write-Host $x } } | Select-Object` is a ParserError ("an empty pipe element
is not allowed"), collect into an array or wrap in `$(...)`. Inline
byte/format-specifier display through the transport is unreliable (`${x:X2}`
printed a bare `0x`) while the comparison is fine, so for byte-level work
write a `.ps1` to the temp dir and run it with `-File` (the existing escape
hatch, hit again). The repo's default branch is `master`
(origin/HEAD -> origin/master): `origin/main` is an "unknown revision". The
shared CI runner starved the thread pool of
`SingleInstanceGuardTests.Primary_ActivationSignal_FiresTheCallbackAndReParks`
past the 30 s ceiling on the v0.6.8 push (2026-08-26 had widened 5 s to
30 s) and past the 60 s ceiling on 2026-08-27, so widening the ceiling is
unbounded (a healthy machine finishes in well under a second, but a loaded
runner can starve the pool arbitrarily; the failures are non-monotonic, a
re-run can pass inside the ceiling the prior run exhausted). The fix is not
a bigger ceiling but a bounded CI retry: the CI workflow runs this test
alone in a fresh test host, up to 3 attempts, while the other 2005 tests run
once unmasked; a real regression fails every attempt, a runner-load flake
lands on a less-loaded moment and passes. In a function whose return is captured, use
`Write-Host`, never `Write-Output` (the emitted line is space-joined onto the
return value; hit live when it polluted `$text` and mangled CONTEXT.md's first
line). `[ref]` parameters are illegal in a PowerShell function signature
(ParserError); return the value or use a script-scope variable. `String.Replace`'s
3-arg (`StringComparison`) overload does not resolve in PS 5.1; use the 2-arg
 form (ordinal by default).

Verified 2026-08-27 (tooling inventory pass; each hit live): the global
opencode config spans two files that merge (`~/.config/opencode/opencode.json`:
providers, model, permissions, agent overrides;
`~/.config/opencode/opencode.jsonc`: the ollama provider and the ONLY
definition of the `codegraph` MCP), and the project `opencode.json` adds
`glider`, `glider-trace`, and `agentmemory`: a "cleanup" that deletes the
`.jsonc` silently loses codegraph. OpenCode also surfaces skills from
`~/.claude/skills` (verified: `cs4ai` was surfaced from there), so that
directory is a skill scan path even though no other agent uses it. npm
package discovery through the registry search API
(`https://registry.npmjs.org/-/v1/search?text=...`) + `gh search repos` is
the working route, and `npm pack <pkg>` + a `rg -U` pass over the unpacked
`dist/` (external URLs, telemetry keywords, spawn/daemon surface) is the
working pre-wire safety inspection for an npm MCP.
Verified 2026-08-27 (harness lint-layer session; each hit live): a
comment-only catch block (`catch { # reason }`) is a parse error under
Windows PowerShell 5.1 (the 5.1 parser reports MissingEndCurlyBrace; a
truly empty `catch { }` parses fine), so empty-catch documentation must
sit on the line above the try, not inside the block (the first
documentation pass put the reasons inline and left wmd-verify.ps1 with
17 parse errors; the ParseRegression pin now requires the
preceding-line reason). The installed PSScriptAnalyzer 1.25.0
(Microsoft-signed) accepts the legacy settings schema only (a
`RuleConfig` key is rejected with SETTINGS_ERROR, and the module's
shipped `Settings\*.psd1` presets use the legacy keys), and within it
the per-rule `Rules = @{ X = @{ Enable = $false } }` form silently
ignored five of six probed rules while `ExcludeRules = @( ... )`
disabled every one. `Invoke-ScriptAnalyzer -Path <file> -Recurse:$false`
returns a flat array of DiagnosticRecords, or `$null` when the file is
clean (the `$null` must be wrapped before enumerating). PS 5.1's
`Join-Path` concatenates a rooted child path instead of honoring it
(`C:\a` + `C:\b\c.md` gives `C:\a\C:\b\c.md`), so rooted-path entries
need an `IsPathRooted` bypass (ref-check.ps1). Under
`$ErrorActionPreference = 'Stop'`, a native command's stderr becomes a
terminating `NativeCommandError` in PS 5.1 (`git ... 2>$null` dies when
git writes anything to stderr; scan-staged-cr.ps1's no-HEAD
first-commit path hit it and now runs under a temporarily relaxed EAP).
A PS 5.1 double-quoted string drops the backtick before an unrecognized
character (a backtick before `s` yields a bare `s`), so doc bodies that
must carry literal backticks are single-quoted (the ref-check test
seam). `[Parser]::ParseFile` returns the AST while the `[ref]` slots
take the tokens and the ParseError array (calling `.Errors` on the
errors array member-enumerates to `$null` and reads as one phantom
error), and `$stmt.SafeAst` is `$null` for some top-level statements in
5.1, so top-level type checks test the statement itself.
Verified 2026-08-27 (agentmemory MCP fix-all session; each hit live):
probing a Node stdio MCP from Windows PowerShell hits two .NET
Framework reader traps even in the working shape: the in-memory pipe
reader (Process + StreamReader) reports stdout EOF ~25-30 ms after the
initialize response while the node process stays alive (no
stdout.close/process.exit anywhere in the dist), and
`[System.IO.File]::ReadAllText` on a cmd-redirected stdout file while
the writer is alive throws an IOException sharing violation (the cmd
`>` handle denies a concurrent read), so the working probe shape is a
static stdin file (all MCP messages, LF-terminated) + a quoted .bat
(`C:\Program Files\nodejs\npx.cmd` -y <pkg> < in.txt > out.txt 2>
err.txt; node.exe lives under a spaced path) + Start-Process + a fixed
wait + taskkill /T /F of the tree + reading out.txt only after the
writer is dead; re-verified against @agentmemory/mcp@0.9.29 (the
initialize answered serverInfo agentmemory 0.9.29 and tools/list
carried exactly the 7 standalone tools: memory_recall, memory_save,
memory_sessions, memory_smart_search, memory_export, memory_audit,
memory_governance_delete). And a process-lifecycle fact: a long-lived
opencode process does not re-launch an MCP connection that closed
between sessions: the graceful `MCP connection closed` WARN for
agentmemory at 2026-08-27T16:43:46Z (the same shape as the 2026-08-05
close) was never followed by a re-launch, and the two sessions that
followed bootstrapped only `mcp:glider:*` / `mcp:glider-trace:*` at
start (no agentmemory tools, no startup WARN/ERROR), so a session
missing a configured MCP's tools needs an opencode restart, not a
config edit: the config entry, the warm _npx cache
(36167fce64e0f40c), and the server command were all verified healthy by
the probe, and no node process survives (zero node.exe/npx.exe).
Verified 2026-08-30 (close-behavior fix session; each hit live): a
fresh Windows PowerShell 5.1 process initializes `$LASTEXITCODE` to
`$null`, so `if ($LASTEXITCODE -ne 0)` after a command that never set
it reads as a failure (`$null -ne 0` is true): `wmd-verify.ps1`'s
`clean` re-invoked `stop` and checked the code, but `stop`'s success
paths ended with a bare `return`, so every successful stop misread as
a failure and `clean` skipped `restore-profile`. The success paths now
end in an explicit `exit 0` (`Fail` already exited 1). And the .NET 10
`dotnet format` whitespace engine normalizes doc-comment indentation
even though it leaves code indentation alone (the 2026-08-26
collection-expression case stands): a class doc comment before
`[TestClass]` under a file-scoped namespace is canonical at column 0
(the shape every other test file in the repo already has), and a
latent 4-space-indented doc block in `WindowCloseInterceptTests.cs`
surfaced the moment the file was edited, the format stage demanding
the column-0 rewrite. When a format-stage whitespace complaint points
at text you did not write, run `dotnet format` (not
`--verify-no-changes`) once and keep the engine's canonical form.
Verified 2026-08-30 (USB engine seam session; each hit live): the
stale temp test DLL: `dotnet build` without
`-p:BaseOutputPath=C:\Users\tobia\AppData\Local\Temp\opencode\wmd-build\`
writes to `bin\`, so a later `dotnet test --no-build` with that
`-p` silently runs the stale temp DLL (a test deleted in the same
session still ran; the one "failure" was a probe test already
removed). Build the test project with the same `-p` before any
`--no-build` run. `ocr_review`: workspace mode selects only tracked
diffs (an all-untracked changeset reports "no items selected"), has
no workspace resume, and its local LLM (ninfer/qwen3.8-27b at
127.0.0.1:8080) is slow (16 min for a 15-file diff, 5 min for a
2-file diff); the commit-based form (`commit: <sha>`) is the reliable
route and needs a large `timeoutMinutes` (the default timed out
mid-run on the big diff). `git hash-object` on a tracked file
applies the clean filter (CRLF to LF per `.gitattributes`), so raw
working-tree bytes never match the index blob on a CRLF file
(compare `git ls-files -s` instead). And `RepoScan.StripCode` (the
shared strip behind every raw-scan pin) had no raw-string-literal
handling: a triple-quote open was consumed two quotes at a time and
the next quote opened a phantom string that re-paired every quote
after it, silently corrupting the rest of a scanned file's strip
(the gate only stayed green because the misparse happened to blank
the body; a pin whose own injected-violation snippet is a raw string
would have tripped it). The strip now blanks a run of three or more
opening quotes to its closing quote run (the possibly longer full
run, per the C# spec), pinned by the `StripCode_RawStringLiteral_*`
tests.

### Meta Skills (from coleam00/skills, MIT)
- **rules-check-drift**: checks `.opencode/AGENTS.md` / `.opencode/rules/` / `CONTEXT.md` against recent changes; reports now-false rules and drifted map entries, minimal edit only. Run before every merge; use the last release tag (e.g. `v0.6.8..HEAD`) as the range on a clean tree.
- **opportunity-scan**: scans agentmemory sessions (reactive: one run's artifacts; proactive: window of logs) and recommends what to encode next (rules/skill/hook/subagent/MCP). Outputs a self-contained HTML report in `docs/`.
- **ablate-ai-layer**: measures whether the always-loaded AI instructions still earn their place by running the same task with the layer intact vs stripped, in throwaway git worktrees. The skill's own `map_layer.py` (`.opencode/skills/ablate-ai-layer/scripts/`) is adapted for `.opencode/`; `--runner` wraps `opencode run`.
- **second-brain-audit**: audits CONTEXT.md / AGENTS.md for state-shaped claims that stopped being true (memory rot), against the codebase + agentmemory. Phase 3 script skipped (no monetary values here); Phase 2 does the work.

### Ported (poteto pstack, MIT)

Installed under `.opencode/skills/`, trimmed for this repo's shape: single local
model (no per-role model routing, all subagents run on the session model), no
PRs/CI/Graphite (playbook closing step = the house build + affected-test gate +
conventional commit), no agent-transcripts source (reflect reads the session
digest). `deslop` → `desloppify`, control-skills → the verify pair, `interrogate`
→ `ocr_review` + `code-reviewer`, `why` → `docs/adr/` + `CONTEXT.md` + git history.

- **poteto-mode**: poteto's agent style, trimmed; ships 15 playbooks under `playbooks/` (investigation, bug-fix, perf-issue, hillclimb, runtime-forensics, trace-forensics, feature, refactoring, prototype, visual-parity, authoring-a-skill, eval, autonomous-run, session-pickup, pause-safely). Routes to the `poteto-agent` subagent.
- **principle-\*** (21): poteto's principles pack, one skill each: boundary-discipline, build-the-lever, encode-lessons-in-structure, exhaust-the-design-space, experience-first, fix-root-causes, foundational-thinking, guard-the-context-window, laziness-protocol, make-operations-idempotent, migrate-callers-then-delete-legacy-apis, minimize-reader-load, model-the-domain, never-block-on-the-human, outcome-oriented-execution, prove-it-works, redesign-from-first-principles, separate-before-serializing-shared-state, sequence-verifiable-units, subtract-before-you-add, type-system-discipline.
- **verify-modernwigidash**: drive the WPF app the way a user does and prove behavior with UIA evidence (the skill's own `wmd-verify.ps1` harness, `.opencode/skills/verify-modernwigidash/scripts/`: launch/doctor/dump/find/list/click/click-nth/value/set/click-at/shot/wait/profile backup+restore/stop/clean).
- **maintain-verification-skill**: periodic pass keeping the verify skill and its feature map honest (parallel source readers, one live session, one small correction batch).
- **create-verification-skill**: generate a project-local UIA/CLI driving skill for a new repo.
- **how**: subsystem walkthroughs before changing something; placement/ownership/critique questions.
- **architect**: sketch types/signatures/module shape before code, then stay in the loop.
- **arena**: spawn N parallel candidates at the same task, graft the strongest parts (edge = fresh contexts, not model diversity).
- **reflect**: three parallel review subagents over the session; each learning routes to a concrete skill edit.
- **swarm**: fan out N parallel workers, drain, one report (parallel coverage, races, exploration).
- **no-comments**: spawn `comment-sicko`, fix accepted findings, offer encodings for claimed constraints.
- **show-me-your-work**: TSV decision log (what/why/evidence/result) for long or unattended runs.
- **figure-it-out**: auditable playbook for big migrations / multi-part work: hypothesis loop + decision log.
- **unslop**: cut AI tells from writing; always applied to prose outputs.
- **technical-writing**: Diátaxis + Google-style sentences + STE instructions for docs/RFCs/commits.

## Specialist Agents

Installed under `.opencode/agents/` (opencode subagents, `mode: subagent`).

| Agent | When to Use |
|-------|-------------|
| dotnet-architect | Architecture decisions, project structure, module boundaries, feature scaffolding |
| build-error-resolver | Autonomous build error fixing, iterative compilation repair |
| code-reviewer | Multi-dimensional code review, PR review, quality gatekeeper (read-only: edit denied) |
| performance-analyst | Profiling, frame-pipeline optimization, allocation reduction, async audit |
| refactor-cleaner | Dead code removal, systematic cleanup, safe refactoring with verification |
| security-auditor | Auth, secrets (DPAPI), USB vendor protocol, untrusted-import trust, vulnerability review |
| test-engineer | Test strategy, MSTest patterns, coverage of critical paths |
| poteto-agent | Routing target for /poteto-mode and any poteto-style request; reads the poteto-mode SKILL.md and applies the principle-* skills |
| comment-sicko | Deranged comment-hater: reports deletions and MUST KILL flags, never edits files (usually via no-comments) |

## Rules

Consolidated coding conventions in `.opencode/rules/dotnet-rules.md` (adapted
from the kit; web/API/EF rules removed, MSTest substituted for xUnit, and the
project's deliberate design decisions, synchronous transport per ADR-0001,
reflection-instantiated widgets with static stores, are preserved and flagged).

## Session Lifecycle

Session boundaries follow work state, not session length. Durable facts
persist at the moment they are created, so compaction costs nothing and a
session death is survivable.

- **Checkpoint.** The moment a durable fact exists (decision, ID, undo
  command, registration, file path), persist it to a repo file, or
  CONTEXT.md / an ADR, and to agentmemory when that MCP is connected. A
  fact that exists only in scrollback is a bug. Persist it before moving
  on. Secrets and PII stay out of memory and notes (dotnet-rules §3).
- **Switch point.** Announce a "good switch point" when the session is
  visibly long AND the current work block is verified-and-persisted (tests
  green, todos current, state in memory). One announcement per work block;
  the user restarts whenever convenient and nothing is lost either way.
- **Handoff at the door.** Session end with work in flight → write the
  handoff doc (the `handoff` skill, renamed from `handoff-doc` upstream); its repo path is the record (put it
  where the work's durable state lives, CONTEXT.md, an ADR, or a repo file).
  The handoff doc, not the compacted session, is what survives a dead
  session.
- **Resume.** A fresh session opened with unfinished business ("resume",
  "where were we") → open the persisted handoff doc (or the repo record the
  work points at) and pick up from the state it records instead of
  re-deriving it.

## Registry candidates (intake filter, skills.sh pass 2026-08-24)

A skill proposed from skills.sh or any upstream registry must pass every
check below before install. A failed check is a dated rejection in the
Not Installed section, never a silent skip. Check 3's runtime half is
ask-first, not an automatic rejection: it is settled in-session, with the
reason the runtime is needed spelled out. The registry leaderboard is a
lagging index for an adopted upstream (renames ship upstream and the
leaderboard lags; `two-axis-review` to `code-review` is the recorded case),
so a sync diffs against the upstream repo, never the leaderboard.

1. **Shape**: local .NET desktop app. No web/TS, no database, no cloud, no
   media/video surface.
2. **Name**: unique across the project (`.opencode/skills/`) and the global
   (`~/.config/opencode/skills/`) locations, which opencode enforces.
3. **External runtime: ask first.** A tool, skill, or program that needs a
   new runtime (npm/Node, Python, `uv`, a global CLI, a new package
   manager) is allowed, but only after I flag it and explain exactly why
   that runtime is needed and what it pulls in (the 2026-08-27 decision
   replaced the outright ban, which had ruled out useful tools without a
   case-by-case look). The safety half stays hard and fails the check on
   its own: nothing inserted into the LLM provider traffic path, no
   telemetry, no cloud service.
4. **Distinctiveness**: no overlap with the existing catalog (judgment
   today; a mechanical scan once the catalog distinctiveness check lands).
5. **Hygiene**: `agnix` and `skillspector --no-llm` green after install.
6. **Claims**: a performance or quality claim is measured with a with/without
   run before the skill earns the catalog, or rejected on the claim alone.

### Accepted under the relaxed filter (2026-08-27 tooling pass)

- **agentmemory** (MCP, accepted 2026-08-27): the session-memory MCP the
  global `/remember` and `/recall` commands wrap. Canonical package
  `rohitg00/agentmemory`; wired project-scoped in `opencode.json` as
  `@agentmemory/mcp@0.9.29`, the standalone entrypoint (probes a full
  server at `localhost:3111`, falls back to a 7-tool local surface on
  file-backed storage at `~\.agentmemory\standalone.json`). Check-3
  runtime: Node (already present, v24) + the npx-cached npm packages
  `@agentmemory/mcp` and the `@agentmemory/agentmemory` core (~6.3 MB).
  Safety half verified by dist inspection 2026-08-27: no daemon, no
  ports, no native binary on the standalone path (the pinned iii-engine
  v0.11.2 binary + 4 ports are server mode only, deliberately not used),
  no telemetry code on the standalone path, and no external endpoint
  without explicit provider config (keyless default: BM25 + local store).
   `memory_save` + `memory_smart_search` were verified by a stdio MCP
   round-trip probe before the entry landed. Dated final decision
   2026-08-27 (parked-resolution pass): the full 54-tool server (daemon +
   `iii.exe` + 4 ports) is REJECTED for this repo: the standalone 7-tool
   surface covers every workflow in use, the daemon + native binary is a
   larger attack surface than the file-backed store, and the safety
   inspection (dist walk) verified the standalone path only. Reopening is
   a deliberate act (start the daemon; the shim then proxies all 54
   tools, which would also restore `/recall`'s lesson step), not drift.
- **cs4ai** (dotnet tool + skill, archived 2026-08-27): a semantic C#
  editor CLI surfaced from `~/.claude/skills`, off-catalog and never
  intake-reviewed; its "use INSTEAD OF Grep/Read/Edit" directive conflicts
  with the MCP-first house rule and its edit surface overlaps
  `glider_rename_symbol` / `glider_move_type` / `glider_move_member` and
  codegraph. The user does not run Claude Code, so the skill directory was
  archived to `~\.claude\skills-archived\cs4ai` (out of the skill scan
   path; restore is one move) and the `cs4ai` dotnet global tool stays
   installed. Reintake is a deliberate decision, not drift.
- **PSScriptAnalyzer + Pester** (PowerShellGet modules, adopted
  2026-08-27, the parked-candidate resolution): the lint + test layer for
  the harness `.ps1` scripts (wmd-verify, run-gates, gate-guard, the
  elevated runner); their bug history (the here-string terminator that
  deleted `Ensure-WinMsg`, the PS 5.1 Add-Type C#5 trap) was caught by
  hand each time until this. Check-3 runtime: none new (Windows
  PowerShell 5.1 + bundled PowerShellGet; Pester 5.7.1 needed
  `-SkipPublisherCheck` to shadow the bundled Microsoft-signed 3.4.0,
  both CurrentUser scope). Implementation: `scripts\ps-hygiene.ps1`
  (opt-in lint layer, NOT a gate stage, ADR-0010 precedent) runs three
  passes over `scripts\` + `.opencode\skills\`: the pure-ASCII byte sweep
  (PS 5.1 mis-parses non-ASCII), PSScriptAnalyzer with
  `scripts\psa-settings.psd1` (every exclusion a dated allow-list entry
  with its reason; `ExcludeRules` is the reliable disable key in the
  installed 1.25.0, the per-rule `Rules.Enable` form silently ignores
  five of the six probed rules), and Pester over `scripts\tests\` (24
  tests: GateGuard 7, RefCheck 6, ScanStagedCr 5, ParseRegression 6
  covering the commit-guard verdict surface, the stale-reference
  pre-pass, the staged-blob lone-CR scan against scratch repos, and the
  harness syntax-surface regression pins: zero parse errors, the 19
  top-level functions, the C#5-era Add-Type payloads compile, the
  here-string terminators at column 0, and every empty catch documented
  with its preceding-line reason). First run fixed three real non-ASCII
  bugs (a BOM + two em dashes in `build-release.ps1`, an em dash in
  `measure-coverage.ps1`, a paint glyph in the `Get-AnyWindow` comment of
  `wmd-verify.ps1`) and the PS 5.1 comment-only-catch parse error the
  documentation comments almost introduced (17 parse errors before the
  fix; the reason comments now sit above the try).

## Not Installed (deliberately)

- markdownlint-cli (npm global, dated final rejection 2026-08-27, the
  parked-candidate resolution): prose lint for CONTEXT.md/ADRs. ADR-0010
  is the cautionary tale (a mechanical prose gate made a ~45k-error
  wall), the prose surface is already governed by the `unslop` and
  `technical-writing` style rules, and an npm-global tool is
  disproportionate for what a style pass covers. Reintake is a
  deliberate decision (opt-in skill step only, never a gate stage), not
  drift.
- `retro` + `implement-spec` (mattpocock upstream, dated final rejection
  2026-08-27, the parked-candidate resolution): both still sit in
  upstream's `in-progress/` bucket at `6654f6b` (re-verified
  2026-08-27, no newer upstream), and both overlap installed skills:
  `retro` (session retrospective + environment improvement suggestions)
  is `reflect` + `opportunity-scan`, `implement-spec` is `implement`.
  Reintake when they graduate upstream, not drift.

- `cwm-roslyn-navigator` MCP server: redundant with Glider
- bash workflow hooks (`hooks/`), not Windows-native; the repo's build/test
  workflow is covered by the build-fix/verify skills and the temp-output test
  command. (The gate-guard pre-commit hook under `scripts/hooks/` is a thin
  sh shim over PowerShell, git-hook shaped, not a bash workflow hook.)
- Web/API/EF/Docker/Aspire skills (api-versioning, ef-core, ddd, clean-architecture,
  vertical-slice, docker, container-publish, aspire, serilog, opentelemetry,
  messaging, minimal-api, openapi, scalar, authentication), no such surface here
- Workflow skills that duplicate existing skills (plan, tdd, checkpoint,
  wrap-up, de-sloppify), the project already has a `desloppify` skill and
  the global `tdd` skill
- Registry pass 2026-08-24 (skills.sh leaderboard, top ~400 + mattpocock
  upstream at `6654f6b`), each against the intake filter above:
  `triage` (a 7-label issue state machine the to-spec to-tickets to
  implement flow does not use; the `/triage` flag reference in
  `docs/agents/issue-tracker.md` is annotated, not activated), `prototype`
  (duplicates the ported poteto prototype playbook; its UI branch is
  web-shaped: one route, a URL search param, a pnpm task runner), `wizard`
  (a bash + `.env` + WSL wizard; the human-only steps here are owned by the
  elevated runner and `hardware-e2e-validation`), `teach` (education
  workspace, no project need), `to-questionnaire` (async decision docs for
  another human; the solo project settles decisions in-session through the
   `question` tool and grilling), `retro` + `implement-spec` (upstream
   `in-progress` at `6654f6b`; superseded 2026-08-27 by the dated final
   rejection in Not Installed), `obra/superpowers` (every salient skill overlaps
  an installed one: systematic-debugging = diagnosing-bugs,
  test-driven-development = tdd, verification-before-completion =
  principle-prove-it-works + verify, requesting/receiving-code-review = the
  code-review skill + code-reviewer agent, writing-plans/executing-plans =
  to-spec/implement, subagent-driven-development = implement + the task
  tool; 2026-08-27: a stale `plugin` entry in the global
  `~/.config/opencode/opencode.json` referenced this repo, nothing was
  ever installed, and the entry was removed), `JuliusBrussee/caveman` (a product, not a skill: an npm CLI, a
  BSL-1.1 proxy in the provider traffic path, telemetry on by default; its
  pixel mode renders SKILL.md bodies to PNG, which agnix and skillspector
  cannot lint; the terseness idea is already unslop +
  technical-writing), `vercel-labs/find-skills` + `anthropics/skill-creator`
  (meta overlap: the intake filter above plus `authoring-a-skill` and
  `writing-for-agents`), and the leaderboard's dominant categories by shape
  (AI video/media, web UI design, databases, cloud, SaaS CLIs): no
  .NET/desktop surface on the top ~400

## Verification Commands

- Build: `dotnet build ModernWigiDash.slnx -c Release --nologo`
- Tests (temp output avoids a running app instance locking the App output):
  `dotnet test ModernWigiDash.slnx -c Release --nologo -p:BaseOutputPath=C:\Users\tobia\AppData\Local\Temp\opencode\wmd-build\ -nodeReuse:false`
- Format: `dotnet format ModernWigiDash.slnx --verify-no-changes --verbosity quiet`
  (line endings are deliberately unpinned, ADR-0010; do NOT re-add
  `end_of_line` to `.editorconfig`, it recreates a ~45,000-error wall on
  Windows checkouts)
- Full gate run (build → test → format, stops at first failure,
  appends one trail row per run to `.audit/gates.tsv`). The build stage is
  a forced recompile (`--no-incremental`): the row's warning column covers
  every project every run, and an mtime-stale incremental build can never
  report UP-TO-DATE over changed content (a real CS8600 hid exactly that
  way, 2026-08-26); if the app is running from `bin\Release` the forced
  recompile fails on a locked output file, so stop the app (the harness
  `stop`) and re-run:
 `scripts\run-gates.ps1`, use it for full gate runs instead of the three
    commands above. The former 4th stage (the 2026-08-23 em-dash prose
    scan) was retired 2026-08-27: em-dash usage is governed by the prose
    style rules (the `unslop` and `technical-writing` skills), not by the
    gate.
- Harness ps1 lint (opt-in, NOT a gate stage, the ADR-0010 precedent):
  `scripts\ps-hygiene.ps1` runs the pure-ASCII sweep over every repo
  `.ps1`, PSScriptAnalyzer with the dated exclusion allow-list
  (`scripts\psa-settings.psd1`), and the 24 Pester tests over
  `scripts\tests\` (GateGuard / RefCheck / ScanStagedCr /
  ParseRegression). Run it when a harness script changes, after a
  tooling install, or before a release.
- Commit guard: a pre-commit hook blocks a commit unless the last gate row in
  `.audit/gates.tsv` is green in all three stages, its sha equals current HEAD,
  and the run is at most 60 min old (`-MaxAgeMinutes` on the guard). Install
  once per clone with `git config core.hooksPath scripts/hooks` (the hook file
  `scripts/hooks/pre-commit` is committed; the activation is local config).
  Logic lives in `scripts/gate-guard.ps1` (testable via `-GatesFile`); the
  hook then runs `scripts/scan-staged-cr.ps1`, which refuses the commit
  when a staged text file (git's own binary heuristic aside) carries a
  lone CR (the git 2.55 `text=auto` binary-classification trap). Escape
  per invocation only: `$env:WMD_GATE_GUARD_SKIP = '1'` (skips the gate
  check; the CR scan still runs).
- Debt guardrails (`DebtGuardTests`, runs in the gate's test stage, so the
  commit guard enforces them before every commit): the mechanical debt layer
  is machine-pinned against the src tree (raw scan, the `ArchitectureTests`
  house-rule shape; shared mechanics in `RepoScan`):
  - sync-over-async (`.Wait()` / `.Result` / `GetAwaiter().GetResult()`) only
    at the documented, budgeted sites (an allow-list with a reason per file,
    drift-checked both directions),
  - `async void` only on event handlers (the `EventArgs` signature),
  - every handle-acquiring file (P/Invoke extern, MemoryMappedFile, UsbContext,
    named mutex, loaded native library) carries its disposal evidence in the
    same file or a documented exception,
  - the frame pipeline's encode + buffer pool have one entry (the
    `IRgb565Encoder.Encode` call and every `FrameBufferPool` reference sit in
    `FrameDelivery` + the pool's own file),
- no dead private helpers (a private method with no call site in its type's
     files or the project XAML, transitive chains included),
   - every P/Invoke in src spells its entry point explicitly (the
     method-name binding is a rename away from the first-call
     EntryPointNotFoundException; the spelled pairs are probed against the
     real DLL by `PInvokeBindingTests`, ADR-0020).
  A violation fails the gate and the failure message spells the fix; a new
  legitimate site is a deliberate allow-list edit with a reason.
- Branch review: incoming PRs and feature branches are reviewed with the
  `code-reviewer` agent backed by `.opencode/rules/dotnet-rules.md` (its
  "Debt Dimensions" section names the three review areas: async/await without
  thread starvation, allocation handling and object pooling, disposal of
  unmanaged handles). The agent covers the judgment layer the pins cannot
  see: is an allow-list reason true, is an abstraction earning its place,
  does a Dispose path release every handle on every failure leg.
- Hygiene sweeps: `desloppify` (the CLI-driven health workflow, state under
  `.desloppify/`) is the periodic deep sweep for redundant abstractions and
  boilerplate; its mechanical residue (dead helpers, the anti-patterns above)
  stays pinned in `DebtGuardTests` so it cannot regress between sweeps.
  `unslop` is the prose pass (its em-dash scan was retired from the gate
  2026-08-27; the style rules govern), not a code
  sweep. Run a desloppify pass before a release or after a large refactor;
  record findings under `docs/reports/` and pin what is mechanical.
- Agent hygiene scanners (the 2026-08-23 awesome-claude-code adoption pass;
  the findings record is `docs/reports/2026-08-23-agent-hygiene-scan.md`):
  `skillspector` (NVIDIA SkillSpector, installed via
  `uv tool install git+https://github.com/NVIDIA/SkillSpector.git`, binary
  `C:\Users\tobia\.local\bin\skillspector.exe`) scans the skill trees for
  supply-chain and prompt-injection patterns, static only (`--no-llm`) so
  skill content never leaves the machine:
  `skillspector scan .opencode/skills --no-llm` and the same over
  `C:\Users\tobia\.config\opencode\skills`. The security-scan skill's layer 7
  wraps it with the house verdict table. `agnix` (npm global, `agnix .`)
  validates SKILL.md / AGENTS.md / agent-frontmatter shape (448 rules); run
  it after authoring or porting a skill (authoring-a-skill step 2,
   create-verification-skill step 4). `ctxlint` (npm global) lints root-level
   context files; its reference base-path (the context file's own directory)
   does not fit `.opencode/AGENTS.md`, so the deterministic stale-reference
   pre-pass is `scripts\ref-check.ps1` (the rules-check-drift step 0).
   The harness ps1 surface gets its own scanner: `scripts\ps-hygiene.ps1`
   (pure-ASCII sweep + PSScriptAnalyzer over `scripts\psa-settings.psd1`
   + Pester over `scripts\tests\`; run when the harness scripts change or
   before a release, opt-in by the ADR-0010 precedent, never a gate
   stage).
- Live-stack run requires elevation. **User preference: no per-call UAC prompts**:
  use the no-consent runner:
  `C:\Users\tobia\AppData\Local\Temp\opencode\wmd-elevated\run-elev-no-uac.ps1 -Command "[command]"`
  (drops the command into `pending.ps1`, triggers the `WmdElevatedRunner` scheduled
  task, created once with `/RL HIGHEST`, so `schtasks /Run` needs no consent, and
  polls `result.txt`; the elevated token is held by the task, the command runs in the
  user session so UIA can drive the app). `run-elevated.ps1` (UAC per call) stays as
  the fallback. If the task is missing (e.g. Temp was cleaned), recreate it with ONE
  elevated call: `schtasks /Create /TN WmdElevatedRunner /SC ONCE /ST 03:13 /TR "powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\Users\tobia\AppData\Local\Temp\opencode\wmd-elevated\elev-runner.ps1" /RL HIGHEST /F`
