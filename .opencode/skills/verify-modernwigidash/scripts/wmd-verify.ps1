#Requires -Version 5.1
<#
wmd-verify.ps1 - UIA verification harness for the ModernWigiDash WPF app.
Drives through UIA v4 core (the UIAutomationCore COM API) - NOT the legacy
System.Windows.Automation v3 managed client (the .NET 3.0-era WPF API): v3
misbehaves against .NET WPF providers and was the root cause of this
harness's drive failures; the v4 core reads the same tree correctly.
No elevation; drives the app in Simulated connection mode (physical-display
behavior routes to the hardware-e2e-validation skill instead).

Usage (repo root derived from this script's location, so it runs from anywhere):

  powershell .opencode\skills\verify-modernwigidash\scripts\wmd-verify.ps1 <command> [args]

Commands:
  launch                            Start the newest Release build (unelevated), wait for the window, record state.
  doctor [-Window <namePart>]       Check window/process health; exit 0 only when THIS instance is drivable.
  dump [-Path <out>]                Dump the UIA tree (depth-capped) to a file or stdout.
find <needle>                     List controls whose Name or AutomationId contains <needle> (case-insensitive).
   list <needle> [buttons]           Read-only: numbered matches in tree order (document / left-to-right) with
                                     control type + screen position. With `buttons`, non-buttons are skipped.
                                     Run before click-nth to prove which #N is which.
   click <needle>                    Click the first matching control (Invoke pattern; mouse fallback only when
                                     the control has no Invoke, and the fallback verifies cursor placement and
                                     refuses when the synthetic mouse cannot move - e.g. headless agent sessions).
   click-nth <needle> <n>            Click the Nth BUTTON match (1-based, tree order) via the same rules.
                                     Disambiguates repeated glyph buttons (e.g. the per-tab close X).
value <needle>                    Print the Value or Name of the first matching control.
   set <needle> <value>              Set ValuePattern text on the first matching control.
   set-in <windowTitle> <value>      Set the ValuePattern text of the FIRST writable text control inside the window
                                     whose title contains <windowTitle> (read-back is printed). For dialogs whose
                                     input carries no UIA Name - the themed prompts (e.g. the `Rename Page` window).
                                     Refuses the main app window (its boxes are addressed by name with set).
  click-at <needle> <x> <y>         Click x,y client-pixels inside the first matching control (canvas pointing).
   click-screen <x> <y>              Click absolute screen coordinates. For Skia-drawn surfaces that expose no
                                      UIA peer at all (the preview canvas is invisible to UIA by design):
                                      compute the point from the known 1:1 preview geometry. Same hard
                                      cursor-placement gate + activation dance as click-at.
  shot <path>                       Screenshot the main app window to <path> (PNG).
  wait <namePart> [-Seconds <n>]    Wait up to n seconds (default 15) for a window whose title contains <namePart>.
  backup-profile                    Back up profile.json + app_theme.json from %LOCALAPPDATA%\ModernWigiDash
                                    (theme files included when present).
  restore-profile                   Restore the backed-up files (no-op when nothing was backed up).
  stop                              Kill the process launched by launch (only that recorded pid, never by name).
  clean                             stop + restore-profile + drop the state file (evidence is never touched).

State: %TEMP%\opencode\wmd-verify.state.json  {pid, exe, startedUtc, profileBackup}

Rules this script enforces:
  - launch refuses to start when another ModernWigiDash.App.exe is already
    running (shared-instance rule: verification drives its own instance).
  - stop/clean kill only the pid recorded at launch.
   - find/list/click/click-nth share one natural tree-order walk (left-to-right
     within each container; the page strip's per-tab buttons read in tab order).
Exit codes: 0 ok, 1 fail (message on stderr).
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0, Mandatory)][string]$Command,
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$Rest
)

$ErrorActionPreference = 'Stop'
$script:UiaReady = $false

function Fail([string]$msg) {
    [Console]::Error.WriteLine("wmd-verify: $msg")
    exit 1
}

function Read-State {
    $p = Join-Path $env:TEMP "opencode\wmd-verify.state.json"
    if (Test-Path $p) { return (Get-Content $p -Raw | ConvertFrom-Json) }
    return $null
}

function Write-State($obj) {
    $dir = Join-Path $env:TEMP "opencode"
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $obj | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $dir "wmd-verify.state.json")
}

function Repo-Root {
    (Resolve-Path (Join-Path $PSScriptRoot "..\..\..\..")).Path
}

function Find-Exe {
    $root = Repo-Root
    $cand = Get-ChildItem -Path (Join-Path $root "ModernWigiDash.App\bin\Release") -Recurse -Filter "ModernWigiDash.App.exe" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $cand) { Fail "no Release build found - run: dotnet build ModernWigiDash.slnx -c Release --nologo" }
    return $cand.FullName
}

# UIA v4 (UIAutomationCore) bridge. Why it is a hand-rolled typed-vtable
# ComImport interop (Add-Type compiled, driven through ordinary .NET calls)
# rather than New-Object -ComObject + PowerShell late binding - three
# distinct constraints, not one:
#
#  (1) Inherent to the v4 API (any Windows build, not fixable): v4 objects
#      are vtable-dispatched and expose NO IDispatch, so PowerShell can
#      never late-bind them and its COM type catalog (which wraps only
#      IDispatch/automation interfaces) generates no early-bound wrappers.
#      A typed-vtable ComImport interop is ALWAYS required to drive v4 from
#      PowerShell; the v3-era friendly .Name properties came from the
#      managed v3 wrapper QI'ing v4 under the hood.
#
#  (2) This Windows build's registration (the part a fuller build would
#      change): the v4 client coclass ("UIAutomation Client Central Class",
#      uiautomationcore.dll) is registered under a bare CLSID - no ProgID,
#      no type library - so New-Object -ComObject cannot instantiate it and
#      no generated interop assembly exists. This forces the CoCreateInstance
#      P/Invoke + hand-rolled CLSID and keeps the vtable slot layout
#      hand-pinned from the SDK header (Windows SDK 10.0.26100.0
#      UIAutomationClient.h). A ProgID + typelib would let you GENERATE the
#      interop instead of hand-writing it, but it would NOT remove the
#      typed-vtable bridge (point 1): a typelib does not make vtable-only
#      objects late-bindable.
#
#  (3) This build's runtime QI behavior: the CLR's RCW QI path rejects the
#      central-class objects (an RCW cannot be cast to a typed COM parameter
#      at bind time), so pattern objects are QI'd through the raw vtable
#      slot (RawQI), not a typed cast.
$script:WmdUiaSource = @'
using System;
using System.Runtime.InteropServices;

namespace WmdUia
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    // 16-byte VARIANT (only VT_BSTR is produced).
    [StructLayout(LayoutKind.Sequential)]
    public struct Variant
    {
        public ushort vt;
        public ushort r1;
        public ushort r2;
        public ushort r3;
        public IntPtr data;
    }

    [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IUIAutomationElement
    {
        [PreserveSig] int _setFocus();                                                   // vtable 3
        [PreserveSig] int _getRuntimeId();                                               // 4
        [PreserveSig] int FindFirst(int scope, object condition, out IUIAutomationElement found); // 5
        [PreserveSig] int _findAll();                                                    // 6
        [PreserveSig] int _findFirstBuildCache();                                        // 7
        [PreserveSig] int _findAllBuildCache();                                          // 8
        [PreserveSig] int _buildUpdatedCache();                                          // 9
        [PreserveSig] int _getCurrentPropertyValue();                                    // 10
        [PreserveSig] int _getCurrentPropertyValueEx();                                  // 11
        [PreserveSig] int _getCachedPropertyValue();                                     // 12
        [PreserveSig] int _getCachedPropertyValueEx();                                   // 13
        [PreserveSig] int _getCurrentPatternAs();                                        // 14
        [PreserveSig] int _getCachedPatternAs();                                         // 15
        [PreserveSig] int GetCurrentPattern(uint patternId, out IntPtr pattern);         // 16
        [PreserveSig] int _getCachedPattern();                                           // 17
        [PreserveSig] int _getCachedParent();                                            // 18
        [PreserveSig] int _getCachedChildren();                                          // 19
        [PreserveSig] int GetCurrentProcessId(out int value);                            // 20
        [PreserveSig] int GetCurrentControlType(out int value);                          // 21
        [PreserveSig] int _getCurrentLocalizedControlType();                             // 22
        [PreserveSig] int GetCurrentName([MarshalAs(UnmanagedType.BStr)] out string value); // 23
        [PreserveSig] int _getCurrentAcceleratorKey();                                   // 24
        [PreserveSig] int _getCurrentAccessKey();                                        // 25
        [PreserveSig] int _getCurrentHasKeyboardFocus();                                 // 26
        [PreserveSig] int _getCurrentIsKeyboardFocusable();                              // 27
        [PreserveSig] int GetCurrentIsEnabled([MarshalAs(UnmanagedType.Bool)] out bool value); // 28
        [PreserveSig] int GetCurrentAutomationId([MarshalAs(UnmanagedType.BStr)] out string value); // 29
        [PreserveSig] int _getCurrentClassName();                                        // 30
        [PreserveSig] int _getCurrentHelpText();                                         // 31
        [PreserveSig] int _getCurrentCulture();                                          // 32
        [PreserveSig] int _getCurrentIsControlElement();                                 // 33
        [PreserveSig] int _getCurrentIsContentElement();                                 // 34
        [PreserveSig] int _getCurrentIsPassword();                                       // 35
        [PreserveSig] int GetCurrentNativeWindowHandle(out long value);                  // 36
        [PreserveSig] int _getCurrentItemType();                                         // 37
        [PreserveSig] int _getCurrentIsOffscreen();                                      // 38
        [PreserveSig] int _getCurrentOrientation();                                      // 39
        [PreserveSig] int _getCurrentFrameworkId();                                      // 40
        [PreserveSig] int _getCurrentIsRequiredForForm();                                // 41
        [PreserveSig] int _getCurrentItemStatus();                                       // 42
        [PreserveSig] int GetCurrentBoundingRectangle(out Rect value);                   // 43
    }

    [ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IUIAutomation
    {
        [PreserveSig] int _compareElements();                                            // vtable 3
        [PreserveSig] int _compareRuntimeIds();                                           // 4
        [PreserveSig] int GetRootElement(out IUIAutomationElement root);                  // 5
        [PreserveSig] int _elementFromHandle();                                           // 6
        [PreserveSig] int _elementFromPoint();                                            // 7
        [PreserveSig] int _getFocusedElement();                                           // 8
        [PreserveSig] int _getRootElementBuildCache();                                    // 9
        [PreserveSig] int _elementFromHandleBuildCache();                                 // 10
        [PreserveSig] int _elementFromPointBuildCache();                                  // 11
        [PreserveSig] int _getFocusedElementBuildCache();                                 // 12
        [PreserveSig] int _createTreeWalker();                                            // 13
        [PreserveSig] int GetControlViewWalker(out IUIAutomationTreeWalker walker);       // 14
        [PreserveSig] int _getContentViewWalker();                                        // 15
        [PreserveSig] int _getRawViewWalker();                                            // 16
        [PreserveSig] int _getRawViewCondition();                                         // 17
        [PreserveSig] int _getControlViewCondition();                                     // 18
        [PreserveSig] int _getContentViewCondition();                                     // 19
        [PreserveSig] int _createCacheRequest();                                          // 20
        [PreserveSig] int _createTrueCondition();                                         // 21
        [PreserveSig] int _createFalseCondition();                                        // 22
        [PreserveSig] int CreatePropertyCondition(int propertyId, Variant value, out object condition); // 23
    }

    [ComImport, Guid("4042c624-389c-4afc-a630-9df854a541fc"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IUIAutomationTreeWalker
    {
        [PreserveSig] int _getParentElement();                                            // vtable 3
        [PreserveSig] int GetFirstChildElement(IUIAutomationElement element, out IUIAutomationElement first); // 4
        [PreserveSig] int _getLastChildElement();                                         // 5
        [PreserveSig] int GetNextSiblingElement(IUIAutomationElement element, out IUIAutomationElement next); // 6
    }

    [ComImport, Guid("fb377fbe-8ea6-46d5-9c73-6499642d3059"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IUIAutomationInvokePattern
    {
        [PreserveSig] int Invoke();                                                       // vtable 3
    }

    [ComImport, Guid("a94cd8b1-0844-4cd6-9d2d-640537ab39e9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IUIAutomationValuePattern
    {
        [PreserveSig] int SetValue([MarshalAs(UnmanagedType.BStr)] string value);         // vtable 3
        [PreserveSig] int GetCurrentValue([MarshalAs(UnmanagedType.BStr)] out string value); // 4
        [PreserveSig] int GetCurrentIsReadOnly([MarshalAs(UnmanagedType.Bool)] out bool value); // 5
    }

    public static class Core
    {
        private const uint CLSCTX_INPROC_SERVER = 1;

        // "UIAutomation Client Central Class" (CComUIAutomation), the v4
        // client coclass as registered on current Windows builds.
        private static readonly Guid CComUIAutomation = new Guid("FF48DBA4-60EF-4201-AA87-54103EEF594E");
        private static readonly Guid IuiaIid = new Guid("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE");
        private static readonly Guid ElementIid = new Guid("D22108AA-8AC5-49A5-837B-37BBB3D7591E");
        private static readonly Guid InvokePatternIid = new Guid("FB377FBE-8EA6-46D5-9C73-6499642D3059");
        private static readonly Guid ValuePatternIid = new Guid("A94CD8B1-0844-4CD6-9D2D-640537AB39E9");

        public const int InvokePatternId = 10000;    // UIA_InvokePatternId
        public const int ValuePatternId = 10002;     // UIA_ValuePatternId

        [DllImport("ole32.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint ctx, ref Guid iid, out IntPtr pptr);

        private delegate int RawQIDelegate(IntPtr self, ref Guid riid, out IntPtr ppv);

        private static IUIAutomation _uia;
        private static IUIAutomationTreeWalker _walker;

        public static void Init()
        {
            if (_uia != null) return;
            // CoCreateInstance takes the CLSID/IID by ref, and C# forbids
            // passing a static readonly field by ref - copy to locals first.
            Guid clsid = CComUIAutomation;
            Guid iid = IuiaIid;
            IntPtr pptr;
            int hr = CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_INPROC_SERVER, ref iid, out pptr);
            if (hr != 0) throw new Exception("UIA activation failed hr=0x" + hr.ToString("X8"));
            _uia = (IUIAutomation)Marshal.GetObjectForIUnknown(pptr);
            _uia.GetControlViewWalker(out _walker);
        }

        // Raw vtable QI: this build's central-class objects reject the CLR's
        // RCW QI path, so call the vtable slot directly and wrap the pointer.
        // Takes an IUnknown pointer (an element's own, or a pattern pointer from
        // GetCurrentPattern) and QIs it to <iid>.
        private static IntPtr RawQI(IntPtr obj, Guid iid)
        {
            IntPtr vt = Marshal.ReadIntPtr(obj);
            IntPtr fn = Marshal.ReadIntPtr(vt);
            RawQIDelegate q = (RawQIDelegate)Marshal.GetDelegateForFunctionPointer(fn, typeof(RawQIDelegate));
            IntPtr ppv;
            int hr = q(obj, ref iid, out ppv);
            if (hr != 0) throw new Exception("UIA QI failed hr=0x" + hr.ToString("X8"));
            return ppv;
        }

        // QI an element RCW to its typed interface. Element RCWs are produced by the
        // out-parameter marshaling above (typed IUIAutomationElement), but
        // PowerShell cannot cast a System.__ComObject to a typed COM parameter
        // at the method-binding step (this build rejects the CLR's QI path), so
        // every public surface takes/returns object and QIs internally through
        // the raw vtable slot.
        private static IUIAutomationElement AsElement(object com)
        {
            if (com == null) return null;
            return (IUIAutomationElement)Marshal.GetObjectForIUnknown(RawQI(Marshal.GetIUnknownForObject(com), ElementIid));
        }

        public static object RootElement()
        {
            Init();
            IUIAutomationElement root;
            _uia.GetRootElement(out root);
            return root;
        }

        // Find the first top-level window (a direct child of the desktop root) whose
        // Name contains <name> (case-insensitive). Walk-based instead of a UIA
        // PropertyCondition: this build's CreatePropertyCondition rejects the
        // hand-marshaled VARIANT, and the harness's own notes record that exact
        // Name conditions are brittle (e.g. an emoji-prefixed title).
        public static object FindRootChildByName(string name)
        {
            Init();
            IUIAutomationElement root = AsElement(RootElement());
            IUIAutomationElement child;
            _walker.GetFirstChildElement(root, out child);
            while (child != null)
            {
                string nm;
                child.GetCurrentName(out nm);
                if (nm != null && nm.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                    return child;
                _walker.GetNextSiblingElement(child, out child);
            }
            return null;
        }

        public static object FirstChild(object element)
        {
            Init();
            IUIAutomationElement el = AsElement(element);
            IUIAutomationElement child;
            _walker.GetFirstChildElement(el, out child);
            return child;
        }

        public static object NextSibling(object element)
        {
            Init();
            IUIAutomationElement el = AsElement(element);
            IUIAutomationElement sibling;
            _walker.GetNextSiblingElement(el, out sibling);
            return sibling;
        }

        public static string Name(object el)
        {
            string value;
            AsElement(el).GetCurrentName(out value);
            return value ?? "";
        }

        public static string AutomationId(object el)
        {
            string value;
            AsElement(el).GetCurrentAutomationId(out value);
            return value ?? "";
        }

        public static int ControlType(object el)
        {
            int value;
            AsElement(el).GetCurrentControlType(out value);
            return value;
        }

        public static int ProcessId(object el)
        {
            int value;
            AsElement(el).GetCurrentProcessId(out value);
            return value;
        }

        public static bool IsEnabled(object el)
        {
            bool value;
            AsElement(el).GetCurrentIsEnabled(out value);
            return value;
        }

        public static long NativeWindowHandle(object el)
        {
            long value;
            AsElement(el).GetCurrentNativeWindowHandle(out value);
            return value;
        }

        public static Rect BoundingRect(object el)
        {
            Rect value;
            AsElement(el).GetCurrentBoundingRectangle(out value);
            return value;
        }

        public static object InvokePattern(object el)
        {
            IUIAutomationElement e = AsElement(el);
            // GetCurrentPattern returns the pattern as a raw IUnknown pointer:
            // an `out object` would marshal as a VARIANT and throw
            // InvalidOleVariantTypeException on a real (non-null) pointer.
            IntPtr pattern;
            int hr = e.GetCurrentPattern(InvokePatternId, out pattern);
            if (hr != 0 || pattern == IntPtr.Zero) return null;
            return Marshal.GetObjectForIUnknown(RawQI(pattern, InvokePatternIid));
        }

        public static object ValuePattern(object el)
        {
            IUIAutomationElement e = AsElement(el);
            IntPtr pattern;
            int hr = e.GetCurrentPattern(ValuePatternId, out pattern);
            if (hr != 0 || pattern == IntPtr.Zero) return null;
            return Marshal.GetObjectForIUnknown(RawQI(pattern, ValuePatternIid));
        }

        public static void Invoke(object pattern)
        {
            ((IUIAutomationInvokePattern)pattern).Invoke();
        }

        public static string PatternValue(object pattern)
        {
            string value;
            ((IUIAutomationValuePattern)pattern).GetCurrentValue(out value);
            return value;
        }

        public static bool PatternReadOnly(object pattern)
        {
            bool value;
            ((IUIAutomationValuePattern)pattern).GetCurrentIsReadOnly(out value);
            return value;
        }

        public static void SetValue(object pattern, string value)
        {
            ((IUIAutomationValuePattern)pattern).SetValue(value);
        }
    }
}
'@

$script:ControlTypeWindow = 50032   # UIA_WindowControlTypeId
$script:ControlTypeEdit = 50004     # UIA_EditControlTypeId
$script:ControlTypeNames = @{
    50000 = 'Button'; 50001 = 'Calendar'; 50002 = 'CheckBox'; 50003 = 'ComboBox'; 50004 = 'Edit'
    50005 = 'Grid'; 50006 = 'Header'; 50007 = 'HeaderItem'; 50008 = 'Hyperlink'; 50009 = 'Image'
    50010 = 'List'; 50011 = 'ListItem'; 50012 = 'Menu'; 50013 = 'MenuBar'; 50014 = 'MenuItem'
    50015 = 'ProgressBar'; 50016 = 'RadioButton'; 50017 = 'ScrollBar'; 50018 = 'Slider'; 50019 = 'Spinner'
    50020 = 'Text'; 50021 = 'ToolBar'; 50022 = 'ToolTip'; 50023 = 'Tree'; 50024 = 'TreeItem'
    50025 = 'Custom'; 50026 = 'Group'; 50027 = 'Thumb'; 50028 = 'DataGrid'; 50029 = 'DataItem'
    50030 = 'Document'; 50031 = 'SplitButton'; 50032 = 'Window'; 50033 = 'Pane'; 50034 = 'Table'
    50035 = 'Tab'; 50036 = 'TabItem'; 50037 = 'TitleBar'; 50038 = 'Separator'
}

function Get-TypeName([int]$id) {
    if ($script:ControlTypeNames.ContainsKey($id)) { return $script:ControlTypeNames[$id] }
    return ('type' + $id)
}

function Init-Uia {
    if ($script:UiaReady) { return }
    if (-not ('WmdUia.Core' -as [type])) { Add-Type -TypeDefinition $script:WmdUiaSource }
    [WmdUia.Core]::Init()
    $script:UiaReady = $true
}

function Get-MainWindow {
    Init-Uia
    $s = Read-State
    $win = [WmdUia.Core]::FindRootChildByName("ModernWigiDash")
    if ($null -eq $win) { return $null }
    if ($s -and $s.pid -and [WmdUia.Core]::ProcessId($win) -ne [int]$s.pid) { return $null }
    return $win
}

function Get-AnyWindow([string]$namePart) {
    # Substring (case-insensitive) Name match over the pid-scoped app tree,
    # first Window-typed match (same walk as Get-DialogWindow). A UIA
    # PropertyCondition on Name is an EXACT match (it failed on "Theme
    # Customization" vs the actual Name "🎨 Theme Customization"), and owned
    # WPF dialogs live inside the OWNER's UIA subtree - not as desktop-root
    # children - so a root-children probe misses them entirely.
    Init-Uia
    foreach ($el in (Collect-Elements $namePart $false)) {
        # v4 control type is a plain int (UIA_WindowControlTypeId = 50032).
        if ([WmdUia.Core]::ControlType($el) -eq [int]$script:ControlTypeWindow) { return $el }
    }
    return $null
}

function Get-ChildLines($el, $depth, $depthMax, $budget) {
    if ($budget.Value -le 0 -or $depth -gt $depthMax) { return @() }
    $budget.Value--
    # v4: the bridge's Core exposes Current* as named methods; the bounding
    # rectangle is a Win32 RECT (left, top, right, bottom).
    $ct = Get-TypeName ([WmdUia.Core]::ControlType($el))
    $line = (('  ' * $depth) + ' [' + $ct + ']')
    $aid = [WmdUia.Core]::AutomationId($el)
    if ($aid) { $line += ' id=' + $aid }
    $nm = [WmdUia.Core]::Name($el)
    if ($nm) { $line += ' "' + $nm + '"' }
    $r = [WmdUia.Core]::BoundingRect($el)
    if (($r.Right - $r.Left) -gt 0) {
        $line += (' @(' + $r.Left + ',' + $r.Top + ') ' + ($r.Right - $r.Left) + 'x' + ($r.Bottom - $r.Top) + ')')
    }
    $out = @($line)
    $child = [WmdUia.Core]::FirstChild($el)
    while ($null -ne $child) {
        $out += Get-ChildLines $child ($depth + 1) $depthMax $budget
        $child = [WmdUia.Core]::NextSibling($child)
    }
    return $out
}

function Find-Element([string]$needle) {
    # The first match in natural document (tree) order, from the same single
    # walk as Collect-Elements (pid-scoped to the launched app when a state
    # file exists). So `click <needle>` is honest about which match it will
    # hit - and when several matches exist, `list` first is the discipline.
    $hits = Collect-Elements $needle $false
    if ($hits.Count -eq 0) { return $null }
    return $hits[0]
}

function Get-DialogWindow([string]$titlePart) {
    # A Window-typed control whose Name contains <titlePart>, from the same
    # pid-scoped single walk as Collect-Elements. Owned WPF dialogs hang off
    # the OWNER window's subtree in this UIA view (not under the desktop
    # root), so root-children-only search misses them - the walk must go the
    # full app tree and pick the first ControlType.Window match.
    Get-AnyWindow $titlePart
}

function Set-FirstWritableText($winEl, [string]$value) {
    # Writes <value> into the first control in the window (natural tree order)
    # that exposes a NON-READ-ONLY ValuePattern; returns the READ-BACK string
    # (the control's value after the write) or $null when no writable text
    # control exists. Returns the read-back instead of printing it so the
    # caller can both verify and display it - wrapping an output-producing
    # function in a condition subexpression (if (-not (Func ...))) would
    # capture that output and hide the proof.
    $stack = New-Object System.Collections.Stack
    Queue-Children $stack $winEl
    while ($stack.Count -gt 0) {
        $el = $stack.Pop()
        $vp = $null
        try { $vp = [WmdUia.Core]::ValuePattern($el) } catch { }
        # A writable ValuePattern alone is NOT a text input: WPF exposes the
        # window TitleBar (50037) as a control whose writable ValuePattern
        # carries the window's title, and it precedes the real Edit in tree
        # order. Gate on the Edit control type so we hit the actual input.
        if ($null -ne $vp -and [WmdUia.Core]::ControlType($el) -eq [int]$script:ControlTypeEdit) {
            try {
                if (-not [WmdUia.Core]::PatternReadOnly($vp)) {
                    [WmdUia.Core]::SetValue($vp, $value)
                    return [WmdUia.Core]::PatternValue($vp)
                }
            } catch { }
        }
        Queue-Children $stack $el
    }
    return $null
}

function Ensure-User32 {
    # ONE complete WmdUser32 definition (cursor, mouse_event, window rect,
    # and the window-activation dance members). shot and Do-ClickScreen used
    # to each Add-Type their own subset: when both commands ran in one
    # PowerShell process, whichever ran first won the -as [type] guard and
    # the other's click fell into MethodNotFound on the missing member.
    if (-not ('WmdVerify.WmdUser32' -as [type])) {
        Add-Type -MemberDefinition @"
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, uint r);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
[DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
[DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
[DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
[StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
"@ -Name WmdUser32 -Namespace WmdVerify
    }
}

function Do-ClickScreen([int]$x, [int]$y) {
    Ensure-User32
    if (-not ('WmdVerify.WmdMouse' -as [type])) {
        Add-Type -MemberDefinition @"
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
[StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
"@ -Name WmdMouse -Namespace WmdVerify
    }
    # HARD GATE: a synthetic click must land where it was aimed. In some session
    # contexts SetCursorPos silently fails (returns false) and mouse_event then
    # fires at the *physical* cursor position - which can click the wrong
    # control (observed: a stray click over AddPage created a page). Refuse
    # the click unless the cursor is verifiably at the target.
    $moved = [WmdVerify.WmdMouse]::SetCursorPos($x, $y)
    $pt = New-Object WmdVerify.WmdMouse+POINT
    [WmdVerify.WmdMouse]::GetCursorPos([ref]$pt) | Out-Null
if (-not $moved -or ([math]::Abs($pt.X - $x) -gt 2) -or ([math]::Abs($pt.Y - $y) -gt 2)) {
        Fail "synthetic mouse unavailable in this session (SetCursorPos->cursor=$($pt.X),$($pt.Y), wanted $x,$y). A mouse_event click would land at the physical cursor; refusing. Canvas click-at needs an interactive session; use click / click-nth (Invoke) for everything else."
    }
    Start-Sleep -Milliseconds 80
    # WPF swallows an ACTIVATING CLICK: when the window at the target point is
    # not the foreground window (usually a terminal or browser is), the
    # LBUTTONDOWN below only activates the window and no control receives it.
    # Activate the target window first with the keystroke-free
    # AttachThreadInput dance (no Alt tap: it would poke the user's browser).
    $ap = New-Object WmdVerify.WmdUser32+POINT; $ap.X = [int]$x; $ap.Y = [int]$y
    $targetHwnd = [WmdVerify.WmdUser32]::WindowFromPoint($ap)
    $fgHwnd = [WmdVerify.WmdUser32]::GetForegroundWindow()
    if ($targetHwnd -ne [IntPtr]::Zero -and $targetHwnd -ne $fgHwnd) {
        $fgTid = [uint32]0; $fgPid = [uint32]0
        $fgTid = [WmdVerify.WmdUser32]::GetWindowThreadProcessId($fgHwnd, [ref]$fgPid)
        $myTid = [WmdVerify.WmdUser32]::GetCurrentThreadId()
        if ($fgTid -ne 0 -and $fgTid -ne $myTid) {
            [void][WmdVerify.WmdUser32]::AttachThreadInput($myTid, $fgTid, $true)
            [void][WmdVerify.WmdUser32]::SetForegroundWindow($targetHwnd)
            [void][WmdVerify.WmdUser32]::AttachThreadInput($myTid, $fgTid, $false)
            Start-Sleep -Milliseconds 300
        }
    }
    [WmdVerify.WmdUser32]::mouse_event(0x0002, 0, 0, 0, 0)  # MOUSEEVENTF_LEFTDOWN
    Start-Sleep -Milliseconds 40
    [WmdVerify.WmdUser32]::mouse_event(0x0004, 0, 0, 0, 0)  # MOUSEEVENTF_LEFTUP
}

function Do-Click($el) {
    # No Invoke pattern (or the element went stale) - fall through to the
    # verified mouse click.
    try {
        $inv = [WmdUia.Core]::InvokePattern($el)
        if ($null -ne $inv) {
            [WmdUia.Core]::Invoke($inv)
            return "invoke"
        }
    } catch { }
    $r = [WmdUia.Core]::BoundingRect($el)
    Do-ClickScreen ([int]($r.Left + ($r.Right - $r.Left) / 2)) ([int]($r.Top + ($r.Bottom - $r.Top) / 2))
    return "mouse"
}

function Queue-Children($stack, $el) {
    # Children are pushed in REVERSED sibling order so the LIFO pop comes out
    # in natural (document / left-to-right) tree order. A raw stack DFS pops
    # the last child first and visits every subtree right-to-left, which made
    # click-nth #N silently mean the Nth element from the RIGHT.
    $kids = New-Object System.Collections.Generic.List[object]
    $child = [WmdUia.Core]::FirstChild($el)
    while ($null -ne $child) { $kids.Add($child); $child = [WmdUia.Core]::NextSibling($child) }
    for ($i = $kids.Count - 1; $i -ge 0; $i--) { $stack.Push($kids[$i]) }
}

function Collect-Elements([string]$needle, $buttonOnly) {
    Init-Uia
    $root = [WmdUia.Core]::RootElement()
    $st = Read-State
    $appPid = if ($st -and $st.pid) { [int]$st.pid } else { 0 }

    # Seed the stack with the desktop's top-level windows (natural order) and,
    # when a state file exists, pre-filter by the app's pid. Dialog windows
    # share the app pid (owned by it), so confirm/prompt flows keep working,
    # while foreign windows - notably the terminal, whose Name mirrors the
    # echoed command text verbatim and can therefore contain needle glyphs -
    # are never walked at all (their UIA subtrees are large and slow too).
    $tops = New-Object System.Collections.Generic.List[object]
    $tw = [WmdUia.Core]::FirstChild($root)
    while ($null -ne $tw) { $tops.Add($tw); $tw = [WmdUia.Core]::NextSibling($tw) }
    $stack = New-Object System.Collections.Stack
    for ($i = $tops.Count - 1; $i -ge 0; $i--) {
        if ($appPid -ne 0 -and [WmdUia.Core]::ProcessId($tops[$i]) -ne $appPid) { continue }
        $stack.Push($tops[$i])
    }

    $found = New-Object System.Collections.Generic.List[object]
    while ($stack.Count -gt 0) {
        $el = $stack.Pop()
        if ($buttonOnly) {
            # Gate on Invoke-pattern availability (what clicking actually
            # needs) rather than control type: WPF buttons, tab buttons, and
            # dialog OK/Cancel all expose Invoke; containers and labels do not.
            # The bridge's InvokePattern returns $null (not a throw) when the
            # pattern is absent, so test for a non-null result.
            $invokable = $false
            $pat = $null
            try { $pat = [WmdUia.Core]::InvokePattern($el) } catch { }
            if ($null -ne $pat) { $invokable = $true }
            if (-not $invokable) { Queue-Children $stack $el; continue }
        }
        $nm = [WmdUia.Core]::Name($el)
        $aid = [WmdUia.Core]::AutomationId($el)
        $hit = ($nm -and $nm.IndexOf($needle, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) -or
               ($aid -and $aid.IndexOf($needle, [System.StringComparison]::OrdinalIgnoreCase) -ge 0)
        if ($hit) { $found.Add($el) }
        Queue-Children $stack $el
    }
    return $found
}

switch ($Command) {
    'launch' {
        $existing = Get-Process -Name "ModernWigiDash.App" -ErrorAction SilentlyContinue
        if ($existing) {
            Fail ("another ModernWigiDash.App is already running (pid " + ($existing.Id -join ',') + "). Shared-instance rule: verification drives its own instance; close the other one first.")
        }
        $exe = Find-Exe
        $proc = Start-Process -FilePath $exe -PassThru
        Write-State @{ pid = $proc.Id; exe = $exe; startedUtc = (Get-Date).ToUniversalTime().ToString("o"); profileBackup = $null }
        $deadline = (Get-Date).AddSeconds(30)
        $win = $null
        while ((Get-Date) -lt $deadline) {
            $win = Get-MainWindow
            if ($win) { break }
            Start-Sleep -Milliseconds 500
        }
        if (-not $win) {
            Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
            Remove-Item (Join-Path $env:TEMP "opencode\wmd-verify.state.json") -ErrorAction SilentlyContinue
            Fail "window did not appear within 30s (is this an interactive desktop session?)"
        }
        $r = [WmdUia.Core]::BoundingRect($win)
        Write-Output (
            'launched pid=' + $proc.Id + ' exe=' + $exe + ' window "ModernWigiDash" @(' +
            $r.Left + ',' + $r.Top + ' ' + ($r.Right - $r.Left) + 'x' + ($r.Bottom - $r.Top) + ')'
        )
    }
    'doctor' {
        $s = Read-State
        if (-not $s) { Fail "no state - run launch first" }
        $proc = Get-Process -Id $s.pid -ErrorAction SilentlyContinue
        if (-not $proc) { Fail ("process " + [int]$s.pid + " is no longer running") }
        $others = @(Get-Process -Name "ModernWigiDash.App" -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne [int]$s.pid })
        if ($others.Count -gt 0) { Fail ("a second ModernWigiDash.App instance appeared (pid " + ($others.Id -join ',') + ") - refusing to drive a shared session") }
        $winPart = $null
        for ($i = 0; $i -lt $Rest.Count; $i++) { if ($Rest[$i] -eq '-Window') { $winPart = $Rest[$i + 1] } }
        if ($winPart) {
            $win = Get-AnyWindow $winPart
            if (-not $win) { Fail ('window matching "' + $winPart + '" not found') }
            if ($s.pid -and [WmdUia.Core]::ProcessId($win) -ne [int]$s.pid) { Fail ('window matching "' + $winPart + '" belongs to pid ' + [WmdUia.Core]::ProcessId($win) + ', not the launched pid ' + [int]$s.pid) }
            if (-not [WmdUia.Core]::IsEnabled($win)) { Fail ('window "' + $winPart + '" is disabled') }
            $r = [WmdUia.Core]::BoundingRect($win)
            Write-Output (
                'ok: window "' + $winPart + '" @(' +
                $r.Left + ',' + $r.Top + ') ' + ($r.Right - $r.Left) + 'x' + ($r.Bottom - $r.Top) +
                ') pid=' + [WmdUia.Core]::ProcessId($win) + ' enabled'
            )
        } else {
            $win = Get-MainWindow
            if (-not $win) { Fail "main window 'ModernWigiDash' not visible to UIA (check the interactive desktop)" }
            if (-not [WmdUia.Core]::IsEnabled($win)) { Fail "main window is disabled (UI thread may be blocked)" }
            $r = [WmdUia.Core]::BoundingRect($win)
            $log = Join-Path $env:LOCALAPPDATA "ModernWigiDash\display_device.log"
            $logNote = if (Test-Path $log) { "log=$log" } else { "log=missing" }
            Write-Output (
                'ok: pid=' + $proc.Id + ' window "ModernWigiDash" @(' +
                $r.Left + ',' + $r.Top + ') ' + ($r.Right - $r.Left) + 'x' + ($r.Bottom - $r.Top) +
                ') enabled ' + $logNote
            )
        }
    }
    'dump' {
        $s = Read-State
        if (-not $s) { Fail "no state - run launch first" }
        $out = $null
        for ($i = 0; $i -lt $Rest.Count; $i++) { if ($Rest[$i] -eq '-Path') { $out = $Rest[$i + 1] } }
        $win = Get-MainWindow
        if (-not $win) { Fail "main window not found" }
        $budget = [PSCustomObject]@{ Value = 900 }
        $lines = Get-ChildLines $win 0 14 $budget
        if ($out) {
            $lines | Set-Content $out -Encoding UTF8
            Write-Output ("dumped " + $lines.Count + " nodes to " + $out)
        } else {
            $lines | Write-Output
        }
    }
    'find' {
        if ($Rest.Count -lt 1) { Fail "usage: find <needle>" }
        for ($i = 0; $i -lt $Rest.Count; $i++) { if ($Rest[$i] -ceq '-') { Fail ("unexpected flag '" + $Rest[$i] + "'") }; break }
        $needle = $Rest[0]
        $s = Read-State
        if (-not $s) { Fail "no state - run launch first" }
        $win = Get-MainWindow
        if (-not $win) { Fail "main window not found" }
        $budget = [PSCustomObject]@{ Value = 900 }
        $hits = New-Object System.Collections.Generic.List[string]
        $script:needle = $needle
        function Search-Hits($el) {
            if ($budget.Value -le 0) { return }
            $budget.Value--
            $ct = Get-TypeName ([WmdUia.Core]::ControlType($el))
            $nm = [WmdUia.Core]::Name($el)
            $aid = [WmdUia.Core]::AutomationId($el)
            if (($nm -and $nm.IndexOf($script:needle, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) -or
                ($aid -and $aid.IndexOf($script:needle, [System.StringComparison]::OrdinalIgnoreCase) -ge 0)) {
                $hit = ' [' + $ct + ']'
                if ($aid) { $hit += ' id=' + $aid }
                if ($nm) { $hit += ' "' + $nm + '"' }
                $r = [WmdUia.Core]::BoundingRect($el)
                if ($r.Right - $r.Left -gt 0) { $hit += (' @(' + $r.Left + ',' + $r.Top + ') ' + ($r.Right - $r.Left) + 'x' + ($r.Bottom - $r.Top) + ')') }
                $hits.Add($hit)
            }
            $child = [WmdUia.Core]::FirstChild($el)
            while ($null -ne $child) { Search-Hits $child; $child = [WmdUia.Core]::NextSibling($child) }
        }
        Search-Hits $win
        if ($hits.Count -eq 0) { Write-Output ('no match for "' + $needle + '"') } else { $hits | ForEach-Object { Write-Output $_ } }
    }
    'click-nth' {
        if ($Rest.Count -lt 2) { Fail "usage: click-nth <needle> <n>   (1-based, tree order)" }
        $all = Collect-Elements $Rest[0] $true
        $n = [int]$Rest[1]
        if ($n -lt 1 -or $n -gt $all.Count) { Fail ('no button match #' + $n + ' for "' + $Rest[0] + '": only ' + $all.Count + ' buttons found') }
        $el = $all[$n - 1]
        $how = Do-Click $el
        Write-Output ('clicked match #' + $n + ' of "' + $Rest[0] + '" (name="' + [WmdUia.Core]::Name($el) + '") via ' + $how)
    }
    'list' {
        # Read-only: numbered matches in tree order with positions — run it
        # before click-nth to prove which #N is which (e.g. the per-tab close
        # buttons of the page strip).
        if ($Rest.Count -lt 1) { Fail "usage: list <needle> [buttons]   (read-only)" }
        $btnOnly = ($Rest.Count -ge 2 -and $Rest[1] -eq 'buttons')
        $all = Collect-Elements $Rest[0] $btnOnly
        if ($all.Count -eq 0) { Write-Output ('no match for "' + $Rest[0] + '"') }
        for ($i = 0; $i -lt $all.Count; $i++) {
            $c = $all[$i]
            $r = [WmdUia.Core]::BoundingRect($c)
            $ctName = Get-TypeName ([WmdUia.Core]::ControlType($c))
            Write-Output ('[' + ($i + 1) + '] ' + $ctName + ' "' + [WmdUia.Core]::Name($c) + '" @' + $r.Left + ',' + $r.Top + ' ' + ($r.Right - $r.Left) + 'x' + ($r.Bottom - $r.Top))
        }
    }
    'click' {
        if ($Rest.Count -lt 1) { Fail "usage: click <needle>" }
        $el = Find-Element $Rest[0]
        if (-not $el) { Fail ('no control matching "' + $Rest[0] + '"') }
        $how = Do-Click $el
        Write-Output ('clicked "' + [WmdUia.Core]::Name($el) + '" (' + $how + ')')
    }
    'value' {
        if ($Rest.Count -lt 1) { Fail "usage: value <needle>" }
        $el = Find-Element $Rest[0]
        if (-not $el) { Fail ('no control matching "' + $Rest[0] + '"') }
        $val = $null
        # v4 ValuePattern via the bridge: PatternValue returns the string itself.
        try { $vp = [WmdUia.Core]::ValuePattern($el); if ($null -ne $vp) { $val = [WmdUia.Core]::PatternValue($vp) } } catch { }
        if (-not $val) { $val = [WmdUia.Core]::Name($el) }
        Write-Output $val
    }
    'set' {
        if ($Rest.Count -lt 2) { Fail "usage: set <needle> <value>" }
        $el = Find-Element $Rest[0]
        if (-not $el) { Fail ('no control matching "' + $Rest[0] + '"') }
        try {
            $vp = [WmdUia.Core]::ValuePattern($el)
            if ($null -eq $vp) { throw [System.Exception]::new('no ValuePattern') }
            [WmdUia.Core]::SetValue($vp, $Rest[1])
            Write-Output ('set "' + [WmdUia.Core]::Name($el) + '" = "' + $Rest[1] + '"')
        } catch {
            Fail ('control "' + [WmdUia.Core]::Name($el) + '" has no writable ValuePattern')
        }
    }
    'set-in' {
        # Deterministic input for dialogs whose text box carries no UIA Name
        # (WPF maps string content to Name, not a bare TextBox): find the
        # dialog window by title, write its single writable text control.
        # The main window is refused outright - its named boxes are addressed
        # with set <needle> <value>, and an unnamed-target write there would
        # be a wrong-box risk (the catalog filter sits first in tree order).
        if ($Rest.Count -lt 2) { Fail "usage: set-in <windowTitle> <value>" }
        $win = Get-DialogWindow $Rest[0]
        if (-not $win) { Fail ('no window with title containing "' + $Rest[0] + '"') }
        $s = Read-State
        if ($s -and $s.pid -and [string][WmdUia.Core]::Name($win) -eq "ModernWigiDash") {
            Fail 'that is the main app window - set-in needs the dialog title (e.g. Rename Page); main-window boxes are addressed by name with: set <needle> <value>'
        }
        $readBack = Set-FirstWritableText $win $Rest[1]
        if ($null -eq $readBack) {
            Fail ('window "' + [WmdUia.Core]::Name($win) + '" has no writable text control')
        }
        Write-Output ('set the unnamed text control of window "' + [WmdUia.Core]::Name($win) + '" = "' + $Rest[1] + '" (read-back: "' + $readBack + '")')
    }
    'click-at' {
        if ($Rest.Count -lt 3) { Fail "usage: click-at <needle> <x> <y>" }
        $el = Find-Element $Rest[0]
        if (-not $el) { Fail ('no control matching "' + $Rest[0] + '"') }
        $r = [WmdUia.Core]::BoundingRect($el)
        $x = $r.Left + [int]$Rest[1]
        $y = $r.Top + [int]$Rest[2]
        Do-ClickScreen $x $y
        Write-Output ('clicked at control-relative (' + $Rest[1] + ',' + $Rest[2] + ') -> screen (' + $x + ',' + $y + ')')
    }
    'click-screen' {
        # Absolute-screen canvas pointing for Skia-drawn surfaces that expose
        # NO UIA peer at all (the preview canvas): click-at needs a named
        # control, so its x,y can't reach the canvas. Same hard cursor gate +
        # activation dance as click-at (Do-ClickScreen).
        if ($Rest.Count -lt 2) { Fail "usage: click-screen <x> <y> (absolute screen coordinates)" }
        Do-ClickScreen ([int]$Rest[0]) ([int]$Rest[1])
        Write-Output ('clicked at absolute screen (' + $Rest[0] + ',' + $Rest[1] + ')')
    }
    'shot' {
        if ($Rest.Count -lt 1) { Fail "usage: shot <path>" }
        $win = Get-MainWindow
        if (-not $win) { Fail "main window not found" }
        Add-Type -AssemblyName System.Drawing
        Ensure-User32
        $hwnd = [IntPtr][WmdUia.Core]::NativeWindowHandle($win)
        $rect = New-Object WmdVerify.WmdUser32+RECT
        [WmdVerify.WmdUser32]::GetWindowRect($hwnd, [ref]$rect) | Out-Null
        $w = $rect.Right - $rect.Left
        $h = $rect.Bottom - $rect.Top
        if ($w -le 0 -or $h -le 0) { Fail ("bad window rect " + $rect.Left + ',' + $rect.Top + ',' + $rect.Right + ',' + $rect.Bottom) }
        $bmp = New-Object System.Drawing.Bitmap($w, $h)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
        $dir = Split-Path $Rest[0] -Parent
        if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        $bmp.Save($Rest[0], [System.Drawing.Imaging.ImageFormat]::Png)
        $g.Dispose(); $bmp.Dispose()
        Write-Output ("screenshot " + $w + " x " + $h + " -> " + $Rest[0])
    }
    'wait' {
        if ($Rest.Count -lt 1) { Fail "usage: wait <namePart> [-Seconds <n>]" }
        $part = $Rest[0]
        $seconds = 15
        for ($i = 0; $i -lt $Rest.Count; $i++) { if ($Rest[$i] -eq '-Seconds') { $seconds = [int]$Rest[$i + 1] } }
        $deadline = (Get-Date).AddSeconds($seconds)
        while ((Get-Date) -lt $deadline) {
            $win = Get-AnyWindow $part
            $winPid = if ($win) { [WmdUia.Core]::ProcessId($win) } else { 0 }
            if ($win -and $winPid -ne 0) {
                $pid2 = $winPid
                $s = Read-State
                if (-not $s -or -not $s.pid -or $pid2 -eq [int]$s.pid) {
                    Write-Output ('window "' + [WmdUia.Core]::Name($win) + '" present (pid ' + $pid2 + ')')
                    return
                }
            }
            Start-Sleep -Milliseconds 250
        }
        Fail ('no window matching "' + $part + '" within ' + $seconds + ' s')
    }
    'backup-profile' {
        $appData = Join-Path $env:LOCALAPPDATA "ModernWigiDash"
        $dir = Join-Path $env:TEMP "opencode\wmd-profile-backup"
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        $s = Read-State
        if (-not $s) { $s = [PSCustomObject]@{ pid = 0 } }
        $saved = @()
        foreach ($f in @('profile.json', 'app_theme.json')) {
            $src = Join-Path $appData $f
            if (Test-Path $src) {
                Copy-Item $src (Join-Path $dir $f) -Force
                $saved += $f
            }
        }
        $s | Add-Member -NotePropertyName profileBackup -NotePropertyValue $dir -Force
        Write-State $s
        if ($saved.Count -eq 0) { Write-Output "nothing to back up (no profile files yet)" } else { Write-Output ("backed up " + ($saved -join ', ') + " -> " + $dir) }
    }
    'restore-profile' {
        $s = Read-State
        $dir = if ($s -and $s.profileBackup) { $s.profileBackup } else { Join-Path $env:TEMP "opencode\wmd-profile-backup" }
        if (-not (Test-Path $dir)) { Write-Output ("no backup at " + $dir + " - nothing to restore"); return }
        $appData = Join-Path $env:LOCALAPPDATA "ModernWigiDash"
        if (-not (Test-Path $appData)) { New-Item -ItemType Directory -Path $appData -Force | Out-Null }
        $restored = @()
        foreach ($f in Get-ChildItem $dir -File) {
            Copy-Item (Join-Path $dir $f.Name) (Join-Path $appData $f.Name) -Force
            $restored += $f.Name
        }
        if ($s) { Write-State $s }
        Write-Output ("restored " + ($restored -join ', '))
    }
    'stop' {
        $s = Read-State
        if (-not $s -or -not $s.pid) { Write-Output "no launched pid recorded - nothing to stop"; return }
        $proc = Get-Process -Id $s.pid -ErrorAction SilentlyContinue
        if (-not $proc) { Write-Output ("pid " + [int]$s.pid + " already gone"); Remove-Item (Join-Path $env:TEMP "opencode\wmd-verify.state.json") -Force; return }
        Stop-Process -Id $proc.Id -Force
        $deadline = (Get-Date).AddSeconds(10)
        while ((Get-Date) -lt $deadline -and (Get-Process -Id $proc.Id -ErrorAction SilentlyContinue)) { Start-Sleep -Milliseconds 250 }
        if (Get-Process -Id $proc.Id -ErrorAction SilentlyContinue) { Fail ("pid " + [int]$s.pid + " survived Stop-Process 10s; not escalating (inspect manually)") }
        Write-Output ("stopped pid " + [int]$s.pid)
    }
    'clean' {
        & $PSCommandPath stop
        if ($LASTEXITCODE -ne 0) { Fail "stop failed; aborting clean" }
        & $PSCommandPath restore-profile
        Remove-Item (Join-Path $env:TEMP "opencode\wmd-verify.state.json") -Force -ErrorAction SilentlyContinue
        Write-Output "clean: app stopped, profile restored, state dropped (evidence artifacts untouched)"
    }
    default {
        Fail ('unknown command "' + $Command + '" (launch|doctor|dump|find|list|click|click-nth|value|set|set-in|click-at|shot|wait|backup-profile|restore-profile|stop|clean)')
    }
}
exit 0