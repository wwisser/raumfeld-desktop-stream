# Raumklang Desktop Stream

Stream Windows system audio to Teufel Raumfeld speakers over your LAN.
The app captures your playback device through WASAPI loopback, encodes MP3 and
serves it over HTTP. It discovers the room, creates a playback zone if needed,
and starts the speaker. SWYH, Node.js and Raumserver are not required.

## Build and run

You need Windows 11 x64, an active playback device and a Raumfeld speaker on the
same LAN. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
to build. The launcher also detects a local SDK in `.tools/dotnet`.

```powershell
.\raumklang.ps1 build
.\raumklang.ps1 doctor
.\raumklang.ps1 start
.\raumklang.ps1 status
.\raumklang.ps1 stop
```

`start` runs in the background and waits until the receiver plays the stream and
fetches audio bytes. Repeated starts reuse the worker. `stop` releases audio and
network resources. It stops speaker playback only when the speaker still has
this app's stream loaded.

```powershell
.\raumklang.ps1 logs
.\raumklang.ps1 logs -Follow
.\raumklang.ps1 run              # Foreground output. Ctrl+C stops the app.
.\raumklang.ps1 publish
```

Publish produces `artifacts/publish/Raumklang.DesktopStream.exe` with its .NET
runtime. Copy the whole folder to another Windows x64 machine. The executable
accepts `start`, `stop`, `status`, `doctor`, `logs` and `run`.
The launcher prefers the published copy, so republish after changing source code.

## Configuration

One room needs no configuration. With several rooms, set `roomName` to the name
or UDN shown by `doctor`.

```powershell
Copy-Item raumklang.example.json raumklang.local.json
# Edit raumklang.local.json, then start.
.\raumklang.ps1 start
```

The launcher reads `raumklang.local.json` when present. You can also pass
`-Config <path>`. Otherwise, the app looks for
`%LOCALAPPDATA%\Raumklang.DesktopStream\config.json`.
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

## Troubleshooting

Run `doctor` and check `logs`. Log files live in
`%LOCALAPPDATA%\Raumklang.DesktopStream\raumklang.log`.

- If discovery fails, check LAN isolation and multicast access, or set
  `raumfeldHost`. Discovery includes unassigned rooms and rooms in standby.
- If the receiver never connects, allow `Raumklang.DesktopStream.exe` through
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
.\raumklang.ps1 format
.\raumklang.ps1 check
.\raumklang.ps1 test
.\raumklang.ps1 test -AudioSmoke
```

The first format or check downloads the pinned PowerShell analyzer into `.tools`.
Tests cover MP3 encoding, HTTP streaming, room and zone parsing, SOAP commands,
playback ownership and worker control. They use a mock receiver.
`-AudioSmoke` also captures the actual Windows output without changing speaker
playback.

The Stereo M has been tested through discovery, zone creation, playback state,
HTTP consumption and repeated start/stop. Audible latency and long-session
stability still need measurement.

Original code uses the [MIT license](LICENSE). Dependency licenses and the
native encoder's redistribution requirements are listed in [licenses](licenses/README.md).
