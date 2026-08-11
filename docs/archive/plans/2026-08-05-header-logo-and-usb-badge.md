# Header Logo & Neutral USB Status Badge Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the header ⚡ placeholder with the real app logo (header/window/taskbar/dialog/exe icon) and restyle the WigiDash attach/detach indicator as a neutral pill with a green/red status dot.

**Architecture:** The logo is shipped as pre-rendered assets (PNG for the header, multi-size ICO for window/executable icons) committed under `ModernWigiDash.App/Resources/Logo/`, referenced statically from XAML and one shared code helper. The badge becomes a static theme-driven pill whose only mutable element is an 8px `Ellipse` dot, switched by the existing `UpdateUsbBadge()` method. No new runtime dependencies; no runtime SVG rendering.

**Tech Stack:** WPF (.NET 10, `net10.0-windows10.0.19041.0`), C#, MSTest, SkiaSharp (`SkiaSharp.Views.WPF` 4.150.1 already referenced). The one-off SVG→PNG/ICO converter uses `Svg.Skia` but is a throwaway tool that is **not** committed.

## Global Constraints

- Target framework `net10.0-windows10.0.19041.0`; do not add new project references or runtime packages.
- **Do not modify** `ThemeSettings.cs`, `App.xaml`, or the theme dialog. `SuccessBackground`/`SuccessBorder` and `DangerBackground`/`DangerBorder` stay (the former still style active widget-action buttons).
- Badge pill must visually match the 🎨 Theme button: `Background={DynamicResource BgCard}`, `BorderBrush={DynamicResource BorderBrush}`, `BorderThickness=1`, `CornerRadius=8`, `Padding=12,6`.
- Status dot is 8×8: `AccentGreen` when attached, `DangerBorder` when detached. Amber `M3Primary` text unchanged.
- Header logo: 38×38, `Stretch=Uniform` (proportional), `VerticalAlignment="Top"` aligned with the title's first line.
- Logo assets registered exactly like the existing fonts: `<Resource Include="Resources\Logo\..." ><CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory></Resource>`.
- Commit messages follow repo style (`feat(app):`, `fix(app):`, etc.). Stage only the files named in each task.
- **Working-tree hygiene first:** The tree currently has uncommitted dropdown-fix changes in `ModernWigiDash.App/MainWindow.xaml.cs`, `ModernWigiDash.Tests/UnitTestSuite.cs`, `ModernWigiDash.Widgets/SocialAndVisualWidgets.cs`, `ModernWigiDash.Widgets/UtilityAndInteractiveWidgets.cs`, deleted `.superpowers/sdd/*` files, and untracked `.superpowers/brainstorm/`. Before Task 1, commit the dropdown-fix changes + the sdd deletions as one separate commit (e.g. `fix(app): keep widget combo dropdowns within screen bounds`). Never stage `.superpowers/brainstorm/`.

## File Structure

- Create: `ModernWigiDash.App/Resources/Logo/modernwigidashlogo.svg` — copied source of truth from `E:\Downloads\modernwigidashlogo.svg`.
- Create: `ModernWigiDash.App/Resources/Logo/logo.png` — 512×512 pre-rendered (header image).
- Create: `ModernWigiDash.App/Resources/Logo/logo.ico` — multi-size (16/24/32/48/64/128/256) pre-rendered (window, dialog, exe icon).
- Modify: `ModernWigiDash.App/ModernWigiDash.App.csproj` — `<ApplicationIcon>` + asset registration.
- Modify: `ModernWigiDash.App/MainWindow.xaml` — window `Icon`, header logo `Image`, badge restyle + dot.
- Modify: `ModernWigiDash.App/MainWindow.xaml.cs` — dialog icon assignment in `ApplyDarkTitleBarToWindow` (line 2169); dot-only `UpdateUsbBadge` (line 2133).
- Throwaway (never committed): `C:\Users\tobia\AppData\Local\Temp\opencode\LogoAssetConverter\` — SVG→PNG/ICO converter.

---

### Task 1: Generate logo assets (PNG + ICO + SVG copy)

**Files:**
- Create (throwaway): `C:\Users\tobia\AppData\Local\Temp\opencode\LogoAssetConverter\LogoAssetConverter.csproj`
- Create (throwaway): `C:\Users\tobia\AppData\Local\Temp\opencode\LogoAssetConverter\Program.cs`
- Create: `ModernWigiDash.App/Resources/Logo/modernwigidashlogo.svg`
- Create: `ModernWigiDash.App/Resources/Logo/logo.png`
- Create: `ModernWigiDash.App/Resources/Logo/logo.ico`

**Interfaces:**
- Consumes: `E:\Downloads\modernwigidashlogo.svg` (512×512, viewBox `0 0 512 512`).
- Produces: `logo.png` (512×512) and `logo.ico` (16/24/32/48/64/128/256) used by every later task; `Resources/Logo/` paths are referenced as `Resources/Logo/logo.png` (relative) and `pack://application:,,,/Resources/Logo/logo.ico` (embedded pack URI).

- [ ] **Step 1: Commit the pending dropdown-fix work first** (see Global Constraints) so this task's commit stays clean:
  ```bash
  git add ModernWigiDash.App/MainWindow.xaml.cs ModernWigiDash.Tests/UnitTestSuite.cs ModernWigiDash.Widgets/SocialAndVisualWidgets.cs ModernWigiDash.Widgets/UtilityAndInteractiveWidgets.cs .superpowers/sdd
  git commit -m "fix(app): keep widget combo dropdowns within screen bounds"
  ```

- [ ] **Step 2: Create the throwaway converter project**

`LogoAssetConverter.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Svg.Skia" Version="2.0.1" />
  </ItemGroup>
</Project>
```
If restore fails on 2.0.1, use the latest 2.x of `Svg.Skia`.

- [ ] **Step 3: Create the converter program**

`Program.cs`:
```csharp
using SkiaSharp;
using Svg.Skia;

const string SvgPath = @"E:\Downloads\modernwigidashlogo.svg";
const string OutPng = @"C:\Users\tobia\.gemini\antigravity\scratch\ModernWigiDash\ModernWigiDash.App\Resources\Logo\logo.png";
const string OutIco = @"C:\Users\tobia\.gemini\antigravity\scratch\ModernWigiDash\ModernWigiDash.App\Resources\Logo\logo.ico";

var svg = new SKSvg();
svg.Load(SvgPath);
var picture = svg.Picture ?? throw new InvalidOperationException("SVG failed to load");

byte[] RenderPng(int size)
{
    using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
    var canvas = surface.Canvas;
    canvas.Clear(SKColors.Transparent);
    canvas.Scale(size / 512f, size / 512f);
    using var paint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High };
    canvas.DrawPicture(picture, paint);
    using var image = surface.Snapshot();
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    return data.ToArray();
}

byte[] render512 = RenderPng(512);
File.WriteAllBytes(OutPng, render512);

int[] sizes = { 256, 128, 64, 48, 32, 24, 16 };
var entries = sizes.Select(s => (size: s, bytes: RenderPng(s))).ToArray();
int headerSize = 6 + 16 * entries.Length;
using var ms = new MemoryStream();
using var bw = new BinaryWriter(ms);
bw.Write((ushort)0);                       // reserved
bw.Write((ushort)1);                       // type: icon
bw.Write((ushort)entries.Length);          // image count
int offset = headerSize;
foreach (var e in entries)
{
    bw.Write((byte)(e.size == 256 ? 0 : e.size));  // width (0 = 256)
    bw.Write((byte)(e.size == 256 ? 0 : e.size));  // height
    bw.Write((byte)0);                             // color count
    bw.Write((byte)0);                             // reserved
    bw.Write((ushort)1);                           // planes
    bw.Write((ushort)32);                          // bit count
    bw.Write((uint)e.bytes.Length);
    bw.Write((uint)offset);
    offset += e.bytes.Length;
}
foreach (var e in entries) bw.Write(e.bytes);
File.WriteAllBytes(OutIco, ms.ToArray());

Console.WriteLine($"PNG {render512.Length} bytes, ICO {new FileInfo(OutIco).Length} bytes");
```

- [ ] **Step 4: Run the converter**

Run: `dotnet run` (workdir: `C:\Users\tobia\AppData\Local\Temp\opencode\LogoAssetConverter`)
Expected: prints `PNG <n> bytes, ICO <m> bytes` and both files exist.

- [ ] **Step 5: Verify the generated assets**

Run:
```powershell
Add-Type -AssemblyName System.Drawing
$png = [System.Drawing.Image]::FromFile('C:\Users\tobia\.gemini\antigravity\scratch\ModernWigiDash\ModernWigiDash.App\Resources\Logo\logo.png')
"PNG: $($png.Width)x$($png.Height)"
$png.Dispose()
$b = [System.IO.File]::ReadAllBytes('C:\Users\tobia\.gemini\antigravity\scratch\ModernWigiDash\ModernWigiDash.App\Resources\Logo\logo.ico')
"ICO type=$($b[2]) count=$([BitConverter]::ToUInt16($b, 4))"
```
Expected: `PNG: 512x512` and `ICO type=1 count=7`.

- [ ] **Step 6: Copy the source SVG into the repo**

Copy `E:\Downloads\modernwigidashlogo.svg` → `ModernWigiDash.App\Resources\Logo\modernwigidashlogo.svg`.

- [ ] **Step 7: Commit**

```bash
git add ModernWigiDash.App/Resources/Logo
git commit -m "feat(assets): add app logo PNG and multi-size ICO from source SVG"
```

---

### Task 2: Register application icon and logo resources in the csproj

**Files:**
- Modify: `ModernWigiDash.App/ModernWigiDash.App.csproj`

**Interfaces:**
- Consumes: the three `Resources/Logo/` files from Task 1.
- Produces: exe/Explorer icon (`<ApplicationIcon>`) and output-copied + embedded assets so relative XAML refs and the `pack://application:,,,/Resources/Logo/logo.ico` URI resolve.

- [ ] **Step 1: Add the ApplicationIcon property**

In `ModernWigiDash.App.csproj`, inside the `<PropertyGroup>` block (after the `<ApplicationManifest>app.manifest</ApplicationManifest>` line at line 23), add:
```xml
    <ApplicationIcon>Resources\Logo\logo.ico</ApplicationIcon>
```

- [ ] **Step 2: Register the logo assets**

In the `<ItemGroup>` that holds the font resources (after line 43, the `Geist-Bold.ttf` entry), add:
```xml
    <Resource Include="Resources\Logo\modernwigidashlogo.svg">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </Resource>
    <Resource Include="Resources\Logo\logo.png">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </Resource>
    <Resource Include="Resources\Logo\logo.ico">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </Resource>
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build ModernWigiDash.App\ModernWigiDash.App.csproj -nologo`
Expected: 0 errors. Then verify assets landed in the output and the exe grew:
```powershell
Get-ChildItem 'ModernWigiDash.App\bin\Debug\net10.0-windows10.0.19041.0\Resources\Logo'
Get-Item 'ModernWigiDash.App\bin\Debug\net10.0-windows10.0.19041.0\ModernWigiDash.App.exe' | Select-Object Name, Length
```
Expected: three files listed; exe exists with a nonzero size. If the build reports a duplicate-item error for the `.png`/`.ico`, remove the `Resource` entries for those two and re-add them as `Content` with the same `CopyToOutputDirectory` metadata (pack-URI embedding is then unavailable, so use relative paths only — see Task 4 note).

- [ ] **Step 4: Commit**

```bash
git add ModernWigiDash.App/ModernWigiDash.App.csproj ModernWigiDash.App/Resources/Logo
git commit -m "feat(app): set exe application icon and register logo assets"
```

---

### Task 3: Header logo in the top bar

**Files:**
- Modify: `ModernWigiDash.App/MainWindow.xaml:35-44`

**Interfaces:**
- Consumes: `logo.png` (relative path from Task 1/2).
- Produces: the 38×38 top-aligned header `<Image>`; later tasks don't depend on it.

- [ ] **Step 1: Replace the ⚡ box with the logo Image**

Replace the header logo block (current lines 36-44) with:
```xml
                <!-- Left Title & Logo -->
                <StackPanel Grid.Column="0" Orientation="Horizontal" VerticalAlignment="Center">
                    <Image Source="Resources/Logo/logo.png" Width="38" Height="38" Stretch="Uniform"
                           VerticalAlignment="Top" Margin="0,0,12,0"/>
                    <StackPanel VerticalAlignment="Top">
                        <TextBlock Text="MODERN WIGIDASH" FontSize="16" FontWeight="Bold" Foreground="White"/>
                        <TextBlock Text="Free-Form Canvas &amp; Dynamic Engine" FontSize="11" Foreground="{DynamicResource AccentBlue}"/>
                    </StackPanel>
                </StackPanel>
```
Both `Image` and the text `StackPanel` are `VerticalAlignment="Top"` so the logo's top edge aligns with the first title line (logo is 38px tall vs ~33px of title block — "as big or bigger than that text").

- [ ] **Step 2: Build to verify**

Run: `dotnet build ModernWigiDash.App\ModernWigiDash.App.csproj -nologo`
Expected: 0 errors.

- [ ] **Step 3: Manual visual check**

Launch the app and confirm the header shows the logo at 38×38, top-aligned with the two-line title, replacing the old blue ⚡ box.
```powershell
Start-Process 'ModernWigiDash.App\bin\Debug\net10.0-windows10.0.19041.0\ModernWigiDash.App.exe'
```
(If the app needs the service, start it first: `.\ModernWigiDash.Service\bin\Debug\net10.0-windows10.0.19041.0\ModernWigiDash.Service.exe -test`.)

- [ ] **Step 4: Commit**

```bash
git add ModernWigiDash.App/MainWindow.xaml
git commit -m "feat(app): replace header placeholder with app logo"
```

---

### Task 4: Window and dialog title-bar icons

**Files:**
- Modify: `ModernWigiDash.App/MainWindow.xaml:8-18`
- Modify: `ModernWigiDash.App/MainWindow.xaml.cs:2169`

**Interfaces:**
- Consumes: `logo.ico` as embedded pack URI (Task 1/2).
- Produces: main window icon (title bar + taskbar) and the same icon on all four code-built dialogs (`ShowThemeDialog` ~2204, `ShowHotkeyActionEditor` 1838, `PromptForText` 1462, `DeviceAuthorizationWindow` 2040) — all flow through `ApplyDarkTitleBarToWindow`.

- [ ] **Step 1: Add the Icon to MainWindow**

In `MainWindow.xaml`, immediately after the `Title=` attribute (line 8), add:
```xml
        Icon="Resources/Logo/logo.ico"
```

- [ ] **Step 2: Assign the icon in the shared dialog helper**

In `ApplyDarkTitleBarToWindow` (`MainWindow.xaml.cs`, line 2169), add this at the top of the method body, before `var hwnd = ...`:
```csharp
        if (window.Icon == null)
        {
            window.Icon = new System.Windows.Media.Imaging.BitmapImage(
                new System.Uri("pack://application:,,,/Resources/Logo/logo.ico"));
        }
```
Note: if Task 2 required the `Content` fallback (no pack URI), use this instead:
```csharp
        if (window.Icon == null)
        {
            string path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "Logo", "logo.ico");
            window.Icon = new System.Windows.Media.Imaging.BitmapImage(new System.Uri(path));
        }
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build ModernWigiDash.App\ModernWigiDash.App.csproj -nologo`
Expected: 0 errors.

- [ ] **Step 4: Manual visual check**

Launch the app and confirm: the main window title bar and taskbar show the logo; open the 🎨 Theme dialog and one other dialog and confirm each title bar shows the logo (not a blank/default icon).

- [ ] **Step 5: Commit**

```bash
git add ModernWigiDash.App/MainWindow.xaml ModernWigiDash.App/MainWindow.xaml.cs
git commit -m "feat(app): apply app logo to window and dialog title bar icons"
```

---

### Task 5: Neutral USB badge pill + status dot (XAML)

**Files:**
- Modify: `ModernWigiDash.App/MainWindow.xaml:56-59`

**Interfaces:**
- Consumes: theme brushes `BgCard`, `BorderBrush`, `M3Primary`, `DangerBorder` (all exist in `App.xaml`/`ThemeManager`).
- Produces: named elements `UsbBadgeBorder` and `UsbStatusDot` consumed by Task 6.

- [ ] **Step 1: Restyle the badge**

Replace the current `UsbBadgeBorder` block (lines 57-59) with:
```xml
                    <Border x:Name="UsbBadgeBorder" Background="{DynamicResource BgCard}" BorderBrush="{DynamicResource BorderBrush}" BorderThickness="1" CornerRadius="8" Padding="12,6">
                        <StackPanel Orientation="Horizontal">
                            <Ellipse x:Name="UsbStatusDot" Width="8" Height="8" Fill="{DynamicResource DangerBorder}" VerticalAlignment="Center" Margin="0,0,8,0"/>
                            <TextBlock x:Name="TxtUsbStatus" Text="WigiDash Detached" FontSize="12" FontWeight="SemiBold" Foreground="{DynamicResource M3Primary}" VerticalAlignment="Center"/>
                        </StackPanel>
                    </Border>
```
The pill now matches the 🎨 Theme button (`BgCard`/`BorderBrush`, CornerRadius 8, Padding 12,6). The dot's initial `Fill` is `DangerBorder` (red) matching the default "WigiDash Detached" label. `TxtUsbStatus` keeps its `x:Name`, text values, font, and amber color.

- [ ] **Step 2: Build to verify**

Run: `dotnet build ModernWigiDash.App\ModernWigiDash.App.csproj -nologo`
Expected: 0 errors.

- [ ] **Step 3: Manual visual check**

Launch the app; confirm the badge is the neutral pill (dark `BgCard`, subtle border) with amber text and a red dot while showing "WigiDash Detached".

- [ ] **Step 4: Commit**

```bash
git add ModernWigiDash.App/MainWindow.xaml
git commit -m "feat(app): restyle USB badge as neutral pill with status dot"
```

---

### Task 6: Dot-only badge logic + final regression

**Files:**
- Modify: `ModernWigiDash.App/MainWindow.xaml.cs:2133-2143`

**Interfaces:**
- Consumes: `UsbStatusDot` (Task 5), `TxtUsbStatus` (existing), `_usbDevice.IsHardwareActive` (existing), brushes `AccentGreen`/`DangerBorder` (exist).
- Produces: final behavior — pill stays constant; dot turns `AccentGreen` when attached and `DangerBorder` when detached.

- [ ] **Step 1: Update UpdateUsbBadge**

Replace the method body (lines 2133-2143) with:
```csharp
    private void UpdateUsbBadge()
    {
        string text = _usbDevice.IsHardwareActive ? "WigiDash Attached" : "WigiDash Detached";
        if (TxtUsbStatus.Text != text)
        {
            TxtUsbStatus.Text = text;
            var resources = Application.Current.Resources;
            UsbStatusDot.Fill = (Brush)resources[_usbDevice.IsHardwareActive ? "AccentGreen" : "DangerBorder"];
        }
    }
```
The `UsbBadgeBorder.Background`/`BorderBrush` swaps are gone — the pill is static and no longer reads `SuccessBackground`/`SuccessBorder` or `DangerBackground`/`DangerBorder`. `UpdateUsbBadge` is still invoked from the same attach/detach lifecycle events as before.

- [ ] **Step 2: Build the whole solution**

Run: `dotnet build ModernWigiDash.slnx -nologo`
Expected: 0 errors, no new warnings (4 pre-existing unrelated warnings are acceptable).

- [ ] **Step 3: Run the unit test suite (regression)**

Run: `dotnet test ModernWigiDash.Tests\ModernWigiDash.Tests.csproj -nologo`
Expected: all tests pass (this plan changes no tested logic; suite is a regression gate).

- [ ] **Step 4: Manual acceptance**

With the service running (`.\ModernWigiDash.Service\bin\Debug\net10.0-windows10.0.19041.0\ModernWigiDash.Service.exe -test`) and the app launched, verify:
- Badge is the neutral pill with amber text; **red dot** while "WigiDash Detached".
- After attaching the WigiDash, badge shows "WigiDash Attached" with a **green dot**; pill, border, and amber text unchanged.
- After detaching, dot returns to red.
- Header logo 38×38 top-aligned; title bar/taskbar/dialog icons show the logo; `ModernWigiDash.App.exe` shows the logo in Explorer.

- [ ] **Step 5: Commit**

```bash
git add ModernWigiDash.App/MainWindow.xaml.cs
git commit -m "feat(app): switch USB badge to status-dot-only updates"
```

---

## Self-Review Notes

- **Spec coverage:** header logo (T3) ✓; window+taskbar icon (T4) ✓; exe/Explorer icon (T2) ✓; dialog title bars (T4, via shared helper covering all four dialogs) ✓; pre-converted PNG+ICO pipeline (T1, T2) ✓; 38px top-aligned (T3) ✓; neutral pill + dot colors (T5, T6) ✓; `SuccessBackground`/`SuccessBorder` kept and badge no longer consumes them (T6) ✓; verification incl. unit suite + manual checklist (T6) ✓.
- **Placeholders:** every code step contains full code/commands; no "TBD/implement later".
- **Type consistency:** `UsbStatusDot`/`UsbBadgeBorder`/`TxtUsbStatus` names match between T5 XAML and T6 code; `ApplyDarkTitleBarToWindow` signature unchanged; `Resources/Logo/...` paths identical across tasks; ICO sizes verified against the ICO header count in T1.
