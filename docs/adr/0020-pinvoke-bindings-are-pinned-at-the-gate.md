# ADR-0020: P/Invoke bindings are pinned at the gate

**Date:** 2026-08-26
**Status:** Accepted
**Deciders:** Project owner

## Context

Every launch of the 2026-08-26 build crashed at startup with
`EntryPointNotFoundException`: `HotkeyApi`'s externs were named
`RegisterHotKeyPInvoke`/`UnregisterHotKeyPInvoke` and carried no
`EntryPoint`, so the binding resolved against the method names, which are
not user32 exports. The crash site was `GlobalHotkeyManager.Refresh` via
the persisted `Ctrl+P` chord (the profile made startup actually register),
and every hotkey test injects `FakeHotkeyApi`: the production
`HotkeyApi.Default` binding was the one unexercised surface, so the
defect was paid for in the on-device loop, the place a feature should
only arrive to be verified, not diagnosed.

The audit found the same shape everywhere else: 20 of the 22
`[DllImport]`s in src (`WinUsbNative`'s 11, `TrackedTargetResolver`'s 7,
`SendInput`, `DwmSetWindowAttribute`) relied on method-name binding.
They happened to work because the names matched the exports; one rename
away from the same crash.

## Decision

P/Invoke bindings are pinned at the gate, in two layers:

- **Shape (DebtGuardTests, `HouseRules_DllImports_NameTheirEntryPointExplicitly`):**
  every `[DllImport]` in src spells its `EntryPoint` explicitly. The
  export name becomes a construction fact the diff shows, and a method
  rename can no longer silently re-target the call.
- **Fact (PInvokeBindingTests):** every spelled `(dll, entry point)` pair
  is probed against the real DLL (`GetModuleHandleW`/`LoadLibraryW` +
  `GetProcAddress`, an export-table lookup that never calls the imported
  function, so the check is safe on every machine). A misspelled export
  fails the gate's test stage instead of throwing on the first real call.
  Positive and negative controls pin the probe itself (it must be able to
  find and to miss), and an injected synthetic violation pins the shared
  extractor (`RepoScan.FindDllImports`) both rules run.

`HotkeyApiTests` keeps the production-delegate invocation (the degenerate
zero chord, released immediately; the return value is machine-defined and
deliberately unasserted): the probe proves the export exists, the pin test
proves the interop path executes.

The production-adapter convention joins the house pattern: a bag's
production binding is exercised by a test (`RegistryAutostartStoreTests`
round-trips the real registry, `TwitchTokenStoreTests` the real DPAPI,
`HotkeyApiTests` the real user32.dll). A new OS-boundary feature lands
with its pin; the device loop is a verification, not a debugger.

## Consequences

- Every existing extern now spells its export. The one judgment call was
  `GetWindowText` (bare method name, `CharSet.Unicode`): production
  evidence (full window titles in the frame-time log) shows the runtime
  resolves the W export, so the explicit spelling is `GetWindowTextW`,
  preserving the current binding.
- New `[DllImport]`s are covered automatically: the probe sweeps all src,
  so a new extern is probed at the next gate with no per-feature
  bookkeeping.
- Scope: the probe covers `[DllImport]` only. The PresentMon leg loads
  its DLL at runtime by name string (an absent service degrades to the
  placeholder, ADR-0017) and the probe cannot assume a machine-local
  optional DLL, so that leg stays out of the sweep.
- Exit: retire the probe if the house stops shipping P/Invoke; the
  spelling rule retires only if the compiler makes explicit entry points
  mandatory (it does not).

## Date

2026-08-26
