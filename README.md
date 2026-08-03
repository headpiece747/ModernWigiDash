# ModernWigiDash

A .NET 10 (C# 14) desktop application and background service for USB display devices using native WinUSB transport.

## Quick Start

**Prerequisites**: .NET 10 SDK, Windows 10+ (for native WinUSB).

### Run the App (WPF Desktop)

Launches the dashboard editor with drag-and-drop widget layout:

```powershell
dotnet run --project ModernWigiDash.App\ModernWigiDash.App.csproj
```

### Run the Service (Background + USB)

Runs the CoreWCF background service with USB device support on `http://localhost:8733/`:

```powershell
dotnet run --project ModernWigiDash.Service\ModernWigiDash.Service.csproj -- -test
```

The app connects to the service automatically when it's running on the same machine.

## Twitch Widget

The Twitch widget can authenticate without asking users to paste an OAuth token. It uses Twitch's Device Authorization Grant and keeps the access and refresh tokens encrypted in the current Windows user's local application data.

To enable live followed-channel selection:

1. Register a Twitch application at the [Twitch Developer Console](https://dev.twitch.tv/console).
2. Use the application's public Client ID in the Twitch widget's **Twitch Client ID** setting, or set `MODERNWIGIDASH_TWITCH_CLIENT_ID` in the user's environment.
3. Select **Log in with Twitch** in the widget inspector and authorize the requested `user:read:follows` permission in the browser.
4. Select a live channel from the populated **Channel Name** list and keep **Auto Connect** enabled.

The Client ID is public and is not a user token or client secret. The widget continues to use anonymous, read-only IRC chat; it does not request chat-writing permissions. Twitch exposes followed live channels through `GET /helix/streams/followed` using `user:read:follows`, but does not provide a general API for listing every paid channel subscription.

### Run Tests

```powershell
dotnet test
```

### Build All

```powershell
dotnet build ModernWigiDash.slnx
```

## Project Structure

| Project | Description |
| :--- | :--- |
| **ModernWigiDash.Sdk** | Base interfaces, widget attributes, grid enums |
| **ModernWigiDash.Core** | SkiaSharp frame compositor, profile/layout model, plugin loader |
| **ModernWigiDash.Hardware** | Native USB transport layer (WinUSB bulk streaming) |
| **ModernWigiDash.Widgets** | Built-in widgets (clock, weather, system telemetry, media) |
| **ModernWigiDash.Service** | CoreWCF HTTP background service, USB device manager |
| **ModernWigiDash.App** | WPF desktop application with widget editor |
| **ModernWigiDash.Tests** | MSTest suite |

## Installing as a Windows Service

For persistent background operation:

```powershell
# Run PowerShell as Administrator
.\Install-ModernWigiDashService.ps1
```

```powershell
.\Install-ModernWigiDashService.ps1 -Uninstall
```

## License

MIT
