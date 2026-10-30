# Hue Tray

Hue Tray is a small app that controls Philips Hue lights from the Windows notification area or the macOS menu bar. It can recall scenes and switch, dim and tint rooms, zones and single lights. Other programs, such as [jog-scroll](https://github.com/hazeledmands/jog-scroll) with a DJ controller, scripts or a Stream Deck, can drive it through a local HTTP API. Both versions share everything except the menu code.

How it behaves:
- On first run it finds your Hue Bridge and asks you to press the bridge's round link button. That pairs it, and it remembers the bridge from then on.
- The menu lists **Scenes** grouped by room. Below that is a submenu for each room and zone, with On, brightness presets, warm/neutral/cool white, and a submenu for each light in it.
- It follows changes made anywhere else (the Hue app, switches, other programs) as they happen.
- Changes go to the bridge no faster than it accepts them: about 10 a second for single lights and 1 a second for rooms and zones. A burst of changes to the same thing, such as a fast jog spin, is merged into the latest value. Brightness steps (`+5`) count from the newest requested level, so a burst adds up correctly even though the bridge sees only a few commands.
- Dimming to 0 turns a light off. Raising the brightness of a light that's off turns it on.

## Install (Windows)

You'll need the .NET 10 SDK (`winget install Microsoft.DotNet.SDK.10`). From the repo root, run:

```
powershell -ExecutionPolicy Bypass -File windows\install.ps1
```

This builds Hue Tray (into `build\windows`), copies it to `%LOCALAPPDATA%\Programs\HueTray`, sets it to start at login and launches it. To build without installing, run `windows\build.ps1`. Windows won't run these scripts from a WSL folder (`\\wsl.localhost\...`), so copy or clone the repo onto a Windows drive first.

The bulb icon is filled while any light is on, and faded while the bridge can't be reached. Left or right click opens the menu. You may need to drag the icon out of the hidden-icons area (^) so it stays visible.

## Install (macOS)

You'll need macOS 13 or later, the .NET 10 SDK (`brew install --cask dotnet-sdk`) and Xcode or its Command Line Tools (`xcode-select --install`). From the repo root, run:

```
macos/install.sh
```

This builds Hue Tray (into `build/macos`), copies it to `~/Applications/Hue Tray.app`, sets it to start at login and launches it. To build without installing, run `macos/build.sh`. It builds for your Mac's processor. The app is signed for your Mac only. A copy downloaded from a CI run is blocked by Gatekeeper until you run `xattr -dr com.apple.quarantine "Hue Tray.app"`.

The bulb is in the menu bar. It's filled while any light is on, and faded while the bridge can't be reached. The first time it looks for the bridge, macOS asks whether Hue Tray may find devices on your local network. Say yes, or it can't reach the bridge. (To change your answer later, go to System Settings > Privacy & Security > Local Network.) Notifications, such as "Press the link button", need permission too.

The menu is Swift (`macos/HueTray`). The bridge connection and the API are the same C# code as on Windows. It runs as a helper, `hue-tray run`, which the app starts and stops with itself. The menu reads and changes the lights through the API like any other client. The helper's log is in `~/Library/Logs/Hue Tray.log`.

## Settings

Settings are in `%APPDATA%\hue-tray\config.json` on Windows and `~/Library/Application Support/hue-tray/config.json` on macOS. Changes take effect within a couple of seconds. **Open Settings File** in the menu opens it.

| key | default | meaning |
|---|---|---|
| `bridgeIp`, `bridgeId`, `bridgeName` | | the paired bridge, filled in by pairing |
| `appKey` | | the key the bridge gave Hue Tray. Delete it to pair again |
| `certFingerprint` | | the bridge's certificate, pinned when pairing. Hue Tray only trusts a bridge with that certificate |
| `apiPort` | 8765 | port for the local API (127.0.0.1 only) |
| `apiToken` | random | the API's password, created on first run |
| `menu.rooms` | `[]` | rooms and zones to show, by name, in this order. Empty shows all of them |
| `menu.lights` | `[]` | lights to show on their own, outside their rooms |
| `menu.scenes` | `[]` | scenes to show. Empty shows all of them |
| `lightCommandsPerSecond` | 10 | how fast to send changes to single lights |
| `groupCommandsPerSecond` | 1 | how fast to send changes to rooms, zones and scene recalls. Philips asks for at most 1 |

**All Off** turns off `menu.rooms` and `menu.lights`, or every light if both are empty.

## HTTP API

Hue Tray listens on `http://127.0.0.1:8765/api/v1/`. Every request needs the token: `Authorization: Bearer <apiToken>`. Get it with `hue-tray token`, or **Copy API Token** in the menu. Wherever a path has a `{name}`, you can use the room, zone, light or scene name (any case) or its Hue id.

| request | does |
|---|---|
| `GET /status` | whether the bridge is connected |
| `GET /lights`, `/rooms`, `/zones`, `/scenes` | lists, with current state |
| `POST /scenes/{name}/activate` | recalls a scene. Body (optional): `{"room": "Bedroom", "transitionMs": 2000}`. `room` is needed when several rooms have a scene with that name |
| `PATCH /lights/{name}`, `/rooms/{name}`, `/zones/{name}`, `/home` | changes the light or group (`/home` is every light). Body: any of `{"on": true, "brightness": 50, "colorTemp": 2700, "color": "#ff8800", "transitionMs": 400}` |
| `POST /off` | does the same as All Off |
| `GET /events` | a WebSocket that sends a JSON message whenever something changes |

`brightness` is a percentage (0–100), or a string step like `"+5"` or `"-5"`. `colorTemp` is in kelvin.

Changes are answered at once with the requested state. They reach the bridge as soon as its rate limit allows. Errors come back as `{"error": "..."}`, with 400 for a bad request, 401 for a missing token, 404 for an unknown name, 409 for an ambiguous name, and 503 if Hue Tray isn't paired.

Each `/events` message has a `type`: `light`, `room`, `zone`, `home`, `scene`, `status` or `reload` (everything was reloaded, for example after reconnecting). It also has the `id` and `item`, the new state in the same shape as the lists. Browsers can't set headers on a WebSocket, so `/events` also accepts `?token=`.

```sh
TOKEN=$(hue-tray token)
curl -H "Authorization: Bearer $TOKEN" -X POST localhost:8765/api/v1/scenes/Relax/activate -d '{"room":"Living room"}'
curl -H "Authorization: Bearer $TOKEN" -X PATCH localhost:8765/api/v1/rooms/Living%20room -d '{"brightness":"+5"}'
```

## Terminal commands

The terminal commands are a separate console program. On Windows it's `hue-tray.exe`, next to `HueTray.exe` in `%LOCALAPPDATA%\Programs\HueTray`. On macOS it's `~/Applications/Hue Tray.app/Contents/MacOS/hue-tray`. To put it on your path, link it from a folder on your path, for example `ln -s ~/Applications/"Hue Tray.app"/Contents/MacOS/hue-tray /opt/homebrew/bin/`.

- `hue-tray pair [ip]`: find the bridge (or use the address you give) and pair with it
- `hue-tray discover`: list the bridges on the network
- `hue-tray list`: rooms and zones with their lights and scenes
- `hue-tray scene <name> [room]`: activate a scene
- `hue-tray light|room|zone <name> <value>...`: change it. Values are `on`, `off`, a brightness (`50`, `+10`, `-10`), a colour temperature (`2700K`) or a colour (`#ff8800`)
- `hue-tray off`: All Off
- `hue-tray status`, `hue-tray token`
- `hue-tray run`: connect to the bridge and run the API without the tray icon, logging to the terminal
- `hue-tray login on` / `login off`: start, or stop starting, at login. On Windows this is a "Hue Tray" entry under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`. On macOS it's `~/Library/LaunchAgents/io.github.hazeledmands.huetray.plist`

The commands that change lights go through the tray app's API when it's running, and straight to the bridge when it isn't.

## Repository layout

- `core/`: everything that isn't platform UI, as a .NET library. It has the bridge connection (`Hue/`), the live state (`State.cs`), the rate-limited command queue, the settings file, the HTTP API (`Api/`) and the terminal commands (`Commands.cs`).
- `windows/`: the WinForms tray app, and the Windows build of the terminal commands, with build and install scripts
- `macos/`: the Swift menu bar app (`HueTray/`), the macOS build of the terminal commands, which is also the app's helper (`helper/`), and build and install scripts
- `tests/`: tests for `core`, using a fake bridge. Run them with `dotnet test tests/HueTray.Core.Tests`.

## Plans

- **jog-scroll integration:** add optional mappings to jog-scroll's config, so that chosen MIDI controls call this API. For example, a knob could step a room's brightness and pads could recall scenes. jog-scroll keeps sole ownership of the controller, which matters on Windows versions where only one program can open a MIDI device.
