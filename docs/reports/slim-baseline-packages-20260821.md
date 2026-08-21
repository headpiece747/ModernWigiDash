## Package freshness (2026-08-21)

Scope: all 6 projects of `ModernWigiDash.slnx`. Central package management is on — every one
of the 16 directly pinned packages is referenced from a `PackageReference` without a `Version`
attribute, so all version pins live exclusively in `Directory.Packages.props` (verified per
csproj; no non-CPM-managed direct `PackageReference` exists). Latest versions were taken from
the NuGet flatcontainer feed (`api.nuget.org/v3-flatcontainer/<id>/index.json`) and cross-checked
against the NuGet gallery and the NuGet CLI's own verdicts.

| Package | Current pin | Latest stable | Status | Major-bump? |
|---|---|---|---|---|
| AsyncFixer | 2.1.0 | 2.1.0 | current | — |
| coverlet.collector | 10.0.1 | 10.0.1 | current | — |
| LibUsbDotNet | 3.0.224 | 3.0.224 | current | — |
| MessagePack | 3.1.8 | 3.1.8 | current | — |
| Meziantou.Analyzer | 3.0.177 | 3.0.177 | current | — |
| Microsoft.Extensions.Logging.Abstractions | 10.0.11 | 10.0.11 | current | — |
| Microsoft.Extensions.TimeProvider.Testing | 10.9.0 | 10.9.0 | current | — |
| Microsoft.NET.Test.Sdk | 18.9.0 | 18.9.0 | current | — |
| MSTest.TestAdapter | 4.3.3 | 4.3.3 | current | — |
| MSTest.TestFramework | 4.3.3 | 4.3.3 | current | — |
| NAudio.Wasapi | 3.0.1 | 3.0.1 (listed) | current | — |
| Roslynator.Analyzers | 4.16.1 | 4.16.1 | current | — |
| SkiaSharp | 4.151.1 | 4.151.1 | current | — |
| SkiaSharp.Views.WPF | 4.151.1 | 4.151.1 | current | — |
| SonarAnalyzer.CSharp | 10.33.0.1635 | 10.33.0.1635 | current | — |
| System.Security.Cryptography.ProtectedData | 10.0.11 | 10.0.11 | current | — |

Result: **0 upgrades available.** All 16 pins are at their latest stable versions. The NuGet CLI
independently agrees — `dotnet list ModernWigiDash.slnx package --outdated` reports "no updates"
for all six projects.

### Vulnerabilities

`dotnet list ModernWigiDash.slnx package --vulnerable --include-transitive` (run from the repo
root, covering all six projects): **no vulnerable direct or transitive packages given the current
sources.** NuGet audit found nothing; there is no CVE-driven action item from this audit.

### License flags

The known commercial-license traps named by the `outdated` skill — MediatR, MassTransit,
FluentAssertions, AutoMapper — are **not present** in the solution (checked against the full
16-package inventory). Negative result recorded; no license screen fires. All direct packages are
permissive-licensed (MIT/Apache-2.0 lineage), including NAudio (MIT) and SkiaSharp (MIT/Apache
dual for the managed layer; native Skia binaries carry their own Skia license, unchanged across
4.x point releases).

### Notes

- **NAudio.Wasapi "22.0.0" is a feed artifact, not a release.** The flatcontainer feed lists a
  `22.0.0` that sorts above 3.0.1, but the registration data shows it is **unlisted**
  (`listed: false`), carries a placeholder publish date (1900-01-01), has a 2023-09-04 commit
  timestamp, targets only `.NETStandard2.0` + `uap10.0.18362`, and depends on
  `NAudio.Core >= 2.2.0` — i.e. a mis-versioned legacy publish from the 2.x era that the owner
  hid. Do not "upgrade" to it. The gallery's latest stable is 3.0.1 (published 2026-08-18, three
  days before this audit); a prerelease `3.0.2-preview.1` (2026-08-20) exists but is out of scope
  for a stable-freshness check.
- **SkiaSharp pair must move in lockstep.** `SkiaSharp` and `SkiaSharp.Views.WPF` share the
  native Skia binary and are pinned to the same 4.151.1 in five projects (Sdk, Core, Hardware,
  Widgets, Tests) plus the WPF host view package in App. Any future upgrade must bump both pins
  together (one `Directory.Packages.props` edit each), and the 4.x line changed the native
  loading/asset model versus 3.x — a future 3→4-style major would need the WPF interop path
  (`SkiaSharp.Views.WPF`) smoke-tested on-device, not just unit tests.
- **Test-framework majors are already where they are.** MSTest 4.3.3 / Microsoft.NET.Test.Sdk
  18.9.0 / coverlet.collector 10.0.1 are current; no test-framework major pending.
- **Microsoft.* pins ride the .NET 10 servicing train** (10.0.11, 10.9.0). Expect routine
  point releases; batch them freely, they are patch-level.
- **Tmix note:** `ModernWigiDash.Sdk` targets plain `net10.0` while the other five target
  `net10.0-windows10.0.19041.0` — deliberate (the Sdk layer must stay platform-neutral), so
  package choices for the Sdk must remain cross-platform-compatible.
- **Skill note:** this installation of the `outdated` skill ships only `SKILL.md`; the referenced
  `knowledge/package-recommendations.md` and `knowledge/mediatr-to-mediator-migration.md` are
  absent. The license screen was therefore applied from the skill's inline trap table, and the
  inventory was taken with `dotnet list package` + `Directory.Packages.props` per the project's
  documented tool mapping (`get_nuget_packages` MCP tool not installed).