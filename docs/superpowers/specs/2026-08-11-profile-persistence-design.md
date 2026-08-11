# Profile Persistence — Design

**Date:** 2026-08-11
**Status:** Draft for review

## Goal

Persist the user's profile automatically: widget placements, page structure,
and property values survive an app restart. Today the app always rebuilds
`StarterProfile.Create()` on launch — every session starts from the default
6-page layout and the user's edits are lost on exit.

A second, independent goal: **scout** the ~200 MB working set and the
startup/CPU/latency overheads to produce an evidence-ranked list of reduction
targets. The scouting pass is a deliverable, not a fix list; fixes land
separately after the report.

## Context (verified facts)

- `ProfileOps.ExportJson` / `ImportJson` already serialize the full profile
  (pages, placements, PropertyValues) and are round-trip tested
  (`ProfileOps.cs:351`).
- `ProfileOps.ReplaceProfile` swaps a profile in with a single dispose-then-assign
  (`ProfileOps.cs:134`); the import sanitizer caps untrusted input.
- The app **never** auto-saves or auto-loads. `MainWindow` constructs
  `StarterProfile` directly (`MainWindow.xaml.cs:165`).
- Path conventions split today: the theme saves next to the exe
  (`AppContext.BaseDirectory/app_theme.json`), icons live in
  `%LocalAppData%\ModernWigiDash\icons`. LocalAppData is the writable, update-safe
  home for a per-user profile.
- The frame pipeline is already lean (~15–20 MB: one 1016×592 RGBA compositor
  surface, a 5×~1.2 MB exact-size RGB565 byte pool, per-widget surfaces). The
  remaining working set is runtime baseline + fonts + WPF chrome + caches —
  exactly what the scouting pass must rank.
- All widget property mutations funnel through `MainWindow.Context.PersistProperty`
  (`MainWindow.Context.cs:43`); drag/resize commits surface through
  `InputController.Move/Release` with an `out bool changed` flag.

## Decision 1 — `ProfilePersistence` module (App)

A new module in the App project, `ProfilePersistence`, owning the file path,
load, save, dirty-tracking, and debounce. Pure policy over injected seams so it
is unit-testable without WPF or the window:

- **Path**: `Path.Combine(Environment.GetFolderPath(LocalApplicationData),
  "ModernWigiDash", "profile.json")` — the same root as the existing icons dir.
  Directory created on first save.
- **Load**: if the file exists and parses, run it through the existing import
  sanitizer, then return the profile. Absent / corrupt / oversized / sanitizer
  failure → return null (caller falls back to starter).
- **Save**: `ExportJson` → write to `profile.json.tmp` → atomic `File.Replace` /
  `Move` so a crash never leaves a torn file.
- **MarkDirty**: arms a debounce timer (~2 s). Only mutations arm it — the 30 FPS
  render loop never touches the module, so there is zero per-frame cost.
- **Flush**: synchronous save; the window's `Closed` handler calls it before the
  teardown disposes run.

### Window wiring (MainWindow)

- **On startup**: `ProfilePersistence.Load()`; if non-null, `ReplaceProfile` it
  in place of the starter profile. If null (first launch), build
  `StarterProfile.Create()`, use it, and save it immediately so the file exists
  from first launch.
- **Dirty hooks** — three mutation funnels:
  1. `PersistProperty` (`MainWindow.Context.cs:43`) — every widget property write
     (inspector, icon-grab, widget OnTouch toggles).
  2. Page/widget structural seams in MainWindow (add/delete/rename page,
     place/remove widget, manual import).
  3. Drag/resize commits: the existing `out bool changed` from
     `InputController.Move`/`Release`; the mouse handlers mark dirty when true.
- **On close**: `Flush()` synchronously in the `Closed` handler (before
  `_framePump.Dispose()` etc.), so a clean exit always persists.
- **Manual Import/Export**: unchanged. A manual import replaces the in-memory
  profile; the next debounce or close persists it.

### Guarantees

- **Zero per-frame cost**: no timer fires during rendering unless a mutation
  happened; the save serializes the already-in-memory profile.
- **Crash-safe**: debounce window is short (2 s); atomic tmp+replace never
  leaves a corrupt file; on restart a corrupt/absent file falls back to the
  starter profile rather than crashing.
- **Backward-compatible**: the file is plain `ExportJson` schema — the same
  format a manual export produces, importable anywhere.

## Decision 2 — Memory / perf scouting pass (evidence first)

Deliverable: a ranked findings report, not fixes. Measurements:

1. **Startup + disk**: process start → window shown time; release zip size vs.
   loaded assemblies; publish-profile cost (`PublishTrimmed=false`,
   self-contained single-file).
2. **Working set breakdown**: run the app with the display disconnected (the
   engine is inert until `Start()`; connection simply fails and the render loop
   idles safely). Capture `dotnet-dump collect` → `dotnet-dump analyze` heap
   summary → top retained types ranked by bytes.
3. **GC / allocation pressure**: `dotnet-counters monitor` for gen0/1/2
   collections, LOH, allocation rate under idle, one active page, all six pages.
4. **Frame/touch latency**: compose→USB-push pacing (the compose gate already
   skips compose during a ~55 ms bulk write) and touch poll→route latency.

Known suspects to rank (not to fix): (a) self-contained runtime baseline +
`PublishTrimmed=false`; (b) per-widget SKSurface pool across all pages
(never evicted); (c) `FontHelper` typeface/font caches (bounded); (d) WPF chrome
+ inspector tree; (e) telemetry producers' buffers.

**Gate**: no fix lands without evidence pointing at it; each fix ships with its
own test, one at a time.

## Error handling & edge cases

- **Corrupt profile on disk**: sanitizer / parse failure → starter profile, and
  overwrite on next save (the corrupt file is replaced, not preserved forever).
- **First launch**: starter profile saved immediately; the file exists before
  any mutation.
- **File locked / read-only**: save failure is logged, never fatal; the next
  debounce or close retries.
- **Import while auto-save pending**: the debounce captures the post-import
  state (import is a mutation that arms the timer).
- **Crash between mutation and debounce**: up to ~2 s of changes are lost —
  accepted, matches the debounce decision.

## Verification

1. Unit tests for `ProfilePersistence`: path resolution, load-valid,
   load-corrupt, load-absent, save-tmp-replace atomicity, debounce coalescing,
   flush.
2. Window-level test: mutate profile → debounce fires → file on disk matches
   `ExportJson` output; close → file written.
3. Manual: run the release zip, rearrange widgets, close, relaunch — layout
   restored. Delete `%LocalAppData%\ModernWigiDash\profile.json`, relaunch —
   starter profile, file recreated.
4. Scouting pass: run the four measurements, produce the ranked report, review
   targets with the user before any fix.

## Out of scope / future

- Runtime migration/versioning of the profile schema (ExportJson schema is
  stable today; a future format change would add versioned read).
- Auto-save of theme (`app_theme.json` already has its own save path).
- Any memory/perf *fix* from the scouting pass — those land as separate,
  evidence-backed changes with their own tests.
