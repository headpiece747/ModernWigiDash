# ADR-0015: The inspector's choice arrays live in attributes and are pinned to the runtime catalogs by test

**Date:** 2026-08-24
**Status:** Accepted
**Deciders:** Project owner

## Context

C# attributes cannot reference runtime values: `[WidgetProperty]`'s
`Choices` is a compile-time literal. But the behavior truth for two widgets
is a runtime catalog: `HotkeyActionCatalog.Entries` (the action-type
vocabulary, the name-to-(kind, media key) mapping, the default, the
`NeedsCommand` rule) and `WeatherLayout.Modes` (the layout-mode catalog, the
cycle order, the display names). The inspector renders the attribute
literal; the behavior reads the catalog. The language forces two spellings
of one fact.

## Decision

The duplication is deliberate and pinned at the gate. The attribute literal
feeds the inspector UI and the runtime catalog feeds the behavior; the
lock-step pin tests fail the suite when the two drift:

- `HotkeyButtonWidgetTests.HotkeyActionAttribute_Options_MatchTheActionCatalog`:
  the attribute default equals the catalog's `DefaultName`, the option count
  equals the entry count, and each option in order parses back to its
  catalog entry's name and kind.
- `WeatherForecastWidgetTests.LayoutModeAttribute_Options_MatchTheModeCatalog`:
  the same shape against `WeatherLayout.Modes`.

A renamed or hand-edited entry compiles cleanly and fails the gate with a
coaching line. That is the failure shape this house wants: loud, local, and
fixable in one file.

## Exit plan

Drop the attribute literal and the pin when the two spellings become one:
the attribute system supports runtime-initialized properties (it does not as
of C# 14), or the inspector switches to the runtime provider
(`IWidgetPropertyOptionsProvider`) for static lists and the catalog owns the
single spelling.

Trigger conditions:

1. A lock-step pin fails (an entry renamed without its attribute): fix the
   pair in one change; the pin names which one drifted.
2. A third widget needs a static choice list (the pattern repeats a third
   time; static lists move to the provider and the literal retires).

## Consequences

**Positive:**

- The inspector and the behavior cannot drift silently: the gate catches the
  mismatch the compiler cannot see.
- Each side keeps the shape the language forces: the attribute stays a
  literal, the catalog stays the runtime owner of the behavior.
- The pins are small (a count plus ordered comparisons) and read as
  documentation of the contract.

**Negative (the debt this ADR registers):**

- Two spellings of one fact exist between the pins. A reader who opens only
  one of them sees a partial truth.
- Adding a choice is a two-file change (attribute + catalog), and forgetting
  one is a gate failure, not a compile error: the feedback is slower than it
  should be.

## Date

2026-08-24