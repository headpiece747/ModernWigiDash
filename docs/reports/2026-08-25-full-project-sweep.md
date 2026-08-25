# 2026-08-25 full-project sweep (gate + agents + tools)

Scope: whole solution at 8e4471a, weighted on the C3-C8 refactor wave
(b73e89b..HEAD: media-session neutral seam, AudioCaptureLifecycle,
TwitchChatConnection, property commit owner, ThemeDraft, the C5 decision
models, ProfileOps import boundary, PriceMapStore, the REST live-membership
fix).

Surfaces exercised:

- Full house gate (scripts\run-gates.ps1): build 0/0, 1796/1796 tests,
  format clean, prose clean. Gate row appended.
- Glider MCP: diagnostics, unused symbols (Private,Internal and
  Public,Internal), unused parameters, unused project references,
  complexity profile, project graph (6 projects, 13 edges, zero cycles,
  edges match the CONTEXT.md allowlist).
- Agents (report-only): code-reviewer (new modules), security-auditor
  (whole project), refactor-cleaner (dead code).
- OCR plugin: preview OK (67 files); four review attempts (full range, two
  half-ranges, the C3 commit) all hit the MCP client 360-480 s per-call
  budget in this environment and returned no findings. The code-reviewer
  carried the LLM line-level review instead.
- Packages: dotnet list --vulnerable and --outdated on all six projects:
  zero vulnerable, zero outdated (16 CPM pins).
- Verification probe: a compiled .NET 10 reflection program measured the
  real WinRT enums against the repo's SDK projection
  (microsoft.windows.sdk.net.ref 10.0.19041.57, lib/net8.0).

## Critical (verified by execution)

### 1. SMTC playback-status ordinal mismatch

WinRtMediaSessionSource.cs:180-181 maps the WinRT enum to the neutral enum
by straight cast: (int)status is >= 0 and <= 5 ? (MediaPlaybackStatus)(int)status
: Unknown. The comment claims "the six named SMTC values share their
ordinals with the neutral enum". Measured:

| WinRT (Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus) | value |
|---|---|
| Closed | 0 |
| Opened | 1 |
| Changing | 2 |
| Stopped | 3 |
| Playing | 4 |
| Paused | 5 |

Neutral MediaPlaybackStatus (MediaPlaybackStatus.cs): Opened=0, Playing=1,
Paused=2, Stopped=3, Closed=4, Changing=5, Unknown=6.

Only Stopped (3) maps to itself. Consequences, all traced in code:

- OS Playing (4) maps to neutral Closed (4): NowPlayingPresentation.IsIdle
  (NowPlayingPresentation.cs:125-128) returns true for a playing track, so
  the widget draws the idle panel while music plays.
- MediaSnapshot.IsPlaying (== Playing, value 1) is true only when the OS
  reports Opened (1).
- TogglePlayPause (MediaSessionMonitor.cs:172-179) sends Play to a playing
  session (the tap inverts).
- ExtrapolatedPosition (NowPlayingPresentation.cs:163-170) never advances.

The pre-refactor widget (visible at b73e89b:ModernWigiDash.Widgets/
NowPlayingWidget.cs) compared the raw WinRT enum directly, which was
correct. The bug entered with the C3 commit (77be19c). No test can catch
it: the suite injects a fake IMediaSessionSource that already speaks the
neutral vocabulary; WinRtMediaSessionSource has no test and its core
comment is false. The repeat-mode mapping is NOT affected (verified
None=0/Track=1/List=2 on both sides).

Fix: replace the cast with an explicit name-based switch (or re-declare
the neutral enum in the real ordinal order), and add a pin asserting the
mapping against the real enum so the assumption is machine-checked.

## Minor (new code)

2. ProfileOps.ImportProfileFile (ProfileOps.cs:494-511) +
   ProfilePersistence.Load (ProfilePersistence.cs:57): the boot-load path
   lost its exception contract. FileInfo.Exists/Length sit outside the
   boundary's try, and the caller no longer wraps the call, while the doc
   comment still promises "Returns null when absent, oversized, corrupt,
   or unparseable". An UnauthorizedAccessException/IOException on the
   profile path now crashes startup instead of falling back. Move the
   exists/length checks inside the try.
3. WasapiLoopbackCaptureSource.Start (AudioCaptureSource.cs:54-77): the
   half-opened capture is leaked on a StartRecording() failure (_capture
   is assigned only after success; the lifecycle's source?.Dispose()
   releases a null). One WASAPI capture leak per persistent-failure retry.
4. ModernWidgetBase.SetProperty (ModernWidgetBase.cs:120-145): the comment
   claims a miss sentinel caches the miss once, but
   ConcurrentDictionary.GetOrAdd never stores a null factory result, so a
   missing property re-runs reflection and writes a FileLog line on every
   call.
5. PriceFeedManager.ReleaseSubscription (PriceFeedManager.cs:357-363):
   non-atomic read-decrement of the ref count (latent; all production
   callers route through the UI thread today).
6. PriceFeedManager.EnsureActive (PriceFeedManager.cs:296-308): a
   double-swap on a cancelled CTS can retire the wrong instance, and its
   30 s deferred Dispose can dispose a source in-flight work still holds
   (latent).
7. MediaSessionMonitor.RefreshAsync (MediaSessionMonitor.cs:237):
   ++_refreshVersion is non-atomic under SMTC continuation threads; a lost
   increment lets an older snapshot overwrite a newer one for one frame.
8. WinRtMediaSessionSource adapters (WinRtMediaSessionSource.cs:38-43,
   77-83) never unsubscribe the underlying WinRT events: each disposed
   monitor's adapter stays rooted on the process-lifetime manager until
   exit (slow object leak on NowPlaying add/remove; no callback storm).
9. ArtworkLoader.WinRtArtworkDecoder (ArtworkLoader.cs:226-236): 10 MB
   input cap but no decoded pixel-footprint cap (MediaDecoder's
   MaxPixelsPerFrame standard is skipped here).
10. SvgIconLoader (SvgIconLoader.cs:71): XDocument.Load is unguarded; a
    malformed .svg throws XmlException into the render path instead of
    degrading to no icon (not XXE: the default reader prohibits DTDs).
11. TwitchChatConnection.Start (TwitchChatConnection.cs:126-146): the
    message buffer is cleared before the old loop's bounded dispose, so an
    in-flight dispatch can append one last line from the old channel into
    the fresh buffer.
12. DeviceAuthorizationModel ctor: no null guard on verificationUri /
    userCode; a null Uri NREs at VerificationText/OpenBrowser instead of a
    logged refusal.

## Clean (verified, new modules)

PriceMapStore (merge rules match spec exactly), the SetWidgetProperty
commit owner (both write paths wired, re-entrancy guard at the funnel),
ThemeDraft (parse-only apply), IconPickerModel / IconValuePolicy /
DeviceAuthorizationModel (trusted-open gate delegates to the shared
TrustedUriPolicy), AudioCaptureLifecycle (lock protocol, watchdog prime,
deferred-stop marshaling; also fixed a pre-existing failure-leg bug),
TwitchChatConnection (single volatile ChatState owner, clamped buffer,
bounded backoff; PONG token retirement is an improvement over the deleted
widget code), ProfileImportOutcome boundary (named verdicts, size guard
before any read), RestPollLoop + the kind wiring table (fail-loud, live
membership view, per-symbol isolation).

## Dead code (refactor-cleaner, reference-count proven)

Confident:

- 9 CS8019 unused usings across 7 files (the solution's only compiler
  warnings): WinRtMediaSessionSource.cs:4 (src, C3 residue),
  DeviceAuthorizationModelTests.cs:2, FramePipelineAllocationTests.cs:1,4,
  IconPickerModelTests.cs:1-2, IconValuePolicyTests.cs:1-2,
  ModernWidgetBaseTests.cs:2 (the four new test files re-declare usings
  the Tests csproj already publishes as project globals).
- TwitchFollowedStreamResponse.UserId: dead JSON DTO field.
- FrameTimeStore.DefaultMaxAge and LhmSensorStore.DefaultMaxAge: dead
  public accessors (the facade's own DefaultMaxAge is the live one).
- ArtworkLoader.ArtworkChanged carries an ArtworkLoaded? payload its sole
  subscriber never reads (NowPlayingWidget.OnArtworkChanged ignores it;
  render reads Current directly).

Needs a human call:

- TwitchTokenValidation.Login: 0 references (the 5-slot shape is pinned by
  3 test constructors + 1 production site).
- Optional API-mirror trims: 16 unused PmStatus members; 4 trailing
  PmIntrospection fields (trailing-only trim is ABI-safe).

Verified NOT dead (false positives cleared): ProfileImportOutcome.Loaded
.Profile (consumed through deconstruction patterns, which the reference
count misses), PageLayout.PageId / ProfileLayout.ProfileId (JSON export
schema), ProcessEntry32 / WinUsbNative / HotkeyActionExecutor interop
struct fields (marshaled-size load-bearing), 41 event-handler parameters
(XAML/WinRT/framework shapes), 6 test-only seams.

Totals: 0 unused project references, 0 unused package references, 0 dead
private helpers (DebtGuard pin green, confirmed by the Private,Internal
sweep).

## Security (security-auditor, whole project)

No critical or major findings. Verified clean: no hardcoded secrets
(TwitchTokenStore DPAPI CurrentUser + entropy, Finnhub key from env,
Twitch client id from config), the shell-open trust gate (apex +
dot-prefixed subdomain rule, gated before Process.Start on both paths),
the profile-import sanitizer at its single boundary, URL construction
(EscapeDataString or validated symbols only), LogLine flatten/bound/redact
at the FileLog seam, the USB frame size single-sourced from DisplayGeometry,
MediaDecoder bomb caps.

Findings: the two minors above (9, 10) plus five info notes: the update
digest comes from the same GitHub API payload that hosts the asset
(standard digest-from-API model; TLS + IsTrustedAssetUrl are the controls);
the app's own trusted profile load skips the sanitizer (inherent to the
Hotkey feature; optional hardening: re-apply the safe-path rule); the
Finnhub key rides the WS/REST query string per the vendor contract and is
verified absent from logs; MediaDecoder's null-codec fallback path is
effectively unreachable (32 MB file cap still bounds it); and
TwitchAuthenticationService.cs:559-560 re-spells the https scheme check
inline instead of routing through the shared TrustedUriPolicy (candidate
for a single Sdk-level IsTwitchAuthorizationUri(Uri) gate).

## Complexity (informational)

Top methods are pre-existing: MainWindow.Connect (37, XAML-generated
partial), RepoScan.StripCode (31) and DebtGuardTests (21/23, test infra).
None of the new modules appears in the high-complexity set.

## Note

At the time of the sweep, the branch was 1 commit ahead of origin/master
(the skills.sh registry commit, 8e4471a) and not pushed. The state after
the fix wave is recorded in the Resolution and Review follow-up sections.

## Resolution (2026-08-25, all findings fixed on master)

Every finding above is fixed, one commit per finding (minor 5 and 6 share
one commit: both are the feed-subscription bookkeeping), each verified by
the full house gate (build 0/0, the complete suite, format, prose). The
test count went 1796 to 1810 (+14 new pins).

| Finding | Commit |
|---|---|
| Critical 1 (SMTC ordinal mismatch) | f79fca7 |
| Minor 2 (boot-load boundary contract) | 7524acb |
| Minor 3 (half-opened WASAPI capture leak) | a4e501e |
| Minor 4 (property-miss re-log per call) | c1a855c |
| Minor 5 + 6 (subscription ref count, CTS swap) | ed88b4d |
| Minor 7 (non-atomic refresh version) | b93a47d |
| Minor 8 (WinRT adapter subscription leak) | 0b9d70b |
| Minor 9 (artwork pixel-footprint cap) | 08849f0 |
| Minor 10 (unguarded SVG parse) | 4e1ea46 |
| Minor 11 (chat restart buffer/loop order) | 3c66dda |
| Minor 12 (auth model null guards) | 8c1fb94 |
| Info note (inline https check) | bf100e4 |
| Dead code (usings, DTO field, store accessors, ArtworkChanged payload) | e445f87 |

Skipped with reasons (judgment calls, not defects):

- TwitchTokenValidation.Login (0 references): the 5-slot wire-schema shape
  is pinned by 3 test constructors + 1 production site; trimming it is a
  contract decision, not a cleanup.
- PmStatus / PmIntrospection mirror trims: the fields mirror the P/Invoke
  service struct; a trailing-only trim is ABI-safe but buys nothing.
- Update digest-from-API: the standard digest-from-API model; TLS +
  IsTrustedAssetUrl are the controls.
- Trusted boot load skipping the import sanitizer: inherent to the Hotkey
  feature; re-applying the safe-path rule would break user absolute image
  paths.
- MediaDecoder null-codec fallback: effectively unreachable; the 32 MB
  file cap still bounds it.
- Finnhub key in the WS/REST query: per the vendor contract; verified
  absent from logs.

Dead-code nuance: the "9 CS8019 unused usings" were IDE diagnostics, not
batch-compiler warnings (a full dotnet build --no-incremental reports 0
warnings); they were nonetheless true P1 global-usings convention
violations (five test files re-declaring the Tests csproj globals, plus
one genuinely unused Windows.Storage.Streams using in
WinRtMediaSessionSource), and e445f87 removed them. The ArtworkChanged
payload shrink re-based the two test pins on Current plus an event
counter, keeping the same guarantees (a key change reloads and raises; a
late stale load is processed but never replaces the published artwork).

Branch state: the resolution wave was pushed (15 commits through
3e69fbf); the review follow-up below is its final commit.

## Review follow-up (2026-08-25, post-push)

The code-reviewer pass over the pushed range (8e4471a..3e69fbf) found
one residual leak leg the wave's own pin could not represent: the stub
seam hands back the same instances, so the fresh-wrapper shape was
untestable. CycleSession excluded the departing session's fresh
wrapper on the premise that AttachSession releases it, but the attach
releases only the previously held adapter, so on the production WinRT
seam (a fresh adapter per session per GetSessions() call) the
departing wrapper, with its three live subscriptions, stayed rooted
per badge tap. The loop now disposes every fresh wrapper that is
neither the arriving one nor the held instance the attach releases
(both seams dispose each wrapper exactly once), pinned by a
FreshSessionWrapper double plus the manager's FreshWrapperFor seam
knob; the AudioCaptureLifecycle comment that still described the
pre-a4e501e half-opened-capture leak was updated in the same commit.