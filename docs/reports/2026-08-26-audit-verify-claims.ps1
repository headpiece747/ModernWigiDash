# Persisted audit artifact (2026-08-26): the trusted OCR pass's claim list,
# encoded as per-claim source checks. Copy of the temp-dir
# verify-ocr-claims.ps1 with the post-audit DialogHost.cs move applied
# (Dialogs/DialogHost.cs -> DialogHost.cs at the App project root). Run from
# the repo root: powershell -NoProfile -File
# docs\reports\2026-08-26-audit-verify-claims.ps1
# The verdicts for every section are in 2026-08-26-audit-findings.md.
$ErrorActionPreference = 'Continue'
function Sec($n) { Write-Output ''; Write-Output "=== $n ===" }
function Grep($pat, $paths) {
    Select-String -Path $paths -Pattern $pat -ErrorAction SilentlyContinue |
        ForEach-Object { (Split-Path $_.Path -Leaf) + ':' + $_.LineNumber + ': ' + $_.Line.Trim() }
}

Sec '1. WeatherWidgetRenderer IDisposable / paints / widget dispose'
Grep 'class WeatherWidgetRenderer|IDisposable|_renderer' 'ModernWigiDash.Widgets/WeatherWidgetRenderer.cs'
Grep '_renderer|DisposeAsync' 'ModernWigiDash.Widgets/WeatherForecastWidget.cs' | Select-Object -First 12

Sec '2. FrameDelivery _encodeFailLog'
Grep '_encodeFailLog' 'ModernWigiDash.Sdk/FrameDelivery.cs'

Sec '3. IModernWidget Context declaration'
Grep 'Context' 'ModernWigiDash.Sdk/IModernWidget.cs'

Sec '4. Sdk InternalsVisibleTo'
Get-Content 'ModernWigiDash.Sdk/InternalsVisibleTo.cs' -ErrorAction SilentlyContinue

Sec '5. DialogHost DeviceAuthWidth (formerly ColorPickerWidth)'
Grep 'ColorPickerWidth|DeviceAuth' 'ModernWigiDash.App/DialogHost.cs'

Sec '6. WidgetRouting zindex tie-break'
Grep 'ZIndex' 'ModernWigiDash.Core/Rendering/WidgetRouting.cs'

Sec '7. WeatherClient switch arms'
Grep 'Resolved|Ambiguous|NoMatch|default' 'ModernWigiDash.Widgets/WeatherClient.cs' | Select-Object -First 14

Sec '8. ProfileOps created pattern + IsSafeInstanceId'
Grep 'created is|IsSafeInstanceId' 'ModernWigiDash.Core/Models/ProfileOps.cs' | Select-Object -First 8

Sec '9. WidgetPluginLoader exception log'
Grep 'ex\.|failed for' 'ModernWigiDash.Core/Plugins/WidgetPluginLoader.cs' | Select-Object -First 8

Sec '10. UpdateService zip-slip + cap'
Grep 'MaxUpdateBytes|escapes the stage|InvalidDataException' 'ModernWigiDash.App/Update/UpdateService.cs' | Select-Object -First 8

Sec '11. HotkeyApi full'
Get-Content 'ModernWigiDash.App/Hotkey/HotkeyApi.cs'

Sec '12. MainWindow ctor comment'
Grep 'constructor-argument fallback|api seams' 'ModernWigiDash.App/MainWindow.xaml.cs' | Select-Object -First 6

Sec '13. MainWindow teardown step + hotkey field'
Grep 'GlobalHotkeys|_globalHotkeyManager|_hotkeyHwnd' 'ModernWigiDash.App/MainWindow.xaml.cs' | Select-Object -First 20

Sec '14. MainWindow duplicate-chord log region'
Grep 'dropped|duplicate' 'ModernWigiDash.App/MainWindow.xaml.cs' | Select-Object -First 8

Sec '15. Context PersistProperty + LaunchAutoHotkeyScript'
Grep 'RefreshGlobalHotkeys|scriptPath|LaunchAutoHotkeyScript' 'ModernWigiDash.App/MainWindow.Context.cs' | Select-Object -First 14

Sec '16. SettingsDialog ahk browse'
Grep 'ahkPath|Browse' 'ModernWigiDash.App/Dialogs/SettingsDialog.cs' | Select-Object -First 16

Sec '17. InspectorPanelRenderer key capture editor'
Grep 'BuildKeyCaptureEditor|ResolvePressKey|ChordKeyName|CancelCapture|PreviewKeyDown|LostFocus' 'ModernWigiDash.App/Inspector/InspectorPanelRenderer.cs' | Select-Object -First 20

Sec '18. TickerPresentation FormatPrice'
Grep 'FormatPrice|DisplayFormat.Number' 'ModernWigiDash.Widgets/TickerPresentation.cs' | Select-Object -First 8

Sec '19. WeatherLocationResolver escape + normalize + abbrev'
Grep 'EscapeDataString|NormalizeForMatch|StateAbbreviationMatches|CountryAliases' 'ModernWigiDash.Widgets/WeatherLocationResolver.cs' | Select-Object -First 14

Sec '20. measure-coverage.ps1'
Grep 'LastWriteTime|GetTempPath|CollectCoverage|totalValid' 'scripts/measure-coverage.ps1' | Select-Object -First 10

Sec '21. test existence: chord key name'
Grep 'ChordKeyName|NumPad' 'ModernWigiDash.Tests/KeyCaptureEditorTests.cs' | Select-Object -First 8

Sec '22. test existence: zip-slip / MaxUpdateBytes'
Grep 'ZipEntry|escapes|MaxUpdateBytes|InvalidDataException' 'ModernWigiDash.Tests/Update*.cs' | Select-Object -First 8

Sec '23. test existence: SafeCacheToken / IsSafeInstanceId'
Grep 'SafeCacheToken|IsSafeInstanceId' 'ModernWigiDash.Tests/ProfileImportSanitizerTests.cs' | Select-Object -First 8

Sec '24. test existence: WeatherPresentation Build'
Grep 'Build' 'ModernWigiDash.Tests/WeatherPresentationTests.cs' | Select-Object -First 8

Sec '25. test existence: FrameDelivery recovery'
Grep 'SetThrowOnEncode|recovery|Recover' 'ModernWigiDash.Tests/FrameDeliveryTests.cs' | Select-Object -First 8

Sec '26. TwitchChatStatusPolicy current'
Grep 'Changed|ThrowIfNull|Login unsuccessful|Improperly|Invalid NICK' 'ModernWigiDash.Widgets/Twitch/TwitchChatStatusPolicy.cs' | Select-Object -First 12