> **Shipped** — implemented as of 2026-08-10 (commits through `fc42ac4`): `ThemeSettings` defaults `#121214`/`#F59E0B` + dark title-bar styling. Archived for history.

# Titanium & Sunset Amber Default Theme Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Update the default application chrome and widget color palette to Titanium & Sunset Amber (`#F59E0B` / `#121214`), and enforce dark immersive title bar styling across all app windows and dialogs.

**Architecture:** Update `ThemeSettings.cs` defaults, `App.xaml` color resources, `MainWindow.xaml.cs` window initialization hooks, and default widget properties across `ModernWigiDash.Widgets`.

**Tech Stack:** .NET 10.0, WPF, SkiaSharp, MSTest for unit tests.

## Global Constraints

- TargetFramework: `net10.0-windows10.0.19041.0`
- C# standard: `#nullable enable`, zero compiler warnings.

---

### Task 1: Update Default Theme Colors in ThemeSettings.cs and App.xaml

**Files:**
- Modify: `ModernWigiDash.Core/Theming/ThemeSettings.cs:20-47`
- Modify: `ModernWigiDash.App/App.xaml:6-30`
- Test: `ModernWigiDash.Tests/UnitTestSuite.cs:450-470`

**Interfaces:**
- Produces: Default Titanium & Sunset Amber color values in `ThemeSettings` and `App.xaml`.

- [ ] **Step 1: Write the failing test**

In `ModernWigiDash.Tests/UnitTestSuite.cs`, add test `ThemeSettings_DefaultsToTitaniumAmberPalette`:

```csharp
[Test]
public void ThemeSettings_DefaultsToTitaniumAmberPalette()
{
    var theme = new ThemeSettings();
    Assert.AreEqual("#121214", theme.BgDark);
    Assert.AreEqual("#1A1A1E", theme.BgPanel);
    Assert.AreEqual("#26262B", theme.BgCard);
    Assert.AreEqual("#3F3F46", theme.Border);
    Assert.AreEqual("#F59E0B", theme.AccentRed);
    Assert.AreEqual("#FBBF24", theme.M3Primary);
    Assert.AreEqual("#FAFAFA", theme.TextPrimary);
    Assert.AreEqual("#A1A1AA", theme.TextSecondary);
    Assert.AreEqual("#0B0B0C", theme.TitleBar);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test ModernWigiDash.Tests/ModernWigiDash.Tests.csproj --filter "FullyQualifiedName~ThemeSettings_DefaultsToTitaniumAmberPalette"`
Expected: FAIL with "Expected #121214 but got #1B2930"

- [ ] **Step 3: Update ThemeSettings.cs and App.xaml defaults**

In `ModernWigiDash.Core/Theming/ThemeSettings.cs`:
```csharp
    // Surfaces
    public string BgDark { get; set; } = "#121214";
    public string BgPanel { get; set; } = "#1A1A1E";
    public string BgCard { get; set; } = "#26262B";
    public string Border { get; set; } = "#3F3F46";

    // Accents
    public string AccentRed { get; set; } = "#F59E0B";
    public string M3Primary { get; set; } = "#FBBF24";
    public string M3PrimaryContainer { get; set; } = "#3F3F46";
    public string M3OnPrimaryContainer { get; set; } = "#FBBF24";
    public string AccentGreen { get; set; } = "#10B981";

    // Text
    public string TextPrimary { get; set; } = "#FAFAFA";
    public string TextSecondary { get; set; } = "#A1A1AA";

    // Interactive states & chrome extras
    public string ControlHover { get; set; } = "#3F3F46";
    public string DropdownHover { get; set; } = "#2A2A30";
    public string TitleBar { get; set; } = "#0B0B0C";
    public string StatusBarBackground { get; set; } = "#0E0E10";
    public string DangerBackground { get; set; } = "#7F1D1D";
    public string DangerBorder { get; set; } = "#EF4444";
    public string SuccessBackground { get; set; } = "#064E3B";
    public string SuccessBorder { get; set; } = "#10B981";
```

In `ModernWigiDash.App/App.xaml`:
```xml
        <Color x:Key="BgDarkColor">#121214</Color>
        <Color x:Key="BgPanelColor">#1A1A1E</Color>
        <Color x:Key="BgCardColor">#26262B</Color>
        <Color x:Key="BorderColor">#3F3F46</Color>
        
        <!-- Primary & Secondary Accent Tokens -->
        <Color x:Key="AccentRedColor">#F59E0B</Color>
        <Color x:Key="M3PrimaryColor">#FBBF24</Color>
        <Color x:Key="M3PrimaryContainerColor">#3F3F46</Color>
        <Color x:Key="M3OnPrimaryContainerColor">#FBBF24</Color>
        <Color x:Key="AccentGreenColor">#10B981</Color>
        <Color x:Key="TextPrimaryColor">#FAFAFA</Color>
        <Color x:Key="TextSecondaryColor">#A1A1AA</Color>

        <!-- Interactive states & chrome extras -->
        <Color x:Key="ControlHoverColor">#3F3F46</Color>
        <Color x:Key="DropdownHoverColor">#2A2A30</Color>
        <Color x:Key="TitleBarColor">#0B0B0C</Color>
        <Color x:Key="StatusBarBackgroundColor">#0E0E10</Color>
        <Color x:Key="DangerBackgroundColor">#7F1D1D</Color>
        <Color x:Key="DangerBorderColor">#EF4444</Color>
        <Color x:Key="SuccessBackgroundColor">#064E3B</Color>
        <Color x:Key="SuccessBorderColor">#10B981</Color>
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test ModernWigiDash.Tests/ModernWigiDash.Tests.csproj --filter "FullyQualifiedName~ThemeSettings_DefaultsToTitaniumAmberPalette"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add ModernWigiDash.Core/Theming/ThemeSettings.cs ModernWigiDash.App/App.xaml ModernWigiDash.Tests/UnitTestSuite.cs
git commit -m "feat(theme): update default theme colors to Titanium & Sunset Amber"
```

---

### Task 2: Enforce Dark Title Bar Styling Across All Dialog Windows

**Files:**
- Modify: `ModernWigiDash.App/MainWindow.xaml.cs:1255-1270`
- Modify: `ModernWigiDash.App/MainWindow.xaml.cs:1570-1585`
- Modify: `ModernWigiDash.App/MainWindow.xaml.cs:1770-1788`
- Modify: `ModernWigiDash.App/MainWindow.xaml.cs:1940-1955`

**Interfaces:**
- Produces: Immersive dark title bar styling (`ApplyDarkTitleBarToWindow`) attached via `SourceInitialized` on all dialog windows.

- [ ] **Step 1: Enforce SourceInitialized title bar hook on all dialogs**

In `ModernWigiDash.App/MainWindow.xaml.cs`:
1. In `ShowHotkeyActionEditor` (around line 1265):
   Add: `dialog.SourceInitialized += (_, _) => ApplyDarkTitleBarToWindow(dialog, ThemeSettings.Theme.TitleBar);`
2. In `PromptForText` (around line 1584):
   Add: `dialog.SourceInitialized += (_, _) => ApplyDarkTitleBarToWindow(dialog, ThemeSettings.Theme.TitleBar);`
3. Verify `ShowThemeDialog` (line 1950) and `_deviceAuthorizationWindow` (line 1786) have `SourceInitialized += (_, _) => ApplyDarkTitleBarToWindow(..., ThemeSettings.Theme.TitleBar);`.

- [ ] **Step 2: Run solution build to verify zero compiler warnings or errors**

Run: `dotnet build ModernWigiDash.slnx`
Expected: Build succeeded with 0 warnings, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add ModernWigiDash.App/MainWindow.xaml.cs
git commit -m "feat(theme): enforce dark title bar styling across all app dialog windows"
```

---

### Task 3: Update Default Colors Across Built-In Widgets and Tests

**Files:**
- Modify: `ModernWigiDash.Widgets/AudioAndMediaWidgets.cs`
- Modify: `ModernWigiDash.Widgets/DigitalAnalogClockWidget.cs`
- Modify: `ModernWigiDash.Widgets/FrameTimeWidget.cs`
- Modify: `ModernWigiDash.Widgets/NowPlayingWidget.cs`
- Modify: `ModernWigiDash.Widgets/SocialAndVisualWidgets.cs`
- Modify: `ModernWigiDash.Widgets/SystemTelemetryWidgets.cs`
- Modify: `ModernWigiDash.Widgets/UtilityAndInteractiveWidgets.cs`
- Test: `ModernWigiDash.Tests/UnitTestSuite.cs`

**Interfaces:**
- Produces: Updated default accent (`#F59E0B`), text (`#FAFAFA`), and background hexes across built-in widgets.

- [ ] **Step 1: Update widget default color properties**

Update default properties across widget classes:
- Change default `#FFCD85` or `#870000` accent colors to `#F59E0B`.
- Change default `#FFFFFF` or `#C6E0FF` text colors to `#FAFAFA`.

- [ ] **Step 2: Update unit test default color assertions**

In `ModernWigiDash.Tests/UnitTestSuite.cs`, update test assertions:
- `Assert.AreEqual("#FAFAFA", new DigitalAnalogClockWidget().TextColorHex);`
- `Assert.AreEqual("#F59E0B", new HardwareMonitorWidget().AccentColorHex);`
- `Assert.AreEqual("#F59E0B", new FrameTimeWidget().AccentColorHex);`
- `Assert.AreEqual("#FAFAFA", new FrameTimeWidget().TextColorHex);`
- `Assert.AreEqual("#F59E0B", new NowPlayingWidget().AccentColorHex);`
- `Assert.AreEqual("#FAFAFA", new HotkeyButtonWidget().TextColorHex);`
- `Assert.AreEqual("#FAFAFA", new StopwatchTimerWidget().TextColorHex);`
- `Assert.AreEqual("#FAFAFA", new CryptoStockTickerWidget().TextColorHex);`
- `Assert.AreEqual("#FAFAFA", new PictureAndGifWidget().TextColorHex);`
- `Assert.AreEqual("#F59E0B", new HotkeyButtonWidget().ButtonColorHex);`

- [ ] **Step 3: Run full unit test suite**

Run: `dotnet test ModernWigiDash.Tests/ModernWigiDash.Tests.csproj`
Expected: PASS (All tests pass)

- [ ] **Step 4: Commit**

```bash
git add ModernWigiDash.Widgets/ ModernWigiDash.Tests/UnitTestSuite.cs
git commit -m "feat(theme): update built-in widget default color properties to Sunset Amber & Titanium"
```
