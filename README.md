# ZoneEnforcer

Turns regions of a super-ultrawide monitor (built for a 5120×1440 Samsung Odyssey G9) into
"virtual monitors" — without display drivers, added latency, or fooling Windows.

Instead of faking extra displays, ZoneEnforcer fools the *apps*: a window assigned to a zone is
made borderless and clamped to that zone, and a WinEvent watchdog instantly re-clamps it whenever
the app tries to go fullscreen, maximize, or move itself. Hitting fullscreen on a YouTube video
fills *your zone*, not the whole panel. A game in borderless/windowed mode stays pinned to its
zone like it's running on its own monitor.

## Build & run

```powershell
dotnet build src/ZoneEnforcer/ZoneEnforcer.csproj -c Release
src\ZoneEnforcer\bin\Release\net10.0-windows\ZoneEnforcer.exe
```

Runs as a tray icon. Start it with no arguments; run it *with* arguments to talk to the running
instance (see CLI below).

## Hotkeys

| Hotkey | Action |
|---|---|
| `Ctrl+Alt+1..9` | Assign the focused window to zone 1..9 of the active layout |
| `Ctrl+Alt+0` | Release the focused window (restores border and position) |
| `Ctrl+Alt+Z` | Flash the zone overlay (also: double-click the tray icon) |
| `Ctrl+Alt+B` | Black out / restore the zone under the mouse cursor |

## CLI

The exe doubles as a command-line client for scripting:

```powershell
ZoneEnforcer.exe list                    # zones + assigned windows
ZoneEnforcer.exe assign left notepad     # match by process name or title substring
ZoneEnforcer.exe assign right "YouTube"
ZoneEnforcer.exe release notepad         # or: release all
ZoneEnforcer.exe layout thirds           # switch layout; bare "layout" lists them
ZoneEnforcer.exe blackout left           # toggle a black panel over a zone ("blackout off" restores all)
ZoneEnforcer.exe edit                    # open the visual layout editor ("edit close" cancels)
ZoneEnforcer.exe zones                   # flash the overlay
ZoneEnforcer.exe reload                  # re-read config.json
ZoneEnforcer.exe quit
```

## Layout editor

"Edit layout…" in the tray menu (or `ZoneEnforcer.exe edit`) opens a fullscreen FancyZones-style
editor: **click** a zone to split it vertically at the cursor, **Shift+click** to split
horizontally, **drag** a shared border to resize (snaps to halves, thirds, and quarters),
**right-click** a zone to remove it, **Enter** to save under a name, **Esc** to cancel. Zones live
in a binary split tree, so the layout always tiles the screen exactly. Saving with an existing
layout name overwrites it; a new name creates a new layout and switches to it.

## Blacking out a zone

Any zone can be covered with a pure-black, click-proof, never-focused panel — on an OLED that
means the pixels are simply off, like the "display" is powered down. Toggle it from the tray menu,
with `Ctrl+Alt+B` (zone under the mouse), or via the CLI. Double-click a blacked-out zone to
restore it.

## Config

`%APPDATA%\ZoneEnforcer\config.json` — created on first run with `halves` and `thirds` layouts
sized to the primary display. Zones are physical pixel rectangles, so you can define any split
you want, including overlapping zones or a centered zone with wings.

```json
{
  "activeLayout": "halves",
  "layouts": {
    "halves": [
      { "name": "left",  "x": 0,    "y": 0, "width": 2560, "height": 1440 },
      { "name": "right", "x": 2560, "y": 0, "width": 2560, "height": 1440 }
    ]
  },
  "autoRules": [
    { "process": "vlc", "zone": "right" },
    { "process": "chrome", "titleContains": "YouTube", "zone": "right" }
  ]
}
```

`autoRules` assign matching windows automatically when they appear. A log of assignments and pipe
traffic is written to `%APPDATA%\ZoneEnforcer\log.txt`.

## Limitations

- **Exclusive-fullscreen games bypass the desktop compositor entirely** and can't be clamped.
  Set the game to *windowed* or *borderless* mode in its video settings — ZoneEnforcer strips the
  border and handles the rest. Most modern games default to borderless anyway.
- **Elevated (admin) windows** can't be moved by a non-elevated process. Run ZoneEnforcer as
  administrator if you need to clamp them.
- A clamped game renders at the zone's resolution (e.g. 2560×1440), which is what you want — but
  a few stubborn engines re-fullscreen themselves in a loop; the watchdog wins, but if it flickers,
  prefer the game's own windowed mode.
