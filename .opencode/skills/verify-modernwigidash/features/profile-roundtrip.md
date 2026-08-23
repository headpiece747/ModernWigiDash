# Profile round trip

The status bar exports the current profile to a file the user chooses and imports a profile file, rehydrating pages and widgets from it. This is the app's portability contract and the highest-value mutation proof: the on-disk JSON is the second view.

## Sub-features

- `export` writes `profile.json` to a user-chosen path via the Save dialog.
- `import` loads a profile file via the Open dialog and rehydrates the pages/widgets.
- `import-gate` rejects an oversized import via the app's size guard.

## How to get to it (user POV)

- Choose `💾 Export Profile` in the status bar.
- Choose `📂 Import Profile` in the status bar.

## Driving it with wmd-verify

Preconditions:

- The app passed `doctor`; `backup-profile` ran.
- The current profile contains at least one placed widget (place one first via the place-widget feature). An empty profile proves less.
- Evidence dir has a writable scratch path: `%TEMP%\opencode\wmd-verify-evidence\<run-slug>\profile\`.

- **Export.** Choose `💾 Export Profile`. Run `click ExportProfile`. A Save dialog (common file dialog, window title `Save As` or the OS localization) appears. Set the file name to the scratch path and confirm: the Save dialog's file-name Edit is usually the Edit control inside the dialog, locate it with `dump -Path <evidence>/profile/savedialog.txt` (look for a dialog whose children include a button named `Save`), then `set <file-name-edit> <scratch-path>.json` and `click Save`. Proof (second view): the file exists at the scratch path and parses: it is JSON, has a `pages` array, and that array's element count matches the page tabs (`find "📄 "` count).
- **Import.** Choose `📂 Import Profile`. Run `click ImportProfile`. The Open dialog appears (same handle story: find the `Open` button). `set <file-name-edit> <scratch-path>.json`, `click Open`. The app rehydrates: the tab strip matches the imported page set and `value ActiveCount` matches the imported widget count. Proof: `find "📄 "` now lists the imported page names; `value ActiveCount` equals the JSON's element count; `shot <evidence>/profile/after-import.png`.
- **Oversize gate.** Create a scratch file larger than the app's import cap (the sanitizer's `MaxImportFileBytes`; check the constant before choosing, do not hardcode a guessed size) with a `profile.json`-shaped but oversized body. Import it; the app refuses with an error dialog (`find "OK"` in a message window). Proof (negative): the dialog text names the size problem, the profile is unchanged (`find "📄 "` and `value ActiveCount` as before), and `restore-profile` is then run via `clean`.
- **Evidence.** Keep the exported JSON (it is the second-view proof), both dialog dumps, the `find`/`value` outputs, and the after-import shot under `<evidence>/profile/`.

## Gotchas

- The file dialogs are OS common dialogs, not app windows: `wmd-verify.ps1`'s `wait`/`doctor -Window` match by title (`Save As`, `Open`, localized on some systems; match loosely and confirm with a `dump` that the `Save`/`Open` button is present before clicking it).
- The Save dialog remembers the last-used path; a `set` of the full path is the reliable move. Appending nothing to a folder path re-saves over an existing file. The scratch path must include a run-slug file name.
- Import replaces the current profile (that is the feature): the `backup-profile` before the run is the only way back. Restore it in cleanup, and expect the relaunched-next-run app to show the restored profile, not the imported one.
- The oversize gate is a sanitizer rule, so the refusing dialog's wording is stable product copy; read it from the dialog, and a wording change is doc drift to fix in this map, not a bug to file.
- An import of the app's own persisted profile with sanitization skipped is the normal save path; a foreign file is the untrusted path. Keep the two distinct in the evidence (one exported file, one foreign file).