# Pages

Pages are the swipeable canvas containers shown as a tab strip under the preview. The user adds pages, switches between them, renames, and deletes (a confirm appears only when the page still holds widgets).

## Sub-features

- `page-add` adds a page; the tab strip gains one tab and the new page becomes active.
- `page-switch` activates an existing page from its tab button.
- `page-rename` renames a page through the per-tab prompt (OK/Cancel + a text box).
- `page-delete` deletes a page through the per-tab close button and its confirm dialog.

## How to get to it (user POV)

- Choose `+ Add Page` under the canvas.
- Choose a `📄 <PageName>` tab button.
- Choose the `✏️` icon on a tab (tooltip `Rename page`).
- Choose the `✕` icon on a tab.

## Driving it with wmd-verify

Preconditions:

- The app passed `doctor`; `backup-profile` ran.
- Count the baseline tabs first: `find "📄 "` and note the count N and the names.

- **Add.** Choose `+ Add Page`. Run `click AddPage`. The strip gains one tab named `📄 Page <N+1>` (or the app's next free name, read it from the result, do not assume). Proof: `find "📄 "` now lists N+1 tabs. Capture `shot <evidence>/pages/after-add.png`.
- **Switch.** Choose an inactive tab. Run `click "📄 Page 1"` (a different tab than the active one). The canvas repaints with that page's widgets. The active state is styled (accent vs plain), which UIA does not expose, so the proof is visual: `shot <evidence>/pages/switched.png` plus the `ActiveWidgets` count (`value ActiveCount`) matching that page's expectation (read it per the profile being driven).
- **Rename.** Choose the `✏️` icon of a tab (same glyph-as-Name pattern as `✕`: `list "✏️" buttons` numbers them in tab order; `click-nth "✏️" N`). A prompt window with the fixed title `Rename Page` appears, body `New name for '<current-name>':`, the current name pre-filled, `Cancel` + `OK`. The input control is a **nameless** `[Edit]` (a bare WPF `TextBox` exposes no UIA Name/AutomationId), so it is addressed by window title, not by needle: run `set-in "Rename Page" "<new-name>"`. It writes the window's single writable text control and prints the read-back. **Guard before committing:** only when the printed read-back equals the intended name run `click OK` (invokes the dialog's OK); if the set failed or the read-back differs, run `click Cancel` instead. A blind `click OK` commits whatever the box pre-filled (the old name: a no-op rename, but the discipline is the same for every dialog). Proof: `list "📄 " buttons` shows the renamed tab and the old-name needle no longer matches; the `ActiveWidgets` count is unchanged (rename touches no widget); after the debounced save, the page's new name appears in the profile JSON on disk.
- **Delete.** Choose the `✕` icon of a tab (every tab shows one except the very last page, which cannot be deleted. Its `✕` is simply not built). The `✕` buttons share the glyph as their UIA Name, so disambiguate by tab position: `list "✕" buttons` prints the close buttons numbered in left-to-right tab order with positions. N is the tab's position. Run `click-nth "✕" N`. Two outcomes:
  - **Empty page:** the tab closes immediately, no dialog.
  - **Page with widgets:** a `Delete Page` confirm dialog appears (title `Delete Page`, body `Are you sure you want to delete '<name>' containing K widget(s)?`, `Cancel` + `OK` buttons). Drive it with `click OK` (or `click Cancel` to abort, always the safe choice when the page is not the one you meant to delete).
  - Proof: `list "📄 " buttons` (or `find "📄 "`) lists one fewer tab; `shot <evidence>/pages/after-delete.png`. Read the profile's page list a second time (`disk-page-names`) after the debounced save as the stored-value proof.
- **Evidence.** The `list`/`find` tab reads before/after each mutation are the structural proof; the shots are the visual proof. Save them as `<evidence>/pages/<action>.txt` / `.png`.

## Gotchas

- The strip rebuilds from the profile on every mutation: names and order are the profile's truth, so read them from `list`/`find`, never assume an index.
- `✕` and `✏️` buttons carry the glyph as their UIA Name (WPF maps string content to Name). Multiple identical glyphs → `click-nth <glyph> N` with N from `list`, never a bare `click` (first match = leftmost tab).
- The only undeletable page is the lone last page: its `✕` is not built, so `list "✕" buttons` always lists exactly the deletable tabs. Deleting the ACTIVE page is legal. The selection clamps to the neighbor; expect `ActiveWidgets` to move.
- Every tab's `✕` looks the same; a wrong N deletes the wrong page. In one run a mirrored (right-to-left) tree walk turned "tab 8" into tab 1's close button. The app then correctly held the deletion behind a `Delete Page` confirm because that page held 8 widgets. Lesson: the app's delete gate is load-bearing, and `list`-then-click is the discipline that keeps N honest.
- Confirms stack: two pending `Delete Page` dialogs share the same screen coordinates; `find "Delete Page"` (or `list "Cancel" buttons`) tells you how many are pending. Cancel top-down (the first `list`/`find` match is the topmost), re-probe between cancels. A stray `click OK` against a stale confirm deletes the page that dialog names, read the dialog body before any `OK`.
- Owned dialogs (the themed prompts, `Delete Page`, `Rename Page`) hang off the **owner window's UIA subtree**, not under the desktop root. `find`/`list` (whole-app walk) reach them, root-level window searches do not. `set-in` and `click` rely on that walk; a dialog found by `find` is clickable.
- Dialog input with no UIA Name (every `DialogHost` prompt): `set <needle>` cannot address it (needle matching needs a Name/AutomationId). That is what `set-in <windowTitle> <value>` is for, and its read-back line is part of the proof: verify it before committing the dialog.
- Headless/agent sessions: the synthetic mouse is unavailable there (`SetCursorPos` fails), so the only mouse-backed command (`click-screen` for canvas pointing) refuses with a clear error rather than clicking the physical cursor position. Everything else runs through Invoke (`click`/`click-nth`), including the catalog place step (`click "BtnPlace_<pluginId>"`). A recipe step that needs a mouse command is precondition-blocked in such a session, report it as unreachable with that precondition, do not route around it with a physical mouse.
- Wheel over the strip scrolls horizontally (inverted). Scripts never scroll; if a tab is off-screen after many pages, that is a `scroll-into-view` check, not a drive step.