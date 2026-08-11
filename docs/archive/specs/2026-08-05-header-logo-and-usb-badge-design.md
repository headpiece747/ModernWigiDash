> **Shipped** — implemented as of 2026-08-10 (commits through `fc42ac4`). Archived for history.

# Header Logo & Neutral Status Badge Specification

## Overview
This specification details two related changes to the ModernWigiDash application chrome:

1. **Rebrand the header with the new app logo** — replace the placeholder blue ⚡ box in the header with the real logo (`E:\Downloads\modernwigidashlogo.svg`, a 512×512 dark-navy rounded square with an amber "W" and white dot), and use that same logo for the window title bar, taskbar, all dialog title bars, and the Explorer/exe application icon.
2. **Restyle the WigiDash attach/detach indicator** as a neutral status pill matching the 🎨 Theme button (amber text unchanged), with a green dot when attached and a red dot when detached — replacing the current green/red background-and-border swap.

Both changes keep the established visual language: dark titanium surfaces, amber `M3Primary` accent, and theme-driven colors.

## Goals & Objectives
1. **Real Logo Everywhere**: Display the logo at 38×38 px, proportionally, top-aligned to span the two-line header title block (top of "MODERN WIGIDASH" to bottom of "Free-Form Canvas & Dynamic Engine"), and reuse it for the window icon, dialog icons, and the exe/Explorer icon.
2. **Standard Asset Pipeline**: Copy the SVG into the repository and pre-convert it to PNG (header/window) and multi-size ICO (exe) — the conventional desktop-program approach. No runtime SVG rendering.
3. **Neutral Status Pill**: The attach/detach indicator keeps a constant neutral pill (same look as the 🎨 Theme button) and communicates state only through an 8 px status dot — emerald when attached, red when detached — plus the existing amber label.
4. **Theme Consistency**: All new visuals use existing theme tokens so they follow `app_theme.json` changes; the `SuccessBackground`/`SuccessBorder` theme properties are **retained** because the widget action active-state buttons (active-action highlight in the properties panel) still consume them.

## Architectural & Component Changes

### 1. Header Logo (`MainWindow.xaml`)
Location: `ModernWigiDash.App/MainWindow.xaml` (lines 36-44)
- Replace the `<Border>` with the blue `AccentBlue` background and ⚡ `TextBlock` (currently 36×36) with:
  - `<Image Source="Resources/Logo/logo.png" Width="38" Height="38" Stretch="Uniform" VerticalAlignment="Top" Margin="0,0,12,0"/>`
  - `VerticalAlignment="Top"` aligns the logo top with the title's top line so the 38 px square spans the two-line title block (title block ≈ 33 px tall; the logo is slightly taller — "as big or bigger than that text").
- Keep the title/subtitle `StackPanel` unchanged (title white 16 SemiBold-Bold, subtitle 11 `AccentBlue`).

### 2. Neutral Status Badge (`MainWindow.xaml`)
Location: `ModernWigiDash.App/MainWindow.xaml` (lines 57-59)
- Restyle `UsbBadgeBorder` to match the 🎨 Theme button exactly:
  - `Background="{DynamicResource BgCard}"`, `BorderBrush="{DynamicResource BorderBrush}"`, `BorderThickness="1"`, `CornerRadius="8"`, `Padding="12,6"`.
- Add an 8 px status dot inside the pill, left of the text:
  - `<Ellipse x:Name="UsbStatusDot" Width="8" Height="8" Fill="{DynamicResource DangerBorder}" VerticalAlignment="Center" Margin="0,0,8,0"/>`
  - Initial `Fill` = `DangerBorder` (red) to match the default "WigiDash Detached" label.
- `TxtUsbStatus` unchanged: text stays "WigiDash Attached"/"WigiDash Detached", 12 SemiBold, `Foreground="{DynamicResource M3Primary}"` (amber).

### 3. Badge Runtime Logic (`MainWindow.xaml.cs`)
Location: `ModernWigiDash.App/MainWindow.xaml.cs`, `UpdateUsbBadge()` (line 2133)
- Pill is now static, so the method only switches the dot brush:
  - Attached: `UsbStatusDot.Fill = resources["AccentGreen"]` (`#10B981`)
  - Detached: `UsbStatusDot.Fill = resources["DangerBorder"]` (`#EF4444`)
- Remove the `UsbBadgeBorder.Background`/`BorderBrush` assignments (lines 2140-2141). No longer reads `SuccessBackground`/`SuccessBorder` or `DangerBackground`/`DangerBorder`.
- `UpdateUsbBadge()` continues to be invoked from the same attach/detach lifecycle events as today (initial sync + hardware events); XAML defaults match the Detached state.

### 4. Logo Assets
New directory: `ModernWigiDash.App/Resources/Logo/`
- `modernwigidashlogo.svg` — copied from `E:\Downloads\modernwigidashlogo.svg` (source of truth, 512×512).
- `logo.png` — 512×512, pre-rendered from the SVG (used by the header `<Image>`).
- `logo.ico` — multi-size ICO: 16, 24, 32, 48, 64, 128, 256 px, pre-rendered from the SVG (used by window/dialog icons and the exe application icon).
- Generated once by a one-off development-time converter (not shipped); regenerated only if the SVG changes.

### 5. Project File (`ModernWigiDash.App.csproj`)
- Add `<ApplicationIcon>Resources\Logo\logo.ico</ApplicationIcon>` to the `<PropertyGroup>` — sets the exe/Explorer icon and the default window-class icon.
- Register the logo assets with `CopyToOutputDirectory="PreserveNewest"` (matching the existing `Resources\Fonts\` pattern) so the relative `Resources/Logo/...` references resolve from the output directory.

### 6. Window & Dialog Icons
- Main window: add `Icon="Resources/Logo/logo.ico"` to `<Window ...>` in `MainWindow.xaml` (covers title bar and taskbar).
- Dialogs: code-built windows (`ShowThemeDialog`, `ShowHotkeyActionEditor`, `PromptForText`, `DeviceAuthorizationWindow`) already flow through the shared `ApplyDarkTitleBarToWindow` helper (`MainWindow.xaml.cs`, line 2169). Assign the same icon once inside that helper so every dialog — current and future — picks up the logo.

### 7. Explicitly Out of Scope
- `SuccessBackground` / `SuccessBorder` theme properties and their `App.xaml` resources: **kept** (still consumed by the active widget-action button styling at `MainWindow.xaml.cs:1100-1101`).
- `DangerBackground` / `DangerBorder`: unchanged (still the detached dot color and other danger affordances).
- No changes to `ThemeSettings.cs`, `App.xaml`, or the theme dialog.

## Verification Plan
1. **Automated**:
   - `dotnet build` on `ModernWigiDash.slnx` — 0 errors, no new warnings beyond the 4 pre-existing unrelated ones.
   - Run the unit test suite (`ModernWigiDash.Tests`).
2. **Manual**:
   - Launch the app (interactive `-test` service mode + app exe) and verify:
     - Header shows the logo at 38×38, top-aligned with the title block.
     - Badge is the neutral pill with amber text; red dot while "WigiDash Detached"; green dot after attaching (and back to red on detach).
     - Window title bar and taskbar show the logo; each dialog title bar shows the logo.
     - `ModernWigiDash.App.exe` shows the logo in Explorer.
