# Adaptive Editorial Calendar Design Specification

**Date:** 2026-09-06  
**Scope:** `ModernWigiDash.Widgets/` (`CalendarWidget.cs`, `CalendarPresentation.cs`, `CalendarLayout.cs`), `ModernWigiDash.Tests/`  
**Reference:** Editorial Graphic Poster Calendar 2026 PDF

---

## 1. Goal & Visual Direction

The goal is to elevate the `CalendarWidget` from a basic agenda view into a high-impact, graphic design editorial calendar inspired by the reference PDF. It introduces an **Adaptive Multi-View** system that dynamically formats its content based on widget dimensions:
- **5×4 (Full 1016×592 Screen)**: Full editorial poster layout featuring a vertical typography sidebar (`2026`, `12 months · 52 weeks · 365 days`), a bold center Poster Month Card, and a rich Live Agenda panel on the right.
- **4×2 / 3×2 (Wide Split Banner)**: Two-column layout with the Poster Month Card on the left and a 3-item upcoming agenda on the right.
- **2×2 / 2×3 (Compact Poster)**: Single-month poster card with high-contrast day matrix, circled date highlights, and a compact single-line "Next Event" summary bar at the bottom.

---

## 2. Palette & Theming Architecture

### 2.1 Seasonal Curated Palettes (from the 12 PDF Cards)
A new `CalendarSeasonalPalettes` static class provides the 12 signature colorways extracted from the reference PDF:
- **Jan**: Midnight Navy (`#141738`), Text `#FFFFFF`
- **Feb**: Vibrant Magenta (`#E53E7A`), Text `#FFFFFF`
- **Mar**: Forest Teal (`#14362E`), Text `#FFFFFF`
- **Apr**: Canary Yellow (`#F5C800`), Text `#1E293B`
- **May**: Royal Indigo (`#293B86`), Text `#FFFFFF`
- **Jun**: Cobalt Blue (`#0A2D75`), Text `#FFFFFF`
- **Jul**: Deep Wine (`#471322`), Text `#FFFFFF`
- **Aug**: Mint Sage (`#D9F2C7`), Text `#064E3B`
- **Sep**: Warm Tangerine (`#FF4D2D`), Text `#FFFFFF`
- **Oct**: Golden Ochre (`#EAA812`), Text `#1E293B`
- **Nov**: Electric Azure (`#005CB9`), Text `#FFFFFF`
- **Dec**: Warm Crimson (`#8B1E3F`), Text `#FFF5F5`

### 2.2 Widget Properties
The widget gains configurable properties:
- `ThemeMode`: Choice (`"Seasonal"`, `"Custom"`). Default: `"Seasonal"`.
- `LayoutMode`: Choice (`"Auto"`, `"Poster Month"`, `"Agenda"`). Default: `"Auto"`.
- Preserves existing properties: `FeedsJson`, `PollIntervalMinutes`, `TimedRows`, `AccentColorHex`, `TextColorHex`.

---

## 3. Notable Dates & Holiday Legend
In the reference PDF, each month card includes a bottom legend of notable holidays/events (e.g. `Jan 1 : New Year's Day 2026`, `Feb 14 : Valentine's Day`, `Sep 7 : Labor Day`).
- A `CalendarNotableDates` helper provides key standard observances for 2026.
- In addition, all-day events from the user's synced feeds for that month are surfaced here.
- Dates with events or notable observances receive a distinct circled badge or dot marker on the day matrix.

---

## 4. Adaptive Layout Calculations

`CalendarLayout` evaluates widget bounds to select one of three layout modes:

```
+-----------------------------------------------------------------------------------+
| 5x4 Full Canvas (>= 880w, >= 480h)                                                |
| +------------+-----------------------------------+------------------------------+ |
| | Left Strip | Center Poster Month Card          | Right Agenda Panel           | |
| | "2026"     | - Year Badge /2026 & Month "SEP"  | - Next Event Hero Card       | |
| | 12 months  | - M T W T F S S Matrix            | - Chronological Event Cards  | |
| | 52 weeks   | - Circled highlights (Today, evs) | - Feed sync status           | |
| | 365 days   | - Notable Dates Legend            | - Link / Details affordance  | |
| +------------+-----------------------------------+------------------------------+ |
+-----------------------------------------------------------------------------------+

+-----------------------------------------------------------------------------------+
| 4x2 Wide Split (>= 580w, < 480h)                                                  |
| +------------------------------------+------------------------------------------+ |
| | Poster Month Card (~45% width)     | Upcoming Agenda (~55% width)             | |
| | - Month "SEP", Matrix, Legend      | - Chronological Event Rows               | |
| +------------------------------------+------------------------------------------+ |
+-----------------------------------------------------------------------------------+

+-------------------------------------------------------+
| 2x3 Compact Poster (< 580w)                           |
| +---------------------------------------------------+ |
| | Poster Month Card                                 | |
| | - Year tag, Month "SEP", Matrix                   | |
| | - Circled Today & Events                          | |
| | - Bottom Next Event Summary bar                   | |
| +---------------------------------------------------+ |
+-------------------------------------------------------+
```

---

## 5. Interaction Model

1. **Month Navigation**:
   - Horizontal swipe (dx > 40px) or tapping month navigation chevrons advances or rewinds the viewed month (`_viewMonthOffset`).
2. **Day Filtering**:
   - Tapping any date in the month grid selects that date and filters the agenda panel to show that day's schedule.
3. **Event Inspection**:
   - Tapping an event row in the agenda opens the event detail overlay with full start/end times, description, location, and meeting URL launcher.

---

## 6. Performance & Quality Gates

- **Zero steady-state allocations**: All text measurements and rendering use `FontHelper.GetCachedFont`. Paints are hoisted as instance fields with dynamic color mutation.
- **Null tolerance & graceful fallback**: If feeds are empty or network is offline, the poster month card and notable dates render cleanly without throwing.
- **Regression Prevention**: All 82 existing calendar unit tests must pass without modification; new unit tests will pin the adaptive layout calculations, seasonal palette resolution, and notable dates logic.
