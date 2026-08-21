# .NET Development Rules — ModernWigiDash

> Adapted from dotnet-claude-kit's consolidated rules for this project's actual
> shape: a .NET 10 WPF desktop app + Hardware/USB + Widget
> plugin libraries + MSTest tests. Web/API/EF-centric rules were removed
> because this solution has no HTTP endpoints, no EF Core, no containers, and
> uses MSTest (not xUnit). Where the kit default conflicts with the project's
> deliberate design decisions (documented in CONTEXT.md / ADR-0001), the
> project decision wins and is flagged below.

---

## 1. C# Coding Style

- File-scoped namespaces always. One type per file. File name matches the type name.
- **Usings:** assume the ImplicitUsings baseline (`System`, `System.Collections.Generic`, `System.IO`, `System.Linq`, `System.Net.Http`, `System.Threading`, `System.Threading.Tasks`) and the project's `<Using>` globals (declared per-csproj — Sdk: SkiaSharp; Core: +Sdk; Hardware: +Sdk; Widgets: +Core.Rendering, System.Globalization; App: +WPF trio, Core.Models; Tests: +App, Widgets, Time.Testing). Only write a `using` for a namespace not already global in that project. In WPF projects (App, Tests) keep the baseline usings explicit — the WPF XAML markup pass compiles a temp project that does not apply ImplicitUsings.
- Order members: constants, fields, constructors, properties, public methods, private methods.
- Primary constructors for DI/reference injection. Eliminates boilerplate field assignments.
- Records for DTOs and immutable value objects.
- `sealed` on classes not designed for inheritance.
- `internal` by default, `public` only when needed. Widgets are the exception — they must be public for the reflection-based `WidgetPluginLoader`.
- Collection expressions over constructor calls (`List<int> ids = [1, 2, 3];`).
- Pattern matching over if-else chains. Switch expressions and `is` patterns.
- `var` for obvious types, explicit types when clarity matters.
- Async suffix on all async methods (`GetPriceAsync`, not `GetPrice`).
- PascalCase for public members/types/namespaces/methods. camelCase for locals/parameters.
- No `_` prefix on private fields when using primary constructors.

## 2. Architecture

- Architecture is documented in `CONTEXT.md` (domain glossary + layering diagram). Never assume a different architecture; read CONTEXT.md first.
- Dependency direction is inward: Sdk → Core/Widgets → Hardware → App. No downward references.
- Widgets are instantiated via reflection (parameterless ctor) — they cannot receive injected dependencies. Static stores with `LastUpdate` staleness tracking (LhmSensorStore, FrameTimeStore) are the pragmatic, deliberate pattern; don't "fix" them with DI.
- Synchronous transport interface is a deliberate ADR-0001 decision. USB I/O is inherently blocking; do NOT wrap `DisplayHidTransport` in fake async. Do not convert it to async without revisiting the ADR.
- Widget-per-file convention: each widget class in its own `.cs` with `[WidgetMetadata]`. Catalog discovery is reflection-based.
- The App csproj pins `<Version>0.0.0</Version>` so dev builds parse as unversioned (`AppVersion.IsDevBuild` disables the updater); release version stamps (`InformationalVersion` + `FileVersion`) come only from `build-release.ps1` — never raise the csproj version.

## 3. Security

- Never hardcode secrets in source. Use DPAPI (as `TwitchTokenStore` does) or env vars.
- Never commit `.env`, token files, or real credentials. Add to `.gitignore`.
- Validate all external input at system boundaries (touch input, widget property values, profile config).
- Do not log PII/tokens at Information level or below. `FileLog` in Sdk is the shared logging utility.
- USB vendor commands and protocol constants live in `DisplayProtocolConstants` — keep protocol framing exact (the test suite `DisplayProtocolTests` enforces it).

## 4. Testing

- Framework is **MSTest** (not xUnit). Write MSTest `[TestClass]` / `[TestMethod]` tests in `ModernWigiDash.Tests`.
- AAA pattern with clear Arrange/Act/Assert separation.
- One assertion concept per test. Separate behaviors → separate tests.
- Test naming: `MethodName_Scenario_ExpectedResult`.
- No mocking frameworks for things you own. Use real or test implementations. Reserve mocks/fakes for third-party boundaries (e.g., `IDisplayTransport` seams).
- Test behavior, not implementation details.
- This project is desktop/USB — no WebApplicationFactory/Testcontainers. Hardware-bound tests use seam injection or null readers.
- Test build must use temp output when the app is running: `dotnet test ModernWigiDash.slnx -c Release --nologo -p:BaseOutputPath=C:\Users\tobia\AppData\Local\Temp\opencode\wmd-build\ -nodeReuse:false`.

## 5. Performance

- Propagate `CancellationToken` through the call chain where async work exists. Dropped tokens mean cancelled work continues.
- Async all the way for real async work — but remember the transport is deliberately synchronous (ADR-0001); no fake async bridges.
- `Task.Wait(timeout)` returns a **bool** (did the task complete), NOT the task's result — read `.Result` only after it returns true (and only on the completion path; a timed-out task has no readable result). The 2026-08-21 standby-verdict bug was exactly this: `standbyOk = Task.Run(...).Wait(budget)` swallowed the device's verdict and made the "Standby NOT confirmed" line dead code, which a probe test (log writes bracketing the suspect) exposed.
- `TimeProvider` over `DateTime.Now` / `DateTime.UtcNow` where testability matters.
- `IHttpClientFactory` / typed clients over `new HttpClient()` for the price-feed HTTP paths (`PriceFeedManager`).
- `ArrayPool<T>` / `MemoryPool<T>` for buffer-heavy operations — the RGB565 frame pipeline is a hot path at 30 FPS.
- 30 FPS render tick in MainWindow: avoid allocations per tick; reuse frame buffers.
- `ValueTask<T>` over `Task<T>` for high-throughput paths that often complete synchronously.

## 6. Error Handling

- Expected failures should be explicit; reserve exceptions for unexpected conditions.
- No bare `Exception` catch unless at an application boundary (top-level handler).
- No catch-and-rethrow without adding context. Either handle it or let it propagate.
- Validate at system boundaries only. Internal code trusts validated data.
- Transport failures should surface diagnostics through the existing logging; USB reconnect/dispose paths must be safe (see `DisplayDeviceEngine` dispose safety).

## 7. Git Workflow

- Conventional commit prefixes: `feat:`, `fix:`, `refactor:`, `test:`, `docs:`, `chore:`.
- Commit body explains "why", not "what".
- No vague messages like "fix bug" or "update code".
- Atomic commits. One logical change per commit. Feature and its tests belong together.
- Never force-push to main or master.
- Run verification before committing: build + tests green.
- Keep PRs focused on a single concern.

## 8. Agent & Tool Usage

- MCP tools before file reading. Use glider (search_symbols, find_references, get_symbol_info, get_diagnostics, find_unused_symbols) before reading source files. CodeGraph (`codegraph_explore`) is the other indexed source — it returns verbatim symbol source in one call.
- `glider_get_project_graph` before structural changes. Understand the dependency tree first.
- `glider_get_diagnostics` after modifications — faster and more structured than parsing `dotnet build` output.
- Do not read entire files to find a single method. Use `find_code` / search_symbols first.
- Subagents for parallel research and independent tasks.
- Route to specialist agents (see `.opencode/AGENTS.md` routing table).
- Load relevant skills before starting work. Check `.opencode/AGENTS.md` skill maps.
- NOTE: the kit's RoslynNavigator MCP is NOT installed here — Glider provides the equivalent tools (see mapping notes in each skill/agent).

## 9. Package Management

- Never hardcode NuGet package versions from memory. Training data contains outdated versions.
- Run `dotnet add package <name>` without `--version` to pull the latest stable.
- Microsoft.* packages for .NET 10 use 10.x versions.
- `Directory.Packages.props` centralizes version pins (CPM) — the whole solution already uses it. Add new packages there, not per-project.
- Never downgrade a package already in the project unless explicitly asked.
- Prefer release versions over preview/RC unless the project targets preview features.
- If unsure about the latest version, run `dotnet package search <name>` or check NuGet.org.

## 10. House One-Liners (ported from poteto pstack)

- **Blast radius: prove by running, not by compiling.** The compiler proves shape; execution proves behavior. Any change with callers across modules (seam moves, renames, shared-policy edits) ends with the affected tests run — and, when a user-facing seam moved, the `verify-modernwigidash` recipe for that surface run once. "It builds" is not the closing claim.
- **Shape before code on non-trivial work.** Before the first edit on a new module/seam: three lines of shape — which type owns the rule, which seam the tests will drive, which state becomes unrepresentable. If you find yourself mid-edit asking "where should this live?", stop and sketch the shape first; the architect skill exists for exactly that pause.
