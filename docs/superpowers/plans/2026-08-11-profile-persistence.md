# Profile Persistence Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Persist the user's profile automatically — widget placements, page structure, and property values survive an app restart — via a debounced auto-save to `%LocalAppData%\ModernWigiDash\profile.json` plus a final save on close.

**Architecture:** A new `ProfilePersistence` module in the App project owns the path, load, atomic save, and debounce. MainWindow loads through it at startup (falling back to `StarterProfile.Create()` and saving once), marks dirty at every mutation funnel (`PersistProperty`, page/widget structural seams, drag/resize commits), and flushes on close. The module reuses the existing `ProfileOps.ExportJson`/`ImportJson`/sanitizer machinery unchanged — the auto-saved file is exactly what a manual export produces.

**Tech Stack:** .NET 10, C# 14, MSTest, SkiaSharp (unaffected), WPF (window wiring only). No new packages — `Microsoft.Extensions.Time.Testing` (`FakeTimeProvider`) is already referenced by the test project.

## Global Constraints

- Test framework is **MSTest**: `[TestClass]` / `[TestMethod]`, AAA pattern, test naming `MethodName_Scenario_ExpectedResult` (`.opencode/rules/dotnet-rules.md`).
- One type per file, file-scoped namespaces, `sealed` where inheritance is not designed for, `internal` test seams like the rest of the App modules.
- Do NOT modify `ProfileOps` (Core) or the export/import schema — reuse `ExportJson`, `ImportJson`, `IsImportFileTooLarge`, `ReplaceProfile` exactly as they are.
- File path: `Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModernWigiDash", "profile.json")`.
- Debounce delay: **2 seconds** (single constant in the module).
- Save failure is logged via the injected `Action<string>` sink, never thrown.
- The 30 FPS render loop must never touch the persistence module (no per-frame cost).
- Test command (fast iteration, filtered): `dotnet test ModernWigiDash.slnx -c Release --nologo --filter "ProfilePersistenceTests" -p:BaseOutputPath=C:\Users\tobia\AppData\Local\Temp\opencode\wmd-build\ -nodeReuse:false`
- Full suite before commit: `dotnet test ModernWigiDash.slnx -c Release --nologo -p:BaseOutputPath=C:\Users\tobia\AppData\Local\Temp\opencode\wmd-build\ -nodeReuse:false`

---

### Task 1: `ProfilePersistence` module — path, load, atomic save

The module's core surface: the LocalAppData path, `Load` (sanitized via the existing import pipeline), and `Save` (atomic tmp+replace). Debounce comes in Task 2 — this task builds and tests the load/save machinery it will ride on.

**Files:**
- Create: `ModernWigiDash.App/ProfilePersistence.cs`
- Create: `ModernWigiDash.Tests/ProfilePersistenceTests.cs`
- Modify: `ModernWigiDash.App/MainWindow.xaml.cs:16` (remove the dead `ProfilePath` constant — done in Task 3; not here)

**Interfaces:**
- Consumes: `ProfileOps.ExportJson(ProfileLayout)` → `string`; `ProfileOps.ImportJson(string, WidgetPluginLoader, IModernWigiDashContext)` → `ProfileLayout?`; `ProfileOps.IsImportFileTooLarge(long)` → `bool`; `ProfileLayout`, `WidgetPluginLoader`, `IModernWigiDashContext` (all existing, unchanged).
- Produces: `ProfilePersistence` with `static string DefaultProfilePath()`, `string ProfilePath`, `ProfileLayout? Load(WidgetPluginLoader, IModernWigiDashContext)`, `void Save()`, `void MarkDirty()`, `void Flush()`, `void Dispose()`. Ctor: `(string profilePath, Func<ProfileLayout> profileProvider, TimeSpan? debounceDelay = null, TimeProvider? timeProvider = null, Action<string>? log = null)`.

- [ ] **Step 1: Write the failing tests for path, load, and save**

Create `ModernWigiDash.Tests/ProfilePersistenceTests.cs`:

```csharp
using Microsoft.Extensions.Time.Testing;
using ModernWigiDash.App;
using ModernWigiDash.Core.Models;
using ModernWigiDash.Core.Plugins;
using ModernWigiDash.Sdk;
using ModernWigiDash.Widgets;

namespace ModernWigiDash.Tests;

[TestClass]
public sealed class ProfilePersistenceTests
{
    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "wmd-profile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static ProfileLayout SampleProfile()
        => new()
        {
            Pages = [new PageLayout { PageName = "P1" }]
        };

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met in time");
            await Task.Delay(10);
        }
    }

    [TestMethod]
    public void DefaultProfilePath_ReturnsLocalAppDataModernWigiDashProfileJson()
    {
        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ModernWigiDash", "profile.json");

        Assert.AreEqual(expected, ProfilePersistence.DefaultProfilePath());
    }

    [TestMethod]
    public void Load_AbsentFile_ReturnsNull()
    {
        var persistence = new ProfilePersistence(
            Path.Combine(NewTempDir(), "profile.json"),
            static () => SampleProfile());

        Assert.IsNull(persistence.Load(new WidgetPluginLoader(), new TestContext()));
    }

    [TestMethod]
    public void Load_ValidFile_ReturnsRehydratedProfile()
    {
        string path = Path.Combine(NewTempDir(), "profile.json");
        var loader = new WidgetPluginLoader();
        loader.RegisterBuiltInPlugin(typeof(TextLabelWidget));
        var context = new TestContext();

        var source = new ProfileLayout { Pages = [new PageLayout { PageName = "P1" }] };
        ProfileOps.PlaceWidget(source, loader, context, "text_label", 0, 0);
        File.WriteAllText(path, ProfileOps.ExportJson(source));

        var persistence = new ProfilePersistence(path, static () => SampleProfile());
        var loaded = persistence.Load(loader, context);

        Assert.IsNotNull(loaded);
        Assert.AreEqual(1, loaded.Pages.Count);
        Assert.AreEqual("P1", loaded.Pages[0].PageName);
        Assert.AreEqual(1, loaded.Pages[0].Widgets.Count);
        Assert.IsNotNull(loaded.Pages[0].Widgets[0].ActiveInstance);
    }

    [TestMethod]
    public void Load_CorruptJson_ReturnsNull()
    {
        string path = Path.Combine(NewTempDir(), "profile.json");
        File.WriteAllText(path, "{ not valid json");

        var persistence = new ProfilePersistence(path, static () => SampleProfile());

        Assert.IsNull(persistence.Load(new WidgetPluginLoader(), new TestContext()));
    }

    [TestMethod]
    public void Load_OversizedFile_ReturnsNull()
    {
        string path = Path.Combine(NewTempDir(), "profile.json");
        File.WriteAllText(path, new string('x', 10 * 1024 * 1024 + 1));

        var persistence = new ProfilePersistence(path, static () => SampleProfile());

        Assert.IsNull(persistence.Load(new WidgetPluginLoader(), new TestContext()));
    }

    [TestMethod]
    public void Save_WritesExportJsonAtomically_NoTempFileLeft()
    {
        string dir = NewTempDir();
        string path = Path.Combine(dir, "profile.json");
        var persistence = new ProfilePersistence(path, static () => SampleProfile());

        persistence.Save();

        Assert.IsTrue(File.Exists(path));
        Assert.IsFalse(File.Exists(path + ".tmp"));
        var loaded = System.Text.Json.JsonSerializer.Deserialize<ProfileLayout>(File.ReadAllText(path));
        Assert.IsNotNull(loaded);
        Assert.AreEqual(1, loaded.Pages.Count);
    }

    [TestMethod]
    public void Save_CreatesDirectory_WhenMissing()
    {
        string dir = Path.Combine(NewTempDir(), "nested", "dir");
        string path = Path.Combine(dir, "profile.json");
        var persistence = new ProfilePersistence(path, static () => SampleProfile());

        persistence.Save();

        Assert.IsTrue(File.Exists(path));
    }

    [TestMethod]
    public void Save_WriteFailure_IsLogged_NotThrown()
    {
        string dir = NewTempDir();
        // The path is a directory, so the tmp write throws IOException.
        string blocked = Path.Combine(dir, "blocked");
        Directory.CreateDirectory(blocked);
        var log = new List<string>();
        var failing = new ProfilePersistence(blocked, static () => SampleProfile(), log: log.Add);

        failing.Save();

        Assert.AreEqual(1, log.Count);
        StringAssert.Contains(log[0], "Profile save failed");
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test ModernWigiDash.slnx -c Release --nologo --filter "ProfilePersistenceTests" -p:BaseOutputPath=C:\Users\tobia\AppData\Local\Temp\opencode\wmd-build\ -nodeReuse:false`
Expected: build fails — `ProfilePersistence` does not exist.

- [ ] **Step 3: Write the implementation**

Create `ModernWigiDash.App/ProfilePersistence.cs`:

```csharp
using ModernWigiDash.Core.Models;
using ModernWigiDash.Core.Plugins;
using ModernWigiDash.Sdk;

namespace ModernWigiDash.App;

/// <summary>
/// Owns the persisted profile file: the LocalAppData path, sanitized load via
/// the existing import pipeline, atomic tmp+replace save, and the debounced
/// MarkDirty/Flush policy. The 30 FPS render loop never touches this module —
/// only user mutations arm the debounce.
/// </summary>
public sealed class ProfilePersistence : IDisposable
{
    public const string DirectoryName = "ModernWigiDash";
    public const string FileName = "profile.json";
    public static readonly TimeSpan DebounceDelay = TimeSpan.FromSeconds(2);

    private readonly string _profilePath;
    private readonly Func<ProfileLayout> _profileProvider;
    private readonly TimeSpan _debounceDelay;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string>? _log;
    private CancellationTokenSource? _debounceCts;
    private int _dirtyVersion;
    private bool _disposed;

    /// <param name="profileProvider">Reads the current profile at save time —
    /// the window's profile reference can be swapped on import, so the module
    /// must never hold a stale reference.</param>
    public ProfilePersistence(
        string profilePath,
        Func<ProfileLayout> profileProvider,
        TimeSpan? debounceDelay = null,
        TimeProvider? timeProvider = null,
        Action<string>? log = null)
    {
        _profilePath = profilePath;
        _profileProvider = profileProvider;
        _debounceDelay = debounceDelay ?? DebounceDelay;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _log = log;
    }

    public static string DefaultProfilePath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            DirectoryName, FileName);

    public string ProfilePath => _profilePath;

    /// <summary>
    /// Reads and sanitizes the persisted profile through the existing import
    /// pipeline (caps + rehydration). Returns null when absent, oversized,
    /// corrupt, or unparseable — the caller falls back to the starter profile.
    /// </summary>
    public ProfileLayout? Load(WidgetPluginLoader loader, IModernWigiDashContext context)
    {
        try
        {
            if (!File.Exists(_profilePath)) return null;
            if (ProfileOps.IsImportFileTooLarge(new FileInfo(_profilePath).Length))
            {
                _log?.Invoke($"Profile file too large ({new FileInfo(_profilePath).Length} bytes); ignoring");
                return null;
            }
            return ProfileOps.ImportJson(File.ReadAllText(_profilePath), loader, context);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Profile load failed, falling back to starter: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Synchronous save: export → temp file → atomic replace, so a crash
    /// never leaves a torn profile.json. Failures are logged, never thrown.
    /// </summary>
    public void Save()
    {
        try
        {
            string dir = Path.GetDirectoryName(_profilePath)!;
            Directory.CreateDirectory(dir);
            string json = ProfileOps.ExportJson(_profileProvider());
            string tmp = _profilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _profilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Profile save failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Arms the debounce: the save fires DebounceDelay after the LAST call —
    /// repeated mutations within the window coalesce into one write.
    /// </summary>
    public void MarkDirty()
    {
        if (_disposed) return;
        _debounceCts?.Cancel();
        _debounceCts = new CancellationTokenSource();
        var cts = _debounceCts;
        int version = ++_dirtyVersion;
        _ = DebounceSaveAsync(version, cts.Token);
    }

    /// <summary>Saves immediately and cancels any pending debounce.</summary>
    public void Flush()
    {
        _debounceCts?.Cancel();
        Save();
    }

    public void Dispose()
    {
        _disposed = true;
        _debounceCts?.Cancel();
    }

    private async Task DebounceSaveAsync(int version, CancellationToken token)
    {
        try
        {
            await Task.Delay(_debounceDelay, _timeProvider, token);
            if (version != Volatile.Read(ref _dirtyVersion)) return;
            Save();
        }
        catch (OperationCanceledException)
        {
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test ModernWigiDash.slnx -c Release --nologo --filter "ProfilePersistenceTests" -p:BaseOutputPath=C:\Users\tobia\AppData\Local\Temp\opencode\wmd-build\ -nodeReuse:false`
Expected: all 8 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add ModernWigiDash.App/ProfilePersistence.cs ModernWigiDash.Tests/ProfilePersistenceTests.cs
git commit -m "feat(profile): add ProfilePersistence module with sanitized load and atomic save"
```

---

### Task 2: Debounce coalescing, flush, and dispose semantics

Task 1 left `MarkDirty`/`Flush`/`Dispose` implemented but untested. This task pins the debounce policy: coalescing within the window, no save before the delay, flush-on-demand, and no save after dispose.

**Files:**
- Modify: `ModernWigiDash.Tests/ProfilePersistenceTests.cs` (add tests)
- (No implementation changes unless a test exposes one)

**Interfaces:**
- Consumes: `ProfilePersistence.MarkDirty()`, `Flush()`, `Dispose()` from Task 1.
- Produces: nothing new — pins Task 1's policy surface.

- [ ] **Step 1: Write the failing tests**

Append to `ProfilePersistenceTests.cs`:

```csharp
    [TestMethod]
    public async Task MarkDirty_NoSaveBeforeDebounce_CoalescesRepeatedMutations()
    {
        string path = Path.Combine(NewTempDir(), "profile.json");
        var time = new FakeTimeProvider();
        int saves = 0;
        var persistence = new ProfilePersistence(
            path,
            () => { saves++; return SampleProfile(); },
            debounceDelay: TimeSpan.FromSeconds(2),
            timeProvider: time);

        persistence.MarkDirty();
        persistence.MarkDirty();
        persistence.MarkDirty();
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.AreEqual(0, saves, "no save before the debounce elapses");
        Assert.IsFalse(File.Exists(path));

        time.Advance(TimeSpan.FromSeconds(1));
        await WaitUntil(() => saves == 1, timeoutMs: 2000);

        Assert.AreEqual(1, saves, "three mutations within the window coalesce to one save");
        Assert.IsTrue(File.Exists(path));
    }

    [TestMethod]
    public async Task MarkDirty_SecondMutationAfterSave_ArmsAnotherSave()
    {
        string path = Path.Combine(NewTempDir(), "profile.json");
        var time = new FakeTimeProvider();
        int saves = 0;
        var persistence = new ProfilePersistence(
            path,
            () => { saves++; return SampleProfile(); },
            debounceDelay: TimeSpan.FromSeconds(2),
            timeProvider: time);

        persistence.MarkDirty();
        time.Advance(TimeSpan.FromSeconds(2));
        await WaitUntil(() => saves == 1, timeoutMs: 2000);

        persistence.MarkDirty();
        time.Advance(TimeSpan.FromSeconds(2));
        await WaitUntil(() => saves == 2, timeoutMs: 2000);

        Assert.AreEqual(2, saves);
    }

    [TestMethod]
    public void Flush_SavesImmediately_EvenWithinDebounceWindow()
    {
        string path = Path.Combine(NewTempDir(), "profile.json");
        var time = new FakeTimeProvider();
        int saves = 0;
        var persistence = new ProfilePersistence(
            path,
            () => { saves++; return SampleProfile(); },
            debounceDelay: TimeSpan.FromSeconds(2),
            timeProvider: time);

        persistence.MarkDirty();
        persistence.Flush();

        Assert.AreEqual(1, saves);
        Assert.IsTrue(File.Exists(path));

        // The pending debounce must not fire a second save.
        time.Advance(TimeSpan.FromSeconds(3));
        Assert.AreEqual(1, saves);
    }

    [TestMethod]
    public async Task MarkDirty_AfterDispose_DoesNotSave()
    {
        string path = Path.Combine(NewTempDir(), "profile.json");
        var time = new FakeTimeProvider();
        int saves = 0;
        var persistence = new ProfilePersistence(
            path,
            () => { saves++; return SampleProfile(); },
            debounceDelay: TimeSpan.FromSeconds(2),
            timeProvider: time);

        persistence.Dispose();
        persistence.MarkDirty();
        time.Advance(TimeSpan.FromSeconds(3));

        Assert.AreEqual(0, saves);
        Assert.IsFalse(File.Exists(path));
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test ModernWigiDash.slnx -c Release --nologo --filter "ProfilePersistenceTests" -p:BaseOutputPath=C:\Users\tobia\AppData\Local\Temp\opencode\wmd-build\ -nodeReuse:false`
Expected: `Flush_SavesImmediately...` and `MarkDirty_AfterDispose...` PASS (implementation exists from Task 1); the two debounce tests FAIL or hang — the `Task.Delay(_debounceDelay, _timeProvider, token)` with `FakeTimeProvider` needs the fake clock advanced, which the tests do — if they still fail, verify `FakeTimeProvider` is resolving `Task.Delay(TimeSpan, TimeProvider)` (it does in .NET 8+; the repo already uses it in clock-dependent tests).

- [ ] **Step 3: Fix the implementation if a test exposes a gap**

The `MarkDirty` version-check in `DebounceSaveAsync` may be unnecessary in the happy path but must be verified: after `Flush` cancels the CTS, an in-flight `DebounceSaveAsync` is cancelled (no double-save). After `Dispose`, `MarkDirty` returns early. If `Flush_SavesImmediately` shows a second save after advancing the clock, the fix is the version guard — it already exists. No code change expected.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test ModernWigiDash.slnx -c Release --nologo --filter "ProfilePersistenceTests" -p:BaseOutputPath=C:\Users\tobia\AppData\Local\Temp\opencode\wmd-build\ -nodeReuse:false`
Expected: all 12 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add ModernWigiDash.Tests/ProfilePersistenceTests.cs
git commit -m "test(profile): pin debounce coalescing, flush, and dispose semantics"
```

---

### Task 3: MainWindow startup wiring — load or starter, first-launch save

The window loads the persisted profile at startup through `ProfilePersistence`, falling back to `StarterProfile.Create()` and saving it once. Also removes the dead `ProfilePath` constant.

**Files:**
- Modify: `ModernWigiDash.App/MainWindow.xaml.cs` — field + ctor wiring (~line 16, 74-207), remove the dead `ProfilePath` constant (line 16)

**Interfaces:**
- Consumes: `ProfilePersistence.Load(WidgetPluginLoader, IModernWigiDashContext)` → `ProfileLayout?`; `ProfilePersistence.Save()`; `ProfilePersistence.DefaultProfilePath()`; `ProfilePersistence.ProfilePath` (Task 1).
- Produces: the window's `_profile` is the loaded profile (or the starter fallback), and `_profilePersistence` is available to Task 4's dirty hooks. `_pageTabs.Rebuild(_profile)` and the badge/selection refresh run against the loaded profile.

- [ ] **Step 1: Remove the dead constant**

The plan's design review found `private static readonly string ProfilePath = Path.Combine(LogDirectory, "logs/profile.json");` at line 16. **Verify first**: it may already be gone (an earlier dead-code cleanup removed it; if `Select-String -Pattern "ProfilePath" MainWindow.xaml.cs` finds nothing, skip this step).

- [ ] **Step 2: Add the field and constructor wiring**

Add the field with the other module fields (near `_pageTabs`). The `_profile` field in this file is **non-nullable** (`private ProfileLayout _profile;`) — do NOT annotate it nullable (that cascades ~10 CS8602s at existing dereference sites). No initializer needed (the ctor assigns it):

```csharp
    private ProfilePersistence _profilePersistence;
```

In the ctor, before the "4. Setup Default Profile Layout" block (before the `StarterProfile` construction at lines 163-166), add:

```csharp
        // Profile persistence: load the saved profile at startup, falling back
        // to the starter profile when absent/corrupt. The provider lambda only
        // dereferences _profile at save time (import swaps the reference).
        _profilePersistence = new ProfilePersistence(
            ProfilePersistence.DefaultProfilePath(),
            () => _profile,
            log: msg => FileLog.Write($"[PROFILE] {msg}"));
```

Then replace the starter block (lines 163-166). **Load through a local** — assigning `Load`'s `ProfileLayout?` result straight to the non-nullable field warns CS8601; the local + `is null` fallback keeps the semantics warning-free:

```csharp
        // 4. Load the persisted profile, or build the starter profile on first
        //    launch. A first launch persists the starter immediately so the
        //    file exists before any mutation.
        var loaded = _profilePersistence.Load(_loader, this);
        if (loaded is null)
        {
            loaded = new StarterProfile(_loader, this).Create();
            _profilePersistence.Save();
        }
        _profile = loaded;
        _pageTabs.Rebuild(_profile);
```

- [ ] **Step 3: Build**

Run: `dotnet build ModernWigiDash.slnx -c Release --nologo`
Expected: succeeds — the window compiles with the new wiring; the `FileLog`/`StarterProfile` references resolve.

- [ ] **Step 4: Run the existing window-level tests**

Run: `dotnet test ModernWigiDash.slnx -c Release --nologo -p:BaseOutputPath=C:\Users\tobia\AppData\Local\Temp\opencode\wmd-build\ -nodeReuse:false`
Expected: full suite green (no behavior change yet — dirty hooks arrive in Task 4; startup still produces a renderable profile either way).

- [ ] **Step 5: Commit**

```bash
git add ModernWigiDash.App/MainWindow.xaml.cs
git commit -m "feat(profile): load persisted profile at startup with starter fallback"
```

---

### Task 4: Dirty hooks + flush on close

Every mutation funnel marks the profile dirty; the `Closed` handler flushes synchronously before teardown.

**Files:**
- Modify: `ModernWigiDash.App/MainWindow.Context.cs:43-51` (`PersistProperty`)
- Modify: `ModernWigiDash.App/MainWindow.xaml.cs` — `BtnPlaceWidget_Click`, `DeleteSelectedWidget`, `RenamePage`, `DeletePage`, `BtnAddPage_Click`, `BtnClear_Click`, `BtnImport_Click`, `ChkSnapToGrid_Changed`, `SkiaCanvas_MouseMove`, `SkiaCanvas_MouseUp`, `Closed`

**Interfaces:**
- Consumes: `ProfilePersistence.MarkDirty()` / `Flush()` (Tasks 1-2), `_profilePersistence` (Task 3).
- Produces: every mutation persists within the debounce window; close always flushes. No new public surface.

- [ ] **Step 1: Hook `PersistProperty` — the widget-property funnel**

In `ModernWigiDash.App/MainWindow.Context.cs`, inside `PersistProperty`, after `placed.PropertyValues[propertyName] = value;`:

```csharp
        if (ProfileOps.FindPlacedWidget(_profile, widget) is { } placed)
        {
            placed.PropertyValues[propertyName] = value;
            _profilePersistence.MarkDirty();
        }
```

This single seam covers every widget property write: inspector write-back, icon-grab moves, and widget `OnTouch` toggles (all route through `ModernWidgetBase.SetProperty` → context `PersistProperty`).

- [ ] **Step 2: Hook the page/widget structural seams**

In `MainWindow.xaml.cs`, add `_profilePersistence.MarkDirty();` at each site, right after the mutation (all single-line additions):

| Method | After this line | Why |
|---|---|---|
| `BtnPlaceWidget_Click` (line 423) | `RefreshSelection(placed);` | widget placed |
| `DeleteSelectedWidget` (line 387) | `RefreshSelection(null);` | widget removed |
| `RenamePage` (line 434) | `RefreshAfterMutation(_selectedWidget);` | page renamed |
| `DeletePage` (line 452) | `RefreshAfterMutation(null);` | page deleted |
| `BtnAddPage_Click` (line 463) | `RefreshAfterMutation(null);` | page added |
| `BtnClear_Click` (line 540) | `RefreshSelection(null);` | page cleared |
| `BtnImport_Click` (line 501) | `RefreshAfterMutation(null);` | profile replaced |
| `ChkSnapToGrid_Changed` (line 469) | `SkiaCanvas.InvalidateVisual();` (only when `_wired`) | SnapToGrid is serialized |

For `ChkSnapToGrid_Changed`, place the call inside the existing `if (!_wired) return;` guard's body:

```csharp
    private void ChkSnapToGrid_Changed(object sender, RoutedEventArgs e)
    {
        if (!_wired) return;
        _profile.ActivePage.SnapToGrid = ChkSnapToGrid.IsChecked == true;
        _profilePersistence.MarkDirty();
        SkiaCanvas.InvalidateVisual();
    }
```

- [ ] **Step 3: Hook drag/resize and icon-grab commits**

In `SkiaCanvas_MouseMove`, inside the `if (... && changed)` block (line 330-334):

```csharp
        if (_inputController.Move((float)pos.X, (float)pos.Y, Input.InputSource.DesktopEdit, _compositor.IsEditMode, out bool changed) && changed)
        {
            _profilePersistence.MarkDirty();
            _inspector.RefreshTransforms();
            SkiaCanvas.InvalidateVisual();
        }
```

In `SkiaCanvas_MouseUp`, after the `if (iconMoved) _inspector.Refresh();` block, add a commit hook for drag/resize (position/size mutations write directly to the placed instance, bypassing `SetProperty`):

```csharp
        if (wasManipulating)
        {
            _profilePersistence.MarkDirty();
        }
```

Do NOT hook the device-touch path (lines 173-190): it always passes `editMode: false`, so manipulations never start there — no dirty-marking needed. Do NOT hook `SwitchToPage`: page navigation changes the serialized `ActivePageIndex`, but the close-time `Flush` already captures it, and swiping must not churn disk writes.

- [ ] **Step 4: Flush on close**

In the `Closed` handler (line 210-231), as the FIRST statement inside the `try` block (before `_framePump.Dispose()`):

```csharp
            try
            {
                // Persist before teardown: a clean exit always lands the final
                // profile state (including the last active page index).
                _profilePersistence.Flush();
                _framePump.Dispose();
                ...
```

- [ ] **Step 5: Build and run the full suite**

Run: `dotnet build ModernWigiDash.slnx -c Release --nologo` then
`dotnet test ModernWigiDash.slnx -c Release --nologo -p:BaseOutputPath=C:\Users\tobia\AppData\Local\Temp\opencode\wmd-build\ -nodeReuse:false`
Expected: build green; full suite green (the hooks are one-line window edits; `ProfilePersistence` behavior is pinned by Tasks 1-2).

- [ ] **Step 6: Commit**

```bash
git add ModernWigiDash.App/MainWindow.xaml.cs ModernWigiDash.App/MainWindow.Context.cs
git commit -m "feat(profile): mark dirty on every mutation and flush on close"
```

---

### Task 5: Manual end-to-end verification

**Files:** none (verification only)

- [ ] **Step 1: Run the app and verify persistence**

1. Launch `ModernWigiDash.App.exe` (from a Release build or the release zip).
2. Rearrange widgets, add a page, change a widget property via the inspector, rename a page.
3. Wait ~3 s (debounce) — verify `%LocalAppData%\ModernWigiDash\profile.json` exists and parses as `ExportJson`-shaped JSON.
4. Close the app. Relaunch. Verify: the layout, pages, and property values are restored; the last active page is restored.
5. Delete `%LocalAppData%\ModernWigiDash\profile.json`, relaunch. Verify: starter profile appears, and the file is recreated immediately (first-launch save).

- [ ] **Step 2: Corrupt-file recovery**

1. Write garbage into `%LocalAppData%\ModernWigiDash\profile.json` (`{ not json`).
2. Relaunch. Verify: starter profile appears (no crash), and the corrupt file is replaced by a valid one on the next save.

- [ ] **Step 3: Crash-window check**

1. Make a change, then kill the process (Task Manager) within the 2 s debounce window.
2. Relaunch. Verify: the last *completed* save is restored (≤2 s of changes lost — the accepted debounce trade-off).

## Self-Review Notes

- **Spec coverage**: Decision 1 (module, path, load, save, dirty hooks, close flush, first-launch save, manual import/export kept) → Tasks 1-4. Decision 2 (scouting pass) is deliberately NOT in this plan — it is a separate measurement procedure with no code changes, split per the writing-plans scope check. Edge cases (corrupt/absent/oversized/locked file, crash window, import-while-pending) → Task 1 tests + Task 5 manual steps.
- **Placeholders**: none — every code step carries full source.
- **Type consistency**: `ProfilePersistence` ctor/`Load`/`Save`/`MarkDirty`/`Flush` signatures are identical across Tasks 1-4; the window uses `_profile!` (matching the existing `InputState` lambda pattern at `MainWindow.xaml.cs:125`).
