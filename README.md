# SteelSeries GG DPI Switcher

A small Windows desktop app for switching DPI on a **wired SteelSeries Aerox 5**.
Choose a level from the compact window, tray menu, or global keyboard shortcuts.
The app remembers your selection and restores it when it starts.

## Features

- Five DPI levels: **700, 800, 900, 1000, and 1100**.
- Global shortcuts that work while the window is hidden.
- A brief popup showing the selected DPI and level after a shortcut change.
- Saved selection, automatic restoration, and retries when the mouse is unavailable.
- A draggable window that stays on top and can hide to the notification tray.

## Requirements

- Windows 10 or 11, 64-bit.
- .NET Framework 4.8.
- A wired SteelSeries Aerox 5: USB vendor `1038`, product `1850`.

Other mouse models, including the wireless Aerox 5, are currently unsupported.
The app communicates directly with the mouse through Windows USB HID APIs.

## Build and run

Download or clone the repository, then open PowerShell in its root folder:

```powershell
.\build.ps1
& '.\SteelSeries GG DPI Switcher.exe'
```

The script uses the Windows .NET Framework C# compiler. It produces
`SteelSeries GG DPI Switcher.exe` in the repository root and downloads no external dependencies.

Close the app before rebuilding that executable. To build a separate copy while
the app is running:

```powershell
.\build.ps1 -OutputPath 'work\SteelSeries GG DPI Switcher.exe'
```

Relative output paths are resolved from the repository root. Generated executables,
debug symbols, and the `work` folder are excluded from Git.

## Usage

Click a DPI button to apply a level. The highlighted button and **Last set** label
show the last command successfully sent by this app.

- Drag the header to move the window.
- Press **Escape** or click **−** to hide it to the notification tray.
- Click the tray icon to reopen it, or right-click for DPI choices and shortcut information.
- Click **×** or choose **Exit** in the tray menu to quit.
- Opening the executable again reveals the existing window.

Windows may place the tray icon in the notification area's overflow menu.

### Keyboard shortcuts

| Shortcut | Action |
| --- | --- |
| Ctrl+Alt+Up | Next level; wraps from 1100 to 700 DPI |
| Ctrl+Alt+Down | Previous level; wraps from 700 to 1100 DPI |
| Ctrl+Alt+1 | 700 DPI |
| Ctrl+Alt+2 | 800 DPI |
| Ctrl+Alt+3 | 900 DPI |
| Ctrl+Alt+4 | 1000 DPI |
| Ctrl+Alt+5 | 1100 DPI |

The app must be running for these shortcuts to work. Holding a key suppresses
automatic repetition; rapid separate presses are combined into the latest target.
If another app has reserved a shortcut, the tray menu lists it as unavailable.

After a successful shortcut change, a small popup shows the DPI and level for
about **1.4 seconds** near the bottom-right of the active monitor. Repeated changes
update the same popup and restart its timer. The popup allows clicks through and
keeps your current app focused, including when the main window is hidden.

### Remembering your DPI

Successful selections are saved in your Windows user's local application-data
folder. If saving fails, the app displays a warning.

On startup, the app reapplies the saved DPI. If the mouse is unavailable or not
ready, it waits and retries. It also restores the selection after a detected
reconnection. Automatic restoration does not show a popup.

### Start with Windows

Startup is optional and configured manually:

1. Build the app and create a Windows shortcut to `SteelSeries GG DPI Switcher.exe`.
2. Press **Win+R**, enter `shell:startup`, and press **Enter**.
3. Move the shortcut into the folder that opens.

The app will open and restore its saved DPI when you sign in. Update the shortcut
if you move the executable. To disable automatic startup, remove the shortcut.

## Behavior and limitations

- Each selection sends all five presets and chooses the requested level.
- The app sends no onboard-memory save command. Lighting, button bindings,
  polling rate, and Windows pointer-speed settings are unaffected.
- **Last set is not live hardware readback.** Changes made using the mouse's DPI
  button or SteelSeries GG are not tracked. Select a level here to apply it again.
- SteelSeries GG can apply its own configuration afterward and override a selection.
- A successful USB write confirms that the command was sent; this implementation
  does not independently verify the mouse's resulting DPI.

## Project layout

```text
src/
  AppInfo.cs          Application name and executable metadata
  Program.cs          Main window, tray menu, and selection handling
  AeroxDevice.cs      USB HID communication
  DpiPreferences.cs   Saved DPI settings
  DpiHotkeys.cs       Global keyboard shortcuts
  DpiToast.cs         Shortcut popup
assets/
  app.ico             Application icon
  app.manifest        Windows application manifest
build.ps1             Build script
README.md             Documentation
```

## Acknowledgments

The mouse sensitivity protocol is based on the public device information in
[Rivalcfg](https://github.com/flozz/rivalcfg), particularly its
[Aerox 5 profile](https://github.com/flozz/rivalcfg/blob/master/rivalcfg/devices/aerox5.py)
and [TrueMove Air DPI table](https://github.com/flozz/rivalcfg/blob/master/rivalcfg/devices/dpi/truemove_air.py).
The app implements its Windows HID communication directly and does not bundle Rivalcfg.

This is an independent community project, unaffiliated with SteelSeries.
