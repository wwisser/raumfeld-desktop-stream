# Raumfeld Desktop Stream

Stream Windows system audio to Teufel Raumfeld speakers over your LAN.
The app captures your playback device through WASAPI loopback, encodes MP3 and
serves it over HTTP. It discovers the room, creates a playback zone if needed,
and starts the speaker. SWYH, Node.js and Raumserver are not required.

## Windows tray and installation

Run `artifacts/installer/Raumfeld-Setup.exe` to install for your Windows user.
The installer includes the .NET runtime, adds a Start menu shortcut and enables
**Open Raumfeld at Windows login** by default. No administrator account is needed.
Login starts the tray quietly; press **Start streaming** when you want to send audio.
Uninstall through Windows Installed apps. Configuration and logs are retained.

Click the notification icon to open the compact control panel. It shows the
selected room and Windows audio source side by side, with the stream state above.
Hover over the status for connection details and bytes sent, or over a truncated
room/source name to read it in full. Start/stop controls reuse the CLI worker.
Clicking outside the panel or
pressing Escape hides it; **Quit** stops streaming and exits the tray. Right-click
the icon for quick controls. Opening the app again shows the existing panel.
Windows may initially put the icon in the notification area's overflow menu.

The panel provides a login-startup toggle, **Logs** and **Quit**. Its centered,
black-and-white layout is 360 × 224 logical pixels, with no title bar, and opens
above the notification area. The Raumfeld emblem appears above **DESKTOP** on
white. Edit the JSON configuration directly and restart streaming to apply it.
An existing configuration from the previous app name is imported automatically
when no new configuration exists. Installation replaces the old startup entry.

```powershell
.\raumfeld.ps1 build
.\raumfeld.ps1 tray
.\raumfeld.ps1 publish
# Install Inno Setup 6, then build the self-contained Windows installer:
.\scripts\Build-Installer.ps1
# Optional: -Compiler C:\path\to\ISCC.exe -Version 1.2.3
```

Installer builds stage files in a fresh folder under `artifacts/package` and write
`artifacts/installer/Raumfeld-Setup.exe`. This allows an existing published
stream to keep running while building. The setup executable is unsigned.
Portable users can run `Raumfeld.Tray.exe` from the publish folder;
`--hidden` starts with only the notification icon, and `--config <path>` selects
a configuration file. The CLI remains available as `Raumfeld.DesktopStream.exe`.

## Build and run

You need Windows 11 x64, an active playback device and a Raumfeld speaker on the
same LAN. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
to build. The launcher also detects a local SDK in `.tools/dotnet`.

```powershell
.\raumfeld.ps1 build
.\raumfeld.ps1 doctor
.\raumfeld.ps1 start
.\raumfeld.ps1 status
.\raumfeld.ps1 stop
```

`start` runs in the background and waits until the receiver plays the stream and
fetches audio bytes. Repeated starts reuse the worker. `stop` releases audio and
network resources. It stops speaker playback only when the speaker still has
this app's stream loaded.

```powershell
.\raumfeld.ps1 logs
.\raumfeld.ps1 logs -Follow
.\raumfeld.ps1 run              # Foreground output. Ctrl+C stops the app.
.\raumfeld.ps1 publish
```

Publish produces `artifacts/publish/Raumfeld.DesktopStream.exe` with its .NET
runtime. Copy the whole folder to another Windows x64 machine. The executable
accepts `start`, `stop`, `status`, `doctor`, `logs` and `run`.
The launcher prefers the published copy, so republish after changing source code.

## Configuration

One room needs no configuration. With several rooms, set `roomName` to the name
or UDN shown by `doctor`.

```powershell
Copy-Item raumfeld.example.json raumfeld.local.json
# Edit raumfeld.local.json, then start.
.\raumfeld.ps1 start
```

The launcher reads `raumfeld.local.json` when present. You can also pass
`-Config <path>`. Otherwise, the app looks for
`%LOCALAPPDATA%\Raumfeld.DesktopStream\config.json`.
Use valid JSON. Unknown settings fail validation. Restart to apply changes.

| Setting | Default | Use |
|---|---|---|
| `roomName` | `null` | Select the only room, or match a room name or UDN. |
| `raumfeldHost` | `"auto"` | Discover the host, or use a hostname or IPv4 address. |
| `raumfeldPort` | `47365` | Native Raumfeld host port. |
| `audioDevice` | `"default"` | Default playback device, or an exact name or ID from `doctor`. |
| `localAddress` | `null` | Use the route to the speaker, or set a local LAN IPv4 address. |
| `streamPort` | `0` | Choose a free port, or set a fixed port. |
| `bitrateKbps` | `192` | MP3 bitrate. Supports 96, 128, 160, 192, 224, 256 and 320. |
| `startupTimeoutSeconds` | `90` | Startup deadline, between 10 and 300 seconds. |
| `logLevel` | `"info"` | Use `"debug"` for protocol details and exception stacks. |

The app leaves the Windows output setting alone and keeps the stream alive
during silence. It follows default-device changes and retries a missing device
for 30 seconds. Playback in an existing multiroom zone affects the whole zone.
The HTTP audio stream has no authentication. Keep it on a trusted LAN.

## Latency

MP3 now reaches the encoder in 10 ms blocks, without the file writer's one-second
input buffer. Capture and HTTP queues stay bounded, and the audio clock catches
up when Windows delays a timer tick.

The sender produces its first MP3 output after 30 to 40 ms of input instead of
1,000 ms. This does not measure speaker latency. The controlled acoustic test
still measured about 3.3 seconds through the Stereo M. Earlier sub-second results
were ambiguous because the microphone also heard the local output.

To repeat the measurement, put the default Windows microphone near the speaker
and run this while streaming:

```powershell
.\raumfeld.ps1 latency -Microphone
```

This briefly mutes the Raumfeld output to identify local leakage, restores its
mute state, then compares a test chirp's loopback and microphone timing.
The microphone runs only during this command. Audio stays in memory and is not
saved. The command excludes local leakage when looking for the speaker signal.
Normal streaming captures playback audio only.

## Troubleshooting

Run `doctor` and check `logs`. Log files live in
`%LOCALAPPDATA%\Raumfeld.DesktopStream\raumfeld.log`.

- If discovery fails, check LAN isolation and multicast access, or set
  `raumfeldHost`. Discovery includes unassigned rooms and rooms in standby.
- If the receiver never connects, allow `Raumfeld.DesktopStream.exe` through
  Windows Firewall on your Private network. A local HTTP check cannot prove
  that the speaker can reach the stream.
- If the wrong adapter is selected, set `localAddress` to the PC's LAN address.
  Automatic selection excludes recognized virtual and VPN adapters.
- If capture fails, choose an available playback device from `doctor`. Keep the
  native LAME DLLs beside the executable.
- If a fixed port is occupied, set `streamPort` to `0`.

Exit codes are `0` for success, `1` for an operation failure, `2` for invalid
arguments and `3` when `status` finds no worker.

## Development

The code follows [Microsoft's C# conventions](https://learn.microsoft.com/dotnet/csharp/fundamentals/coding-style/coding-conventions).
`.editorconfig` defines the rules. Builds treat style violations and warnings as
errors. CI checks C# and PowerShell formatting before running tests.

```powershell
.\raumfeld.ps1 format
.\raumfeld.ps1 check
.\raumfeld.ps1 test
.\raumfeld.ps1 test -AudioSmoke
```

The first format or check downloads the pinned PowerShell analyzer into `.tools`.
Tests cover MP3 encoding, HTTP streaming, room and zone parsing, SOAP commands,
playback ownership and worker control. They use a mock receiver.
`-AudioSmoke` also captures the actual Windows output without changing speaker
playback.

The Stereo M has been tested through discovery, zone creation, playback state,
HTTP consumption, acoustic latency and repeated start/stop. Long-session
stability still needs testing.

Original code uses the [MIT license](LICENSE). Dependency licenses and the
native encoder's redistribution requirements are listed in [licenses](licenses/README.md).
