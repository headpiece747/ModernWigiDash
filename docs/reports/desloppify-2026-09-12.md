# Desloppify Scan Report: ModernWigiDash

**Date:** 2026-09-12 | **Tool:** desloppify 1.0 (uvx, git+https://github.com/peteromallet/desloppify.git) | **Scope:** entire project

## Scores
- overall 78.0/100 | objective (mechanical) 91.7% | strict 77.6/100 (target 85.0) | verified 91.7%
- Recipe: overall = 25% mechanical + 75% subjective. Mechanical pool 91.7%, subjective pool 73.4%.
- Biggest weighted drags are subjective dimensions (elegance, design coherence, contracts) - judgment-layer, not mechanical debt.

## Authoritative framing
This repo's mechanical debt is already machine-pinned by DebtGuardTests (the gate): sync-over-async, async void, new HttpClient, ambient clock, bare catch, P/Invoke entry points, dead private helpers, layer edges. Desloppify's mechanical surface overlaps that pin, so the GATE REMAINS AUTHORITATIVE; this scan is a secondary signal. Where they disagree, the gate wins.

## Security findings: ALL FALSE POSITIVES (verified)
The "9 security issues" are test fixtures with dummy values, not real secrets:
- TwitchSessionTests.cs (4x accessToken), TwitchTokenStoreTests.cs (4x AccessToken/RefreshToken), PriceFeedSocketLoopTests.cs (1x finnhubApiKey: "test-key") - all hardcoded_secret_name on TEST files with dummy data.
- The 1 "import cycle" (App.xaml.cs -> DialogChrome -> DialogHost -> MainWindow.Update -> MainWindow.xaml.cs ...) is a false positive: these are PARTIAL CLASSES of one type sharing a file, not a real dependency cycle. Glider's project graph shows 0 cycles.
Real secret handling (DPAPI via TwitchTokenStore) is correct and unchanged.

## Subjective drags (judgment-layer, for the code-reviewer agent - not mechanical fixes)
- Auth consistency 32% (web-shaped rubric misapplied to a desktop app with no auth endpoints - ignore)
- Elegance / design coherence / contracts ~72-78% (subjective; the deepening candidates in the Phase 3 report address the structural ones)

## Verdict
No new mechanical debt beyond what the gate already pins. No real security issues. The subjective scores reflect the tool's web-oriented rubric applied to a C# WPF desktop app; treat as directional only.
