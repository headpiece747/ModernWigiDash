# Security Scan Report

**Project:** ModernWigiDash | **Date:** 2026-08-25 | **Scanner:** static analysis (opencode + Glider semantic tools + GitHub Advisory Database + SkillSpector v2.9.6)
**Baseline:** HEAD 3d4a7cc (one commit over the 8cf016c baseline; the added commit 3d4a7cc binds the device-auth browser open behind a test seam). No source was modified by this scan.

> This is a static analysis scan. It catches known patterns but does not replace penetration testing, dynamic analysis, or threat modeling. The app has no network endpoints, so several web-shaped checks are N/A and are marked as such.

## Summary

| Severity | Count |
|----------|-------|
| Critical | 0 |
| High | 0 |
| Medium | 0 |
| Low | 0 |
| Info | 4 |

The four deliberate designs named in the task (untrusted-import sanitization at the profile import boundary, DPAPI for the Twitch token, the TrustedUriPolicy shell-open gate, protocol constants in DisplayProtocolConstants) were verified against the code and are consistent. Their existence is not reported as findings; the verification evidence is in the layer sections below.

## Findings

No findings at Medium or above.

### [INFO] Sdk/TrustedUriPolicy.cs:19-22:31 - Shell-open gate reads Uri.Host, so userinfo-carrying URIs pass

`IsTwitchAuthorizationUri` checks the scheme is https and that `uri.Host` is the twitch.tv apex or a dot-prefixed subdomain. A URI of the form `https://user@twitch.tv/...` has `Host == "twitch.tv"` and passes; the shell then opens the full URI in the default browser, which discards the userinfo component. Reaching this shape requires the verification URI to already come from a TLS session to id.twitch.tv that an attacker controls, which is outside the gate's documented threat model (a tampered or attacker-supplied verification URL). No change required; recorded so the nuance is on the record.

### [INFO] App/ProfilePersistence.cs:57-60 - The app's own profile.json loads as trusted input

`ProfilePersistence.Load` calls `ProfileOps.ImportProfileFile(_profilePath, loader, context, trusted: true)`, so the untrusted-import rules (ActionCommand clear, path restrictions, InstanceId regeneration) are skipped for the app's own persisted file, by design: applying them would wipe the user's own configured command and absolute paths on every restart. An attacker who can write `%LocalAppData%\ModernWigiDash\profile.json` already has code execution as the same user the app runs as (the app does not elevate), so the skipped rules would not add defense against that actor. The untrusted boundary (a foreign profile file the user picks) is sanitized; see Layer 3.

### [INFO] Directory.Packages.props:15 - MessagePack 3.1.8 sits just above the June 2026 coordinated patch line

Fourteen MessagePack advisories (CVE-2026-48510 through CVE-2026-48517, published 2026-06-25) affect nuget MessagePack `>= 3.0, < 3.1.7` on the 3.x line; the pinned 3.1.8 is above the patch line and not affected. Usage is typed deserialization only (`MessagePackSerializer.Deserialize<List<IndexEntry>>` over a `[MessagePackObject]` record, App/LibreHardwareService/LhmSharedMemoryReader.cs:171, 260) with no typeless API, so the vulnerable surface (typeless and JSON-conversion paths) is not exercised. Keep an eye on the 3.1.x line.

### [INFO] App/LibreHardwareService/LhmSharedMemoryReader.cs - Sensor data crosses a named local resource

The LibreHardwareService shared-memory maps (`sensors`, `status`) are named local resources. A malicious same-user process that creates the named map first could supply fabricated sensor values, which render only in the local hardware-monitor widget. The reader takes one mutex-guarded bounded copy with attacker-claimed-size caps, and the source is a LocalSystem service the user installed deliberately. This is local impersonation of a user-chosen service, not a remote vector; recorded for the record.

## Layer Results

| Layer | Status | Findings |
|-------|--------|----------|
| 1. Package Vulnerabilities | PASS | 0 vulnerable versions across the 16 pinned packages |
| 2. Secrets Detection | PASS | No hardcoded credentials or keys; DPAPI review clean |
| 3. OWASP Code Patterns | PASS | Injection, traversal, and deserialization boundaries verified |
| 4. Auth Configuration | PASS | Desktop device flow; shell-open gate verified at both sites |
| 5. CORS Policy | N/A | No web server; the app is only an HTTP/WebSocket client |
| 6. Data Protection | PASS | Token DPAPI-protected; log redaction verified; no PII/keys in logs |
| 7. Skill Supply Chain | PASS | 2 trees, 148 files, 100% coverage; 40 issues, all triaged, none in the BLOCK class |

## Layer 1: Package Vulnerabilities (A03:2025)

Method: inventory from Directory.Packages.props (central package management) checked against the GitHub Advisory Database on 2026-08-25. Per the task constraints, no restore/build ran, so the NuGet vulnerability database behind `dotnet list package --vulnerable` was not consulted; the transitive set was inferred and the one non-trivial transitive (NAudio.Core) was checked directly.

Direct packages and verdicts:

| Package | Pinned | Advisory verdict |
|---------|--------|------------------|
| SkiaSharp, SkiaSharp.Views.WPF | 4.151.1 | 1 advisory (CVE-2023-4863, libwebp OOB write) affects nuget SkiaSharp `>= 2.0.0, < 2.88.6`; out of range |
| NAudio.Wasapi | 3.0.1 | 0 advisories |
| NAudio.Core (transitive) | via NAudio.Wasapi | 0 advisories |
| MessagePack | 3.1.8 | 14 advisories (CVE-2026-48510..48517) affect 3.x `< 3.1.7`; 3.1.8 is patched (see INFO finding) |
| LibUsbDotNet | 3.0.224 | 0 advisories |
| System.Security.Cryptography.ProtectedData | 10.0.11 | 0 advisories |
| Microsoft.Extensions.Logging.Abstractions | 10.0.11 | 0 advisories |
| Microsoft.Extensions.TimeProvider.Testing | 10.9.0 | test-only, not shipped |
| Microsoft.NET.Test.Sdk | 18.9.0 | test-only, not shipped |
| MSTest.TestAdapter / TestFramework | 4.3.3 | test-only, not shipped |
| AsyncFixer, Meziantou.Analyzer, Roslynator.Analyzers, SonarAnalyzer.CSharp, coverlet.collector | - | analyzer/tooling-only, `PrivateAssets=all`, never ship in the app |

## Layer 2: Secrets Detection

Scanned all `.cs`, `.json`, `.xml`, `.config` in the repo for the high-confidence and medium-confidence patterns (connection strings with passwords, Bearer literals, private-key blocks, AWS key IDs, `ApiKey`/`Secret`/`Token` literal assignments, base64 runs over 40 characters, `.env` files).

- No matches in source. The only long hex literals are SHA-256 test fixtures (Tests/UpdateCheckerTests.cs:48, 150, Tests/UpdateServiceTests.cs:350).
- No `.env` file exists.
- The Finnhub API key comes from the `FINNHUB_API_KEY` environment variable or an explicit constructor argument, never from source (Widgets/PriceFeedManager.cs:136-141; an unset key disables the stock feeds with a log line that carries no value).
- The Twitch Client ID comes from a widget property or the `MODERNWIGIDASH_TWITCH_CLIENT_ID` environment variable (Widgets/Twitch/TwitchAuthenticationService.cs:89, 540). A Twitch Client ID is public identifier material, not a secret.
- Test fixtures use obviously fake values (`access-token-123`, Tests/TwitchTokenStoreTests.cs:14-15); skipped per the false-positive rule.

DPAPI review (Widgets/Twitch/TwitchTokenStore.cs):

- `ProtectedData.Protect/Unprotect` with `DataProtectionScope.CurrentUser` plus a fixed entropy string (line 14, 39-55, 81-85). The entropy is a domain-separation constant, not a secret.
- Atomic write: temp file with a random GUID name, `File.Move` overwrite, temp deleted in `finally` (lines 86-97).
- ACL hardening after the move: protected SACL, single FullControl rule for the current user, best-effort with a debug line on failure (lines 100-133).
- Failure handling is fail-closed: unprotect failure resets the session to a fresh login (lines 39-51).
- `Delete` is best-effort on logout (lines 137-146).

Verdict: clean.

## Layer 3: OWASP Code Patterns

There is no database (no SQL surface) and no HTML output path (no XSS surface; WPF text rendering). The relevant surfaces are injection into external protocols, filesystem traversal, and deserialization of untrusted data.

### Path traversal

- **Profile import boundary** (Core/Models/ProfileImportSanitizer.cs): `SafeRelativePath` (lines 198-208) rejects rooted, drive-relative (`C:foo`), UNC, and any `..` segment; page-background and widget image/icon paths are restricted through it. The path-property set (`ActionPropertyKeys`, lines 41-46) is pinned by Tests/ProfileSanitizerDriftTests.cs against the Widgets assembly's `[WidgetProperty(Path)]` declarations, so a renamed path property fails the build instead of disarming the guard. `ActionCommand` (the Hotkey widget's `Process.Start`/`SendInput` value) is cleared unconditionally on import, including whitespace-only values (lines 146-164). `InstanceId` is regenerated unless it is a safe ASCII token not already claimed by another widget in the same import (lines 194-207), because widgets key cache file names by it.
- **Weather cache file key** (Widgets/WeatherForecastWidget.cs:219-222): defense in depth behind the import boundary. `SafeCacheToken` re-checks `ProfileImportSanitizer.IsSafeInstanceId` and falls back to a per-instance GUID, so a path-bearing id can never reach the `weather_{token}.json` name builder.
- **Update zip extraction** (App/Update/UpdateService.cs:196-206): every zip entry is resolved and must stay under the stage directory before `ExtractToDirectory` runs (zip-slip guard, defense-in-depth behind the digest gate).
- **Picture/icon file loads**: paths are user-selected widget properties in a single-user app; the import boundary sanitizes foreign values. Same trust domain as the local user.

### Command / protocol injection

- **Hotkey widget** (Widgets/HotkeyActionExecutor.cs:179-189): `Launch` runs the user's own property values (their machine, their intent); `OpenUrl` is gated to http/https/mailto by `HotkeyActionPolicy.IsAllowedUrl`. The import boundary clears the command, so a foreign profile cannot arm execution.
- **Update launch** (App/Update/UpdateService.cs:169-213): the staged batch is re-verified against the SHA-256 stamp written at staging before launch (lines 180-188); the relaunch path is built with `Path.Combine` (no doubled separators); the stage directory derives from the release version, which is reconstructed from parsed integer SemVer fields (App/Update/UpdateChecker.cs:29-35), so it cannot carry path or command metacharacters.
- **IRC channel injection** (Sdk/TwitchChannelRule.cs): the JOIN target must be within 25 characters and free of CR/LF; the rule runs at import (Core/Models/ProfileImportSanitizer.cs:166-172) and again at connect in the widget.
- **URL building**: user-derived values are `Uri.EscapeDataString`-encoded (Widgets/WeatherLocationResolver.cs:215-217, 237; Widgets/Twitch/TwitchApiClient.cs:153-155). The ticker symbol embedded in the Finnhub and Yahoo REST URLs passes `SymbolCatalog.IsValidSymbol` (1-32 chars, `[A-Za-z0-9.:-]`, Widgets/SymbolCatalog.cs:122-123) before the URL is built (Widgets/FinnhubRestLeg.cs:21, Widgets/YahooChartRestLeg.cs:21), so `&`, `?`, `%`, and `/` cannot inject query parameters.

### Deserialization of untrusted data

- All JSON parsing uses System.Text.Json into typed models; no BinaryFormatter, no Newtonsoft, no type-name handling anywhere in the repo.
- Profile import: size-guarded (10 MB cap before any read, Core/Models/ProfileImportSanitizer.cs:31), parsed through the single boundary `ProfileOps.ImportProfileFile` (Core/Models/ProfileOps.cs), then sanitized per Layer 3 above.
- Weather cache: 1 MB size bound, bounded read, System.Text.Json into private records (Widgets/WeatherCacheStore.cs:21, 51-116, 190-229); `WeatherForecastParser` reads only the properties it names (Widgets/WeatherForecastParser.cs).
- Price/geocoding responses: parsed field-by-field from read-only `JsonElement` (Widgets/PriceFeedMessages.cs, Widgets/WeatherLocationResolver.cs); a malformed payload yields a null sample, never an exception path that crosses the trust boundary.
- MessagePack: typed deserialization only, from the LibreHardwareService local map (see INFO finding). No typeless API.
- Media decode: the picture widget's `MediaDecoder.Decode` enforces bomb caps (32 MB file, 256 frames, 4096 px/frame, 512 MB total) with every partial bitmap disposed on failure (Widgets/MediaDecoder.cs); a refused decode retires the previously installed media.

### Cryptographic usage

- SHA-256 with `CryptographicOperations.FixedTimeEquals` for the update digest comparison (App/Update/UpdateService.cs:79-91, 209-212); digest comparison is case-insensitive hex over decoded bytes.
- DPAPI for the token (Layer 2). No MD5/SHA1/ECB/TripleDES usage in code (the two `ECB` text hits are the European Central Bank FX rate source name, Widgets/FrankfurterRestLeg.cs:4, Widgets/PriceFeedMessages.cs:234).

## Layer 4: Auth Configuration

No endpoints exist, so the auth surface is the Twitch OAuth2 device flow (client-side) and the shell-open gate.

- **Endpoints**: all five Twitch endpoints are https on `id.twitch.tv` / `api.twitch.tv` (Widgets/Twitch/TwitchApiClient.cs:15-19). No plaintext transport.
- **Client ID**: widget property or `MODERNWIGIDASH_TWITCH_CLIENT_ID` env var (Widgets/Twitch/TwitchAuthenticationService.cs:89, 540). Public identifier material; a missing one fails with a message that names both sources and leaks nothing.
- **Token storage**: DPAPI CurrentUser + entropy + ACL hardening + atomic write (Layer 2). The device-flow user code is displayed in the authorization window, which is the point of the flow; it is never written to the log (the only FileLog writes in the Twitch folder are the token store's failure lines, Widgets/Twitch/TwitchTokenStore.cs:39-50).
- **Token lifecycle**: validation tick, refresh, and revoke route through the client (Widgets/Twitch/TwitchApiClient.cs:15-19); logout revokes through the `revoke` endpoint.
- **Shell-open gate, verified consistent with CONTEXT.md**: `TrustedUriPolicy.IsTwitchAuthorizationUri` (Sdk/TrustedUriPolicy.cs:19-22) requires https and a host that is exactly `twitch.tv` or ends with `.twitch.tv` (dot-prefixed, case-insensitive), so `faketwitch.tv` and `evil-twitch.tv` are refused. Both production shell-open sites route through the composite: `DeviceAuthorizationModel.OpenBrowser` (App/DeviceAuthorizationModel.cs:51-58, refusal logged, the open seam never runs) and `TwitchSession.TryOpenBrowser` (Widgets/Twitch/TwitchAuthenticationService.cs:561). Pinned by Tests/TrustedUriPolicyTests.cs, including the lookalike-suffix and non-https rejections. One nuance is recorded as an INFO finding (userinfo-carrying URIs).

## Layer 5: CORS Policy

N/A. The application hosts no HTTP server and exposes no endpoint; it is a client of public APIs (Open-Meteo, Twitch, Finnhub, Binance, CoinGecko, Frankfurter, Yahoo) and of local WebSocket/IRC connections. There is no `CorsPolicy`, `AllowAnyOrigin`, or origin-configuration surface anywhere in the source.

## Layer 6: Data Protection

- **Twitch token at rest**: DPAPI-protected, CurrentUser scope, ACL restricted to the current user (Layer 2).
- **profile.json at rest**: `%LocalAppData%\ModernWigiDash\profile.json` (App/ProfilePersistence.cs:15-16, 53-56), plaintext, per-user directory. Contents are layout, positions, theme colors, and widget property values; it carries no credentials (the Finnhub key is env-var-only and the Twitch token is DPAPI-protected). The `trusted: true` load trade-off is recorded as an INFO finding.
- **Weather cache at rest**: `weather_cache\weather_{safe-id}.json` next to the executable (Widgets/WeatherForecastWidget.cs:276, 184; Widgets/WeatherCacheStore.cs:16-41). Weather data plus the location query key; no credentials. Bounded 1 MB read.
- **Logs**: `display_device.log` next to the executable (Sdk/FileLog.cs:63). Every line passes `LogLine.Sanitize` (Sdk/LogLine.cs:57-69): newlines flattened, value bounded to 2000 characters, URL query strings stripped, and credential-shaped params (`access_token`, `refresh_token`, `device_code`, `token`) redacted to `token=<redacted>`. Verified against the one secret that rides a URL: the Finnhub key in the REST quote URL (Widgets/FinnhubRestLeg.cs:21). The REST poll cycle logs only the sanitized symbol, never the URL (Widgets/RestPollLoop.cs:59), and `PriceRestLeg` logs nothing (0 matches), so the key cannot reach the log through the price-feed path; `LogLine` is the backstop for any other path.
- **Update integrity**: SHA-256 digest verified before extraction (App/Update/UpdateService.cs:135-143), 500 MB download size cap enforced per write (lines 236-238, 245-262), asset URL host-gated to `github.com` / `objects.githubusercontent.com` over https (App/Update/UpdateChecker.cs:55-73), staged cmd re-hashed before launch (lines 180-188).
- **No PII in logs**: log statements carry widget names, tags, exception messages, and sanitized user values; no emails, tokens, or codes.

## Layer 7: Skill Supply Chain

Tool: `C:\Users\tobia\.local\bin\skillspector.exe` v2.9.6, `--no-llm` (static only; skill content does not leave the machine), 2026-08-25. Both trees at 100% inspection coverage.

### Project tree: `.opencode\skills` (53 skills, 95 files)

36 issues, no issue in the BLOCK class (no env-secret-to-network taint chains, no YARA malware matches, no obfuscated execution with a network or encoded source, no shipped bytecode, no new credentials). Triage against the house verdict table (docs/reports/2026-08-23-agent-hygiene-scan.md):

| Pattern | Location(s) | Verdict |
|---------|-------------|---------|
| RA1, RA2, AS3, EA1 | reflect/references/*.md | EXPECTED: skill-layer maintenance skill; "edit skill" / "clear state" prose, cross-skill reads are its stated job |
| P2 | desloppify/SKILL.md:10 | EXPECTED: machine-managed version marker block |
| TM1 | desloppify/SKILL.md:286; security-scan/references/scan-layers.md:218 | EXPECTED: quoted `rm -rf /tmp/...` command examples in docs |
| E1 | httpclient-factory/SKILL.md:30, 55, 175, 188, 195 | EXPECTED: `api.example.com` / `api.test` example endpoints |
| AST4 | ablate-ai-layer/scripts/run_ablation.py:47-48, 153-155, 189-193 | EXPECTED: git subprocess is the script's stated job |
| RP1 | desloppify/SKILL.md:294 | EXPECTED: documented `uvx-git` prerequisite |
| EA2 | poteto-mode/SKILL.md:77; security-scan/references/scan-layers.md:221 | EXPECTED: the never-block-on-the-human principle |
| MP3 | security-scan/references/scan-layers.md:213, 215; show-me-your-work/SKILL.md:53 | EXPECTED: negative-form state constraints ("clear state", "never edit or delete history") |
| MP3 | create-verification-skill/references/feature-map-example/README.md:47 | INTENTIONAL (new this run): the word "persistence" in an example feature-map description; no memory-manipulation instruction. Verified the line |
| P2 | project-structure/SKILL.md:72, 87, 102, 160 | INTENTIONAL (new this run): XML comments inside the illustrative `.props`/`.csproj` code blocks ("Versions below are illustrative", "No TargetFramework here"). Verified all four lines; no hidden instructions |

### Global tree: `C:\Users\tobia\.config\opencode\skills` (19 skills, 53 files)

4 issues, none in the BLOCK class:

| Pattern | Location | Verdict |
|---------|----------|---------|
| P6 | diagnosing-bugs/scripts/hitl-loop.template.sh:10 | INTENTIONAL (new this run): the line is a comment documenting the `step` template helper ("show instruction, wait for Enter"). Verified the file; no prompt extraction |
| TM2 | to-tickets/SKILL.md:40 | INTENTIONAL (new this run): prose about ticket "blocked by" edges in wide-refactor sequencing. Verified the line; no tool chaining |
| EA2 | grilling/SKILL.md:26 | EXPECTED: autonomous-interview style, the skill's stated job |
| EA2 | setup-matt-pocock-skills/SKILL.md:59 | EXPECTED: one-shot setup flow |

No network exfiltration patterns, no obfuscated execution, no credential material in either tree. The four new-this-run hits (two per tree, all HIGH-labeled by the static scorer) were each verified against the actual line and are benign prose or comments; none change the 2026-08-23 baseline verdict.

## Constraint Compliance

- Read-only on all source; the only file written is this report.
- No git state changes (read-only `git log` / `git status` only).
- No secret values are reproduced in this report; the only identifiers named are public or redacted-by-design.
- No em dash character appears in this document.