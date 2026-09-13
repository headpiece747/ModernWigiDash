# Spotify widget is separate and Web-API-backed

## Status

Accepted (2026-09-13)

## Context

The existing Now Playing widget reads the system's SMTC (System Media Transport
Controls) session passively: it shows whatever app is playing, with no user control.
A Spotify-specific widget that offers explicit play/pause/skip/next-track and playlist
navigation requires the Spotify Web API, which in turn requires a user OAuth token.
SMTC alone cannot provide these capabilities.

Building the Spotify feature as a fork of the Now Playing widget would couple two
different data sources (SMTC vs. Spotify Web API) behind one type, making both harder
to test and to evolve independently. The house pattern for distinct data sources is a
separate widget per source (the Hardware Monitor vs. Frame Time precedent).

## Decision

- A **separate** `SpotifyWidget` (plugin id `spotify`, category "Media & Audio")
  coexists with the passive-SMTC Now Playing widget by data source, not copy.
- Authentication uses the Spotify **device flow** (no loopback listener), reusing
  the Twitch token-store (DPAPI), device-authorization dialog, and trusted-URI
  policy seams. The client id is a public app identifier stored as a widget property
  or read from an environment variable; it is not a secret.
- The Web API provides now-playing state (`GET /me/player`), playlists
  (`GET /me/playlists`), and playback control endpoints. Access tokens are refreshed
  lazily on expiry/401 via `EnsureAccessTokenAsync`.
- The widget defaults to a full-page `GridSizePreset.Size5x4` (1016×592) because the
  album art + title + artist + progress bar layout needs the space; it shrinks via
  the existing resize handles.
- No Spotify logo or wordmark assets (trademark). Own iconography only; the Spotify
  green `#1DB954` is used as an accent color.
- When no session is active (not logged in, or the Web API returns 404 for no
  current player), the widget renders the house placeholder ("Spotify not connected").

## Consequences

- Two media widgets can be placed on different pages: the passive one shows any
  SMTC source, the Spotify one shows explicit Spotify control.
- The device-flow auth surface (token store, dialog, trust predicate) is shared with
  Twitch; a new trusted-host predicate (`IsSpotifyAuthorizationUri`) is added to
  `TrustedUriPolicy`.
- The Spotify Web API requires a registered application (client id); the widget
  degrades gracefully when the property is empty.
