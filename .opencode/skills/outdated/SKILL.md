---
name: outdated
description: >
  Dependency health report for .NET solutions: outdated NuGet packages,
  vulnerable versions, and commercial-license traps (MediatR, MassTransit,
  FluentAssertions, AutoMapper) — powered by the get_nuget_packages MCP tool.
  Invoke when: "outdated packages", "check dependencies", "stale packages",
  "package audit", "dependency health", "are my packages up to date",
  "license check", "vulnerable packages", "nuget audit".
---

> **Tool mapping (this project):** `get_nuget_packages` has no Glider equivalent —
> inventory packages with `dotnet list package` (CLI) and the central
> `Directory.Packages.props`. glider_find_package_consolidation_candidates and
> glider_find_package_usages are available for CPM drift and usage mapping.

# /outdated

## What

A three-layer dependency health report:

1. **Inventory** — every `PackageReference` per project, with TFMs and central
   package management awareness, via the `get_nuget_packages` MCP tool (no
   network, token-cheap).
2. **Staleness + vulnerabilities** — current vs latest stable, and known CVEs,
   via the `dotnet` CLI.
3. **License screen** — flags packages that moved to commercial licenses so an
   innocent `dotnet outdated --upgrade` doesn't silently change your legal
   position.

The output is a single prioritized table — vulnerabilities first, license traps
second, staleness last — with a recommended action per row.

## When

- "check for outdated packages", "package audit", "dependency health"
- Before a .NET version upgrade (pairs with `/migrate` Flow B)
- After inheriting an unfamiliar codebase
- Dependabot/NuGet audit warnings appeared and you want the full picture
- Periodically on long-lived projects — quarterly is a good cadence

## How

**Step 1: Inventory (MCP, no network)**

```
get_nuget_packages()                          -- whole solution
get_nuget_packages(projectFilter: "Api")      -- or one project
```

Returns per-project `{Name, TargetFramework, Cpm, Packages: [{Id, Version}]}`.
Note `Cpm: true` — updates then belong in `Directory.Packages.props`, not the
csproj. Flag mixed TFMs across projects while you're here.

**Step 2: Staleness and vulnerabilities (CLI)**

```bash
dotnet list package --outdated
dotnet list package --vulnerable --include-transitive
```

Both need a successful restore first. If restore fails, fix that before
auditing — a broken lock state makes version output unreliable.

**Step 3: License screen**

Check the inventory against the known commercial moves (full rationale in
`knowledge/package-recommendations.md`):

| Package | Commercial from | Free alternative |
|---|---|---|
| MediatR | 13+ (Lucky Penny, RPL) | `Mediator` (martinothamar) — source-generated, MIT |
| MassTransit | 9+ (v8 Apache, patches end 2026 then EOL) | Wolverine 6.x, or stay on v8 short-term |
| FluentAssertions | 8+ (v7 stays Apache, frozen) | xUnit built-in `Assert` (kit default), Shouldly, AwesomeAssertions |
| AutoMapper | 15+ (Lucky Penny) | Manual mapping (kit default) or Mapperly (MIT) |

A license flag fires when the project is on the free major and a naive
"update all" would cross the boundary — that is the trap this step exists for.

**Step 4: Report**

One table, priority-ordered:

| Priority | Meaning | Action |
|---|---|---|
| VULNERABLE | Known CVE in current version | Update now, test, deploy |
| LICENSE | Next major crosses a commercial boundary | Pin below boundary or plan migration |
| MAJOR | Behind a major (breaking changes likely) | One at a time, release notes first |
| MINOR/PATCH | Routine drift | Batch patches; minors one at a time |

**Step 5: Act (optional)**

Offer to execute updates via `/migrate` Flow C — one package at a time,
`dotnet build && dotnet test` between each. Never batch major updates:
batched failures are unattributable.

### MCP Tools Used

- `get_nuget_packages` — inventory, CPM detection, TFM audit
- `get_diagnostics` — verify the solution still compiles clean after updates

## Example

```
User: /outdated

Claude: Inventorying packages (get_nuget_packages)...
  3 projects, CPM enabled (Directory.Packages.props), all net10.0.

  Checking versions and CVEs...

  | Package                  | Current | Latest  | Priority   | Action                          |
  |--------------------------|---------|---------|------------|---------------------------------|
  | System.Text.Json (trans.)| 10.0.2  | 10.0.10 | VULNERABLE | Update now (CVE-2026-XXXX)      |
  | MassTransit              | 8.3.0   | 9.1.2   | LICENSE    | v9 is commercial. Stay on v8    |
  |                          |         |         |            | (EOL end 2026) or plan Wolverine|
  | Serilog.AspNetCore       | 9.0.0   | 10.0.0  | MAJOR      | Update alone, check sink compat |
  | FluentValidation         | 12.0.1  | 12.1.1  | PATCH      | Batch with other patches        |

  1 vulnerability, 1 license trap, 1 major, 1 patch.
  Want me to apply these via /migrate Flow C?
```

## Related

- `/migrate` — Flow C executes the updates this report recommends
- `knowledge/package-recommendations.md` — vetted packages + licensing detail
- `knowledge/mediatr-to-mediator-migration.md` — step-by-step MediatR exit
- `/verify` — full pipeline after applying updates
