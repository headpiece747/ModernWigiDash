# Steam Library Widget Redesign — Specification

**Date:** 2026-10-26  
**Status:** Approved  
**Author:** Antigravity AI  
**Scope:** ModernWigiDash Native Steam Library Widget (`ModernWigiDash.Widgets.Steam`)

---

## 1. Executive Summary & Inspiration

The Steam Library widget in ModernWigiDash is redesigned to deliver the tactile, high-impact aesthetic of the **Elgato Marketplace Steam Library touchscreen widget** and **Steam Big Picture / Steam Deck UI**, unified under ModernWigiDash's **Titanium Amber** design language.

Instead of a basic grid, the widget introduces three selectable presentation modes:
1. **Hero Deck (Recommended / Default):** A 60/40 split featuring a dominant active hero game card on the left with instant `▶ PLAY NOW` or `■ STOP GAME` touch targets, atmospheric backdrop glow, live process status, and local playtime stats, paired with a vertical quick-switch shelf on the right.
2. **Showcase Carousel:** A cinematic console-grade carousel with a centered 3D focal card, ambient aura, and floating bottom action controls.
3. **Tactical Grid Wall:** An upgraded multi-game poster wall (2×4 or 2×5) designed for fast, high-density 1-tap launching.

All modes are switchable via an on-screen frosted header pill (`[ ▣ Hero ]`, `[ ◫ Showcase ]`, `[ ⊞ Grid ]`) or through the widget property inspector, and feature quick-filter pills (`All Installed` | `Favorites` | `Recent`).

---

## 2. Hard Invariants & Technical Constraints (Elephant Core)

1. **Physical Display Target:** 1016 × 592 px RGB565 LCD over USB / HID.
2. **Zero Per-Frame Memory Allocation:**
   - Hoisted `SKPaint` instances across all renderers. Fills, strokes, drop shadows, and text paints are instantiated once and reused.
   - `FontHelper` typeface caching for all typography.
   - `MemoSlot` for formatted text strings (e.g. playtime `"142.5 hrs"`, counts, titles).
3. **100% Offline / Local Operation:**
   - **No Steam Web API key and No SteamID64 required.** The widget runs fully offline out-of-the-box.
   - Discovery via Windows Registry (`HKCU\Software\Valve\Steam`) and `libraryfolders.vdf`.
   - Game metadata parsed from per-app manifests (`appmanifest_<appId>.acf`).
   - Playtime and last-played timestamps read locally from `userdata/<account_id>/config/localconfig.vdf`.
   - Artwork loaded locally from Steam's client cache (`Steam/appcache/librarycache/<appId>/`).
   - Process tracking via Windows process matching (`SteamProcessTracker`).
   - Launching via `steam://rungameid/<appId>` URI protocol.
4. **Bounded 24-Slot LRU Texture Eviction:**
   - Cover art and hero banners are managed by `SteamPosterCache`.
   - Memory is strictly capped to 24 slots max (~15–25 MB VRAM). Off-screen textures are deterministically disposed when capacity is reached.
5. **Single-Source Immutable Geometry Records:**
   - Layout computations produce immutable records (`SteamHeroGeometry`, `SteamShowcaseGeometry`, `SteamGridGeometry`) calculated only on viewport resize, layout mode switch, or library snapshot refresh.
   - Touch hit-testing directly queries the exact same geometry record that the renderer draws, ensuring 100% pixel-accurate tap targets.
6. **Ergonomic Touch Targets:**
   - Primary action buttons (`PLAY NOW`, `STOP GAME`) are at least 54px in height.
   - Mode toggles and filter pills are at least 44–48px in touch hit-test radius.
   - 600 ms long-press for favoriting, 24px drag threshold for scrolling.

---

## 3. Visual & Interactive Design

### 3.1 Color Palette (Titanium Amber)
- **Deep Background Canvas:** `#121418` to `#161920`
- **Elevated Card Surface:** `#1A1D24` (border: `rgba(255, 255, 255, 0.08)`)
- **Solar Amber Accent:** `#F59E0B` (glow: `rgba(245, 158, 11, 0.25)`)
- **Running Status Green:** `#22C55E` (glow: `rgba(34, 197, 94, 0.3)`)
- **Stop / Action Red:** `#EF4444`
- **Frosted Glass Pills:** `rgba(255, 255, 255, 0.08)` with 1px border `rgba(255, 255, 255, 0.16)`
- **Typography:** High-contrast pure white `#F3F4F6` for titles; `#9CA3AF` for metadata.

### 3.2 Header Bar (Shared Across All Layouts)
- **Left:** Filter tabs: `All` | `Favorites` | `Recent` (active tab has amber highlight).
- **Center:** Total games count badge (e.g. `28 Games`).
- **Right:** Layout Switcher Pill (`[ ▣ Hero ]` / `[ ◫ Showcase ]` / `[ ⊞ Grid ]`). Tapping cycles to the next layout.

### 3.3 Layout Modes

#### Mode 1: Hero Deck (60/40 Split)
- **Left Hero Card (~60% width):**
  - Atmospheric blurred background derived from game poster or hero banner.
  - Live status indicator:
    - If running: Pulsing green pill `● RUNNING`
    - If idle: Amber pill `Ready to Play`
  - Playtime pill: e.g. `142.5 hrs`
  - Game Title (bold 22pt) + Developer/Publisher text (12pt muted).
  - **Hero CTA Button:**
    - Idle: Vibrant solar amber `▶ PLAY NOW` (54px height, tactile gradient).
    - Running: High-visibility `■ STOP GAME` (54px height, red gradient).
- **Right Shelf (~40% width):**
  - Vertical list of game capsules (thumbnail + title + status).
  - Tapping an item sets it as the active hero.
  - Long-press (600ms) toggles favorite status.
  - Smooth vertical touch scrolling.

#### Mode 2: Showcase Carousel
- Full-bleed cinematic layout with center focal card (1.15× scale and amber aura).
- Left and right neighbor cards partially visible.
- Floating bottom dock with `▶ PLAY NOW` / `■ STOP GAME` action button and playtime pill.
- Swipe left/right or tap chevron pills to navigate.

#### Mode 3: Tactical Grid Wall
- 2×4 or 2×5 poster grid of vertical capsules (`library_600x900.jpg`).
- Running games have a glowing green border and running badge.
- Tapping launches the game immediately; tapping a running game displays the stop action.
- Horizontal drag scrolls smoothly through the collection.

---

## 4. Architecture & File Decomposition

```
ModernWigiDash.Widgets/Steam/
├── Models/
│   ├── SteamAppManifest.cs
│   ├── SteamEnums.cs              [Update: Add HeroDeck to SteamLayoutMode enum]
│   ├── SteamGameItem.cs           [Add: HeroArtPath, Developer, Category flags]
│   └── SteamPersonaState.cs
├── Parsing/
│   ├── SteamLibraryLocator.cs     [Add: library_hero.jpg detection]
│   └── SteamVdfTokenizer.cs
├── Rendering/
│   ├── SteamHeroGeometry.cs       [New: Immutable geometry record for Hero Deck]
│   ├── SteamHeroLayout.cs         [New: Layout calculator for Hero Deck]
│   ├── SteamHeroRenderer.cs       [New: Zero-alloc 30 FPS renderer for Hero Deck]
│   ├── SteamCarouselGeometry.cs   [Renamed/updated from SteamShowcaseGeometry]
│   ├── SteamShowcaseLayout.cs     [Updated: includes filter pills and mode toggle]
│   ├── SteamShowcaseRenderer.cs   [Updated: Titanium Amber styling]
│   ├── SteamGridGeometry.cs       [Updated: includes filter pills and mode toggle]
│   ├── SteamGridLayout.cs         [Updated: includes filter pills and mode toggle]
│   ├── SteamGridRenderer.cs       [Updated: Titanium Amber styling]
│   └── SteamGeometryMemo.cs       [Updated: caches hero geometry]
├── Services/
│   ├── ISteamLibrarySource.cs
│   ├── ISteamPosterLookup.cs
│   ├── SteamLibraryService.cs
│   ├── SteamLibrarySnapshot.cs
│   ├── SteamPosterCache.cs        [24-slot LRU texture cache]
│   ├── SteamPosterPolicy.cs
│   ├── SteamPosterSource.cs       [Add: Hero banner paths]
│   ├── SteamProcessTracker.cs
│   └── SteamRenderFrame.cs
└── SteamLibraryWidget.cs          [Updated orchestration: mode cycling, filter state, touch routing]
```

---

## 5. Testing & Quality Strategy

1. **Unit & Layout Tests (`ModernWigiDash.Tests`):**
   - `SteamHeroLayoutTests.cs`: Verifies exact pixel dimensions and touch target hit boxes on 1016×592 bounds.
   - `SteamHeroRendererTests.cs`: Verifies zero allocation per tick, missing art fallbacks, running vs idle state rendering, and memoized string formatting.
   - `SteamLibraryWidgetTests.cs`:
     - Tapping the mode pill cycles `HeroDeck` → `ShowcaseCarousel` → `GridWall` → `HeroDeck`.
     - Tapping filter tabs filters displayed items (`All`, `Favorites`, `Recent`).
     - Tapping `PLAY NOW` dispatches launch command.
     - Tapping `STOP GAME` terminates process.
     - Long-press toggles favorite.
2. **Performance & Memory Verification:**
   - Zero per-frame memory allocation verified via analyzer rules and unit tests.
   - Bounded 24-slot texture cache tested under heavy simulated library scrolling.
3. **Build & Gate Enforcement:**
   - Solution builds cleanly in Release mode (`dotnet build -c Release`).
   - All 2,873+ existing unit tests plus new Steam tests pass.
   - `scripts/ref-check.ps1` and `scripts/ps-hygiene.ps1` clean.
   - Pre-commit gate guard passes.
