# Calendar widget: feed editor

The Calendar widget's inspector exposes a **Feeds** section: one row per feed
(kind combo, label, the kind's connection field(s), an enable checkbox, a
remove button) plus an `Add feed` button. Editing a field commits the whole
list as a single `FeedsJson` string through the inspector write-back funnel;
the model drops incomplete feeds, so a half-entered row never persists a broken
feed. A CalDAV feed's password is machine-local (DPAPI credential store), never
in the profile.

## Sub-features

- `feed-add` adds an empty feed row via `Add feed`; the row's controls appear.
- `feed-edit` sets a feed's URL / label / connection fields; the change commits
  and persists to `profile.json`.
- `feed-kind-swap` switches a row between `ics` and `caldav`; the connection
  fields swap (a `.ics URL` box vs. Server / Principal path / Username /
  Password).
- `feed-persist` proves the edited feed reached `profile.json`'s `FeedsJson`.

## How to get to it (user POV)

- Place + select the Calendar widget (see `place-widget.md`).
- In the inspector's Feeds section, choose `Add feed`, then fill the row.

## Driving it with wmd-verify

Preconditions:

- The app passed `doctor`; `backup-profile` ran.
- A Calendar widget is placed and selected (its Feeds section is showing).

**Feed-row control ids (UIA reality):** every feed-row control carries a
deterministic AutomationId of the form `CalFeed<Role>_<index>` (index = the
row's position, 0-based): `CalFeedKind_<i>` (combo), `CalFeedLabel_<i>`,
`CalFeedUrl_<i>` (ics) or `CalFeedServer_<i>` / `CalFeedPrincipal_<i>` /
`CalFeedUsername_<i>` / `CalFeedPassword_<i>` (caldav), `CalFeedEnabled_<i>`
(checkbox), `CalFeedRemove_<i>` (button). Address them by id with `set` /
`value` / `click` -- they are unique per row and never collide with other
inspector controls.

- **Add a feed.** Run `click-nth "Add feed" 1` (the button is the first match;
  see the needle gotcha below). Proof: `list CalFeedUrl_0` reports one Edit
  (the new ics row's URL box).
- **Edit the feed.** Run `set CalFeedUrl_0 <https-url>` and
  `set CalFeedLabel_0 <label>`. Proof (read-back): `value CalFeedUrl_0` returns
  the URL. Proof (persist): after the debounce (~2 s), read
  `%LOCALAPPDATA%\ModernWigiDash\profile.json` and confirm the selected
  calendar widget's `propertyValues.FeedsJson` carries the serialized feed
  (`[{"kind":"ics","url":"...","label":"...",...}]`). This is the load-bearing
  assertion: before the PROPFIND-template fix, this read stayed `[]` because
  the commit aborted before persistence.
- **Swap to CalDAV.** Open the kind combo's dropdown and pick `caldav` (see the
  kind-combo gotcha for the exact steps). Proof (swap): `list CalFeedServer_0`,
  `CalFeedPrincipal_0`, `CalFeedUsername_0`, and `CalFeedPassword_0` each report
  one Edit, and `list CalFeedUrl_0` reports no match (the ics box was replaced).
  Then fill the triple (`set CalFeedServer_0 <https-server>`,
  `set CalFeedPrincipal_0 <path>`, `set CalFeedUsername_0 <user>`) and re-read the
  profile: `FeedsJson` now carries `[{"kind":"caldav","server":"...","principalPath":"...","username":"...",...}]`.
  Proof (secret separation): the raw profile JSON contains no `password` string
  (the CalDAV password is machine-local in the DPAPI credential store, never the
  traveling profile).
- **Evidence.** Save the `value` read-backs, the persist-read JSON, and a
  `shot <evidence>/calendar-live/feed-editor/feed-row-populated.png` (and
  `caldav-row.png` for the swapped row).

## Gotchas

- **`Add feed` collides with `+ Add Page`.** A bare `click "Add feed"` matches
  both the feed button and the page-tab strip's `+ Add Page` (contains-match on
  "add"). Use `click-nth "Add feed" 1` and route spaced needles through
  `cmd /c 'powershell ... click-nth "Add feed" 1'` so the quotes survive. A
  stray `Page N` appearing means you clicked the page button; delete it and
  retry.
- **The kind combo's items are virtualized.** WPF realizes a ComboBox's items
  only when the dropdown opens, so a UIA `SelectionItemPattern.Select` on a
  `caldav` child fails (item-not-found, or no selection pattern) until the
  dropdown is open. To drive the swap: expand the combo via its
  `ExpandCollapsePattern` (the harness does not script this; a small ad-hoc
  UIA script does), wait ~700 ms for the items to realize, then find the
  realized item named `caldav` and click it at its bounding-rect center (a
  direct mouse click selects; the item exposes no `SelectionItemPattern`).
  Verify the swap by the field-swap proof above, not by the combo's reported
  selected value.
- **Persistence is debounced.** Read `profile.json` ~2 s after the last edit;
  reading immediately can show the pre-edit value.
- **A bad feed host degrades, it does not crash.** An unresolvable URL logs a
  `[CALENDAR] <label>: fetch failed, keeping last-known events (...)` line and
  the widget renders its unavailable display (ADR-0017). That log line is the
  correct Simulated-mode behavior, not a failure -- and it confirms the producer
  started cleanly (a `TypeInitializationException` here would instead abort the
  commit, leaving `FeedsJson` at `[]`).
