# Titanium & Sunset Amber Default Color Theme Specification

## Overview
This specification details the updated default color theme for the ModernWigiDash application chrome and widgets (Option C: Titanium & Sunset Amber). It establishes a premium, visually appealing out-of-the-box default palette with dark titanium zinc surfaces (`#121214`), rich sunset amber accents (`#F59E0B`), warm amber glow highlights (`#FBBF24`), emerald status greens (`#10B981`), and high-contrast warm white text (`#FAFAFA`). Furthermore, it ensures dark immersive title bars (`DwmSetWindowAttribute`) are applied across all window dialogs (`MainWindow`, `ShowThemeDialog`, `ShowHotkeyActionEditor`, `PromptForText`, `DeviceAuthorizationWindow`).

## Goals & Objectives
1. **Premium Default Palette**: Replace mismatched muddy default colors (`#1B2930` dark teal, `#870000` crimson, `#FFCD85` beige) with a unified Titanium & Sunset Amber palette.
2. **Comprehensive Title Bar & Window Styling**: Apply DWM immersive dark title bar color (`DwmSetWindowAttribute`) with `ThemeSettings.Theme.TitleBar` across **all** application windows, modal dialogs, and popups.
3. **Widget Default Color Alignment**: Update default accent and text color defaults across built-in widgets (Clock, Telemetry, Audio, NowPlaying, Twitch, Utility) to match Sunset Amber (`#F59E0B`) and Titanium (`#121214` / `#1A1A1E`).
4. **.NET 10 & C# Best Practices**: Target `.NET 10.0`, enforce strict nullability (`#nullable enable`), pattern matching, `using` disposal, and SonarAnalyzer compliance.

## Architectural & Component Changes

### 1. `ThemeSettings.cs` Defaults & Property Updates
Location: `ModernWigiDash.Core/Theming/ThemeSettings.cs`
- Update default color properties:
  - `BgDark`: `#121214` (Deep Titanium Gray)
  - `BgPanel`: `#1A1A1E` (Dark Titanium Panel)
  - `BgCard`: `#26262B` (Elevated Titanium Card)
  - `Border`: `#3F3F46` (Subtle Zinc Border)
  - `AccentRed`: `#F59E0B` (Sunset Amber Primary Accent)
  - `M3Primary`: `#FBBF24` (Warm Amber Glow)
  - `M3PrimaryContainer`: `#3F3F46` (Badge Background)
  - `M3OnPrimaryContainer`: `#FBBF24` (Badge Text)
  - `AccentGreen`: `#10B981` (Emerald Green Status)
  - `TextPrimary`: `#FAFAFA` (Crisp Warm Off-White)
  - `TextSecondary`: `#A1A1AA` (Muted Zinc Gray)
  - `ControlHover`: `#3F3F46` (Hover Background)
  - `DropdownHover`: `#2A2A30` (Dropdown Hover)
  - `TitleBar`: `#0B0B0C` (Sleek Dark Title Bar)
  - `StatusBarBackground`: `#0E0E10` (Status Bar Background)
  - `DangerBackground`: `#7F1D1D`
  - `DangerBorder`: `#EF4444`
  - `SuccessBackground`: `#064E3B`
  - `SuccessBorder`: `#10B981`

### 2. Application Resource Dictionary (`App.xaml`)
Location: `ModernWigiDash.App/App.xaml`
- Update XAML default `<Color x:Key="...">` values to match the new `ThemeSettings.cs` defaults.

### 3. Application Window Title Bar Enforcement (`MainWindow.xaml.cs`)
Location: `ModernWigiDash.App/MainWindow.xaml.cs`
- Ensure `ApplyDarkTitleBarToWindow` is hooked up via `SourceInitialized` on all dialog windows:
  1. Main Application Window (`MainWindow`)
  2. `ShowThemeDialog()`
  3. `ShowHotkeyActionEditor()`
  4. `PromptForText()`
  5. `_deviceAuthorizationWindow`

### 4. Built-In Widget Default Palette Updates
Location: `ModernWigiDash.Widgets/*.cs`
- Update default `AccentColorHex` / `PrimaryColorHex` properties from `#FFCD85` or `#870000` to `#F59E0B` (Sunset Amber).
- Update default `TextColorHex` to `#FAFAFA`.
- Update default `BackgroundHex` to `#121214` / `#1A1A1E`.

## Verification Plan
1. **Automated Unit Tests**:
   - Run unit test suite in `ModernWigiDash.Tests`.
   - Update default color assertions in `UnitTestSuite.cs` to match the new `#F59E0B` / `#FAFAFA` / `#121214` defaults.
2. **Manual Verification**:
   - Build `ModernWigiDash.slnx` using `dotnet build`.
   - Launch application and verify that windows, dialogs, title bars, and widgets display the new Titanium & Sunset Amber theme out of the box.
