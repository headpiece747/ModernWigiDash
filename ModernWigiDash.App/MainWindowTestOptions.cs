using ModernWigiDash.App.Hotkey;
using ModernWigiDash.App.Power;
using ModernWigiDash.App.PresentMon;
using ModernWigiDash.Hardware.Transport;

namespace ModernWigiDash.App;

/// <summary>
/// The window test seam's named options: the three core seams (PresentMon
/// interop, profile path, power-mode source) plus the six optional seams
/// (tray surface, session-end standby probe, hotkey API, AHK spawn,
/// app-settings store, USB engine). A test construction spells each
/// non-default seam by name, so the old nine-argument positional form and
/// its anonymous run of nulls is unrepresentable.
/// </summary>
internal sealed record MainWindowTestOptions(
    IPresentMonNative PresentMonNative,
    string ProfilePath,
    IPowerModeSource PowerModeSource,
    ITrayIconSurface? TraySurface = null,
    Func<bool>? SessionEndStandby = null,
    HotkeyApi? HotkeyApi = null,
    AhkLaunchApi? AhkApi = null,
    AppSettingsStore? AppSettingsStore = null,
    DisplayDeviceEngine? UsbEngine = null);
