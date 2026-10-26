# Steam Library Widget Redesign Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver a high-impact, tactile Steam Library widget featuring three presentation modes (Hero Deck 60/40 split, Showcase Carousel, and Grid Wall) with on-screen mode cycling and quick-filter tabs, operating 100% offline from the local Steam installation with zero Web API/SteamID64 requirements and strict low-resource zero-allocation rendering.

**Architecture:** The widget orchestrates an offline discovery pipeline (`SteamLibraryService` + `SteamLibraryLocator`), single-source geometry records (`SteamHeroGeometry`, `SteamShowcaseGeometry`, `SteamGridGeometry`), and specialized zero-allocation SkiaSharp renderers (`SteamHeroRenderer`, `SteamShowcaseRenderer`, `SteamGridRenderer`). All render passes use hoisted `SKPaint` instances and a 24-slot bounded LRU texture cache.

**Tech Stack:** C# 14 / .NET 10, SkiaSharp, WPF / ModernWigiDash Widget SDK, xUnit.

## Global Constraints

- Target Display: 1016 × 592 px RGB565 LCD over USB / HID
- Zero per-frame heap allocations during the 30 FPS render tick
- Hoisted `SKPaint` instances in all renderers; `MemoSlot` for formatted text strings
- 100% offline local Steam data (no Web API key or SteamID64 required)
- Bounded 24-slot LRU texture eviction cache (`SteamPosterCache`)
- Minimum 48–54px touch targets; 600ms long-press for favorites; 24px drag threshold for scrolling

---

### Task 1: Extend Enums and Models for Hero Deck & Filtering

**Files:**
- Modify: `ModernWigiDash.Widgets/Steam/Models/SteamEnums.cs`
- Modify: `ModernWigiDash.Widgets/Steam/Models/SteamGameItem.cs`
- Test: `ModernWigiDash.Tests/SteamLibraryWidgetTests.cs`

**Interfaces:**
- Consumes: Existing `SteamLayoutMode`
- Produces:
  - `SteamLayoutMode.HeroDeck` (0), `Showcase` (1), `Grid` (2)
  - `SteamFilterMode`: `All` (0), `Favorites` (1), `Recent` (2)

- [ ] **Step 1: Write unit tests verifying enum values and default layout mode**
- [ ] **Step 2: Add `HeroDeck` to `SteamLayoutMode` and create `SteamFilterMode` in `SteamEnums.cs`**
- [ ] **Step 3: Run tests to verify compilation and baseline behavior**
- [ ] **Step 4: Commit changes**

---

### Task 2: Implement Hero Deck Geometry & Layout Calculator

**Files:**
- Create: `ModernWigiDash.Widgets/Steam/Rendering/SteamHeroGeometry.cs`
- Create: `ModernWigiDash.Widgets/Steam/Rendering/SteamHeroLayout.cs`
- Modify: `ModernWigiDash.Widgets/Steam/Rendering/SteamGeometryMemo.cs`
- Test: `ModernWigiDash.Tests/SteamHeroLayoutTests.cs`

**Interfaces:**
- Consumes: Viewport bounds (`SKRect`), `SteamLibrarySnapshot`, selected app ID, scroll offset
- Produces: `SteamHeroGeometry` containing exact `SKRect` bounds for:
  - Header: Filter tabs (`All`, `Favorites`, `Recent`), Game count badge, Layout toggle pill (`[ ▣ Hero ]`)
  - Left Hero: Hero card, background art, title, status pill, playtime pill, `PLAY NOW` / `STOP GAME` button
  - Right Shelf: Container bounds, list of `SteamShelfItemGeometry` (capsule thumb, title, status, hit box)

- [ ] **Step 1: Write unit tests in `SteamHeroLayoutTests.cs` for 1016×592 layout calculations**
- [ ] **Step 2: Implement immutable records in `SteamHeroGeometry.cs`**
- [ ] **Step 3: Implement static layout calculator in `SteamHeroLayout.cs`**
- [ ] **Step 4: Update `SteamGeometryMemo.cs` to cache `SteamHeroGeometry`**
- [ ] **Step 5: Run tests and verify all geometry calculations pass**
- [ ] **Step 6: Commit changes**

---

### Task 3: Implement Zero-Allocation Hero Deck Renderer

**Files:**
- Create: `ModernWigiDash.Widgets/Steam/Rendering/SteamHeroRenderer.cs`
- Test: `ModernWigiDash.Tests/SteamHeroRendererTests.cs`

**Interfaces:**
- Consumes: `SKCanvas`, `SteamHeroGeometry`, `SteamGameItem` (selected hero), `IReadOnlyList<SteamGameItem>` (shelf), `SteamRenderFrame`
- Produces: Rendered Skia frame with:
  - Atmospheric ambient background aura from game art
  - Titanium Amber styling: frosted glass pills, elevated card borders
  - Live status indicator (`● RUNNING` pulsing green vs amber `Ready to Play`)
  - Big tactile CTA (`▶ PLAY NOW` vs `■ STOP GAME`)
  - Vertical quick-switch shelf with selection ring and capsule thumbs
  - Zero heap allocations per tick

- [ ] **Step 1: Write unit tests in `SteamHeroRendererTests.cs` testing render passes and zero-allocation assertions**
- [ ] **Step 2: Implement `SteamHeroRenderer` with hoisted `SKPaint` fields and `MemoSlot`**
- [ ] **Step 3: Run tests and verify rendering passes cleanly**
- [ ] **Step 4: Commit changes**

---

### Task 4: Upgrade Showcase and Grid Layouts & Renderers with Header Controls

**Files:**
- Modify: `ModernWigiDash.Widgets/Steam/Rendering/SteamShowcaseLayout.cs`
- Modify: `ModernWigiDash.Widgets/Steam/Rendering/SteamShowcaseRenderer.cs`
- Modify: `ModernWigiDash.Widgets/Steam/Rendering/SteamGridLayout.cs`
- Modify: `ModernWigiDash.Widgets/Steam/Rendering/SteamGridRenderer.cs`
- Test: `ModernWigiDash.Tests/SteamShowcaseLayoutTests.cs`
- Test: `ModernWigiDash.Tests/SteamGridLayoutTests.cs`

**Interfaces:**
- Consumes: Filter mode, layout mode
- Produces: Shared header bar bounds across Showcase and Grid layouts for mode switching and category filtering

- [ ] **Step 1: Update existing layout tests with header bounds expectations**
- [ ] **Step 2: Add header filter pills and mode switch pill to `SteamShowcaseLayout` and `SteamGridLayout`**
- [ ] **Step 3: Update `SteamShowcaseRenderer` and `SteamGridRenderer` to draw header pills**
- [ ] **Step 4: Run tests to verify existing and new tests pass**
- [ ] **Step 5: Commit changes**

---

### Task 5: Integrate Orchestration & Touch Routing in `SteamLibraryWidget`

**Files:**
- Modify: `ModernWigiDash.Widgets/Steam/SteamLibraryWidget.cs`
- Test: `ModernWigiDash.Tests/SteamLibraryWidgetTests.cs`

**Interfaces:**
- Consumes: `SteamHeroRenderer`, `SteamShowcaseRenderer`, `SteamGridRenderer`, `SteamHeroGeometry`
- Produces:
  - 3-mode selection via widget property `LayoutMode` ("Hero Deck", "Showcase", "Poster Grid")
  - On-screen touch pill cycling: `HeroDeck` → `Showcase` → `Grid` → `HeroDeck`
  - Filter tabs touch routing: `All` | `Favorites` | `Recent`
  - Hero Deck touch routing: tapping shelf item changes hero; tapping action button triggers launch/stop
  - Showcase & Grid touch routing: launching, stopping, scrolling, 600ms long-press favoriting

- [ ] **Step 1: Write integration tests in `SteamLibraryWidgetTests.cs` for mode switching, filtering, and hero actions**
- [ ] **Step 2: Implement state tracking (`_activeFilter`, `_selectedHeroAppId`) and touch dispatch in `SteamLibraryWidget.cs`**
- [ ] **Step 3: Run all Steam widget tests to verify green**
- [ ] **Step 4: Commit changes**

---

### Task 6: Full Solution Verification, Gates & Build Validation

**Files:**
- All modified and new files
- Scripts: `scripts/run-gates.ps1`, `scripts/build-release.ps1`

- [ ] **Step 1: Run complete test suite in Release mode (`dotnet test -c Release`)**
- [ ] **Step 2: Run `scripts/ref-check.ps1` and `scripts/ps-hygiene.ps1`**
- [ ] **Step 3: Run `scripts/run-gates.ps1` to anchor the clean gate trail**
- [ ] **Step 4: Run `scripts/build-release.ps1` to produce the verified release bundle**
- [ ] **Step 5: Final Elephant-Goldfish audit and completion confirmation**
