# Outdated Packages Report

**Project:** ModernWigiDash | **Date:** 2026-08-25 | **Scope:** dependency staleness, vulnerabilities, license screen
**Baseline:** HEAD 8cf016c, full gate green ~1 hour before this run. Read-only research; no source file was modified and no package version was changed. No restore, build, or test was run.
**Inventory source:** `Directory.Packages.props` (central package management) cross-checked against `dotnet list package --no-restore --include-transitive` on all six projects (Sdk, Core, Hardware, Widgets, App, Tests).

> Methodology note. "Latest stable" below is the highest *listed* stable version on nuget.org. Unlisted versions and prereleases are excluded from that figure but are noted where relevant. Staleness was checked against the NuGet flat container (`v3-flatcontainer/<id>/index.json`), vulnerabilities against the NuGet OSV vulnerability feed (`v3/vulnerabilities`, base + update, merged) with a prerelease-aware version-range check, and licenses from the NuGet registration catalog (`licenseExpression` / bundled LICENSE).

## Summary

| Measure | Count |
|---------|-------|
| Total pinned packages | 16 |
| Current (on latest listed stable) | 16 |
| Behind (older stable exists) | 0 |
| At-risk (vulnerable or license trap) | 0 |
| Pinned packages in a known-vulnerable range | 0 |
| Commercial license traps | 0 |

All sixteen CPM-pinned packages are on their latest listed stable version. No package has a known vulnerability in its pinned version, and none of the four named commercial-trap packages (MediatR, MassTransit, FluentAssertions, AutoMapper) is referenced. Every direct reference is CPM-managed; no per-csproj versioned (non-CPM) direct reference exists.

Two nuances are worth surfacing even though they do not change the counts:

- **NAudio.Wasapi** is pinned at 3.0.1, which is the latest *listed* stable (published 2026-08-18). A numerically higher 22.0.0 exists in the version index but is **unlisted** (`listed=false`), cataloged 2023-09-04 with a placeholder description ("Package Description"). It is not a release to chase; no upgrade is warranted.
- **SonarAnalyzer.CSharp** is the one package whose license is not MIT/Apache/BSD/OSS: it ships under the "Sonar Source-Available License v1.0" (2024). It is a build-time-only analyzer (`PrivateAssets=all`) and is not shipped in the app, so there is no distribution impact.

## Package inventory

| id | pinned | latest stable | status | notes |
|----|--------|---------------|--------|-------|
| AsyncFixer | 2.1.0 | 2.1.0 | current | Apache-2.0; build-time analyzer (dev-only) |
| coverlet.collector | 10.0.1 | 10.0.1 | current | MIT; test-only |
| LibUsbDotNet | 3.0.224 | 3.0.224 | current | LGPL-3.0-or-later (copyleft OSS); runtime USB dependency |
| MessagePack | 3.1.8 | 3.1.8 | current | MIT; 14 advisories (27 ranges) on record, all bounded at or below 3.1.7, none cover 3.1.8 |
| Meziantou.Analyzer | 3.0.177 | 3.0.177 | current | MIT; build-time analyzer (dev-only) |
| Microsoft.Extensions.Logging.Abstractions | 10.0.11 | 10.0.11 | current | MIT; 11.0.0-preview.x is a .NET 11 prerelease, not applicable to net10.0 |
| Microsoft.Extensions.TimeProvider.Testing | 10.9.0 | 10.9.0 | current | MIT; test-only |
| Microsoft.NET.Test.Sdk | 18.9.0 | 18.9.0 | current | MIT; test-only |
| MSTest.TestAdapter | 4.3.3 | 4.3.3 | current | MIT; test-only |
| MSTest.TestFramework | 4.3.3 | 4.3.3 | current | MIT; test-only |
| NAudio.Wasapi | 3.0.1 | 3.0.1 | current | MIT; 22.0.0 present in index but unlisted (2023 placeholder); 3.0.1 (2026-08-18) is latest listed stable |
| Roslynator.Analyzers | 5.0.0 | 5.0.0 | current | Apache-2.0; build-time analyzer (dev-only) |
| SkiaSharp | 4.151.1 | 4.151.1 | current | MIT; 4.152.0-preview.1.1 is a prerelease; 1 advisory (<= 2.88.6) not covered |
| SkiaSharp.Views.WPF | 4.151.1 | 4.151.1 | current | MIT; 4.152.0-preview.1.1 is a prerelease |
| SonarAnalyzer.CSharp | 10.33.0.1635 | 10.33.0.1635 | current | Sonar Source-Available License v1.0 (non-OSS); build-time analyzer (dev-only, not shipped) |
| System.Security.Cryptography.ProtectedData | 10.0.11 | 10.0.11 | current | MIT; 11.0.0-preview.x is a .NET 11 prerelease, not applicable to net10.0 |

### Transitive dependencies (not CPM-pinned, out of scope for the vuln/license screen)

Pulled in by the direct packages and resolved by the graph, not declared in `Directory.Packages.props`: OpenTK 4.3.0 family (via SkiaSharp.Views.WPF, incl. OpenTK.GLWpfControl 4.2.3 and OpenTK.redist.glfw 3.3.0-pre), NAudio.Core 3.0.1, System.Numerics.Tensors 9.0.0, Microsoft.NET.StringTools 17.11.4, MessagePack.Annotations 3.1.8 and MessagePackAnalyzer 3.1.8, SkiaSharp.NativeAssets.Win32 / macOS 4.151.1, Microsoft.Extensions.DependencyInjection.Abstractions 10.0.11, and the test stack (Microsoft.Testing.Platform 2.3.3 family, Microsoft.CodeCoverage / TestPlatform 18.9.0, Microsoft.ApplicationInsights 2.23.0, MSTest.Analyzers 4.3.3). These were not audited for vulnerabilities or license, per the pinned-versions scope.

## Vulnerabilities

No pinned package is in a known-vulnerable range. The NuGet OSV vulnerability feed (base snapshot 2026-08-20, empty 2026-08-25 update) was checked against all sixteen pinned versions using a prerelease-aware NuGet version-range comparison.

Two packages have advisory entries on record, but neither covers the pinned version:

- **MessagePack 3.1.8** (MIT). Fourteen distinct advisories (27 range entries) are on record for the package. The most recent are the coordinated June 2026 batch, whose vulnerable ranges top out at `< 3.1.7` (e.g. `[3.0.0, 3.1.7)` and `[3.0.214-rc.1, 3.1.7)`); older entries are bounded lower (`< 2.5.187`, `< 2.5.301`, `[2.6.95-alpha, 3.0.214-rc.1)`, `(, 1.9.11)`, etc.). The pinned 3.1.8 is above the highest patch boundary (3.1.7), so it is not affected by any of them. Usage in the app is typed deserialization only (a `[MessagePackObject]` record), which is consistent with the prior 2026-08-25 security scan that recorded 3.1.8 as sitting just above this patch line.
- **SkiaSharp 4.151.1** (MIT). One advisory is on record (GHSA-j7hp-h8jx-5ppr) covering `[2.0.0, 2.88.6)`. The pinned 4.151.1 is far above 2.88.6, so it is not affected.

The remaining fourteen packages are not present in the NuGet advisory database at all.

No action required on vulnerabilities. If the 3.1.x line of MessagePack is ever touched, re-check against the current patch line, since the June 2026 batch is recent.

## License traps

No commercial license traps. None of the four named commercial-trap packages (MediatR, MassTransit, FluentAssertions, AutoMapper) is referenced, and no package in the inventory has moved to a proprietary or commercial license.

Full license breakdown of the sixteen pinned packages:

- MIT (12): coverlet.collector, MessagePack, Meziantou.Analyzer, Microsoft.Extensions.Logging.Abstractions, Microsoft.Extensions.TimeProvider.Testing, Microsoft.NET.Test.Sdk, MSTest.TestAdapter, MSTest.TestFramework, NAudio.Wasapi, SkiaSharp, SkiaSharp.Views.WPF, System.Security.Cryptography.ProtectedData. (Microsoft.* and the test stack are MIT as standard.)
- Apache-2.0 (2): AsyncFixer, Roslynator.Analyzers.
- LGPL-3.0-or-later (1): LibUsbDotNet. Copyleft but free/open-source; a standard runtime dependency for the USB transport. Not a commercial trap; the LGPL obligation is to allow the user to relink/replace the library, which is satisfied by consuming it as a NuGet package in a desktop app.
- Non-OSS (1): SonarAnalyzer.CSharp, under "Sonar Source-Available License v1.0" (last updated 2024-11-13). This is a source-available, non-OSS license with a competing-product restriction, not a permissive OSS license. It is a build-time-only analyzer (`PrivateAssets=all` in `Directory.Packages.props`) and is not distributed with the app, so it has no license impact on the shipped product. It is the single non-permissive-license entry in the inventory and is flagged for awareness, not as a trap.

No action required on licenses.