# Polyscreen

Split one large monitor into virtual "screens". Define zones, assign windows to them, and each
window is made borderless and kept clamped to its zone — even when it tries to go fullscreen.
Fullscreen video fills its zone instead of the whole monitor, and a game in windowed or
borderless mode stays pinned to its zone as if it were running on its own display.

No display drivers, no added latency, no changes to your display configuration — a watchdog
re-applies the window's position the instant an app tries to move, maximize, or fullscreen
itself. Releasing a window restores its original border and position.

The focused clamped window goes always-on-top so it covers the taskbar, just like a real
fullscreen app; it drops back the moment focus moves elsewhere. Toggleable in the tray menu
("Focused window covers taskbar") or with `ontop on|off`.

## Requirements

- Windows 10/11
- [.NET 10 SDK](https://dotnet.microsoft.com/download) to build

## Build & run

```powershell
dotnet build src/Polyscreen/Polyscreen.csproj -c Release
src\Polyscreen\bin\Release\net10.0-windows\Polyscreen.exe
```

Runs as a tray icon. Start it with no arguments; run it *with* arguments to talk to the running
instance (see CLI below).

## Hotkeys

| Hotkey | Action |
|---|---|
| `Ctrl+Alt+1..9` | Assign the focused window to zone 1..9 of the active layout |
| `Ctrl+Alt+0` | Release the focused window (restores border and position) |
| `Ctrl+Alt+Z` | Cycle zone overlays: Forced Zones → Quick Zones → hidden (disabled features are skipped; also: double-click the tray icon) |
| `Ctrl+Alt+B` | Black out / restore the zone under the mouse cursor |
| `Ctrl+Alt+Esc` | Panic reset: release all windows, restore all blackouts |

## CLI

The exe doubles as a command-line client for scripting:

```powershell
Polyscreen.exe list                    # zones + assigned windows
Polyscreen.exe assign left notepad     # match by process name or title substring
Polyscreen.exe assign right "YouTube"
Polyscreen.exe release notepad         # or: release all
Polyscreen.exe layout thirds           # switch layout; bare "layout" lists them
Polyscreen.exe layout delete thirds    # delete a layout (also in the tray's Delete layout menu)
Polyscreen.exe blackout left           # toggle a black panel over a zone ("blackout off" restores all)
Polyscreen.exe edit Coding             # edit a layout (bare "edit" = active, "edit new" = create)
Polyscreen.exe zones                   # cycle overlays: forced -> quick -> hidden
Polyscreen.exe forcedzones off         # disable clamping and release all windows
Polyscreen.exe reset                   # release all windows and blackouts
Polyscreen.exe startup on              # run when Windows starts (also in the tray menu)
Polyscreen.exe ontop off               # focused window no longer covers the taskbar (default: on)
Polyscreen.exe quickzones layout wide  # choose the Shift+drag snapping layout
Polyscreen.exe reload                  # re-read config.json
Polyscreen.exe quit
```

## Quick Zones (Shift+drag)

FancyZones-style snapping built in: hold **Shift** while dragging any window and a zone overlay
appears; drop the window into a zone to snap it there. This is a one-time move — the window keeps
its border and is not clamped — so it coexists cleanly with enforced windows.

Quick Zones has its own layout, fully independent of the Forced Zones layout — pick it in the
tray's "Quick Zones" submenu or with `quickzones layout <name>`. Both selections can use the
same layout or different ones. Disable the feature entirely with the same submenu or
`quickzones off`.

## Layout editor

The tray menu has "Create layout…" (blank canvas), "Edit layout" (pick one), and "Delete layout"
(pick one, with confirmation); `Polyscreen.exe edit [name|new]` does the same from the CLI.
The editor is fullscreen and FancyZones-style: **click** a zone to split it vertically at the
cursor, **Shift+click** to split
horizontally, **Ctrl+click** to rename a zone, **drag** a shared border to resize (snaps to
halves, thirds, and quarters),
**right-click** a zone to remove it, **Enter** to save under a name, **Esc** to cancel. Zones live
in a binary split tree, so the layout always tiles the screen exactly. Saving with an existing
layout name overwrites it; a new name creates a new layout and switches to it.

Press **T** in the editor to toggle **"Over taskbar"**, stored per layout. With it off (default)
the design canvas is the taskbar-free work area — the taskbar strip is hatched out — so zone
sizes are exact: split a 1392px-tall work area into four rows and every row is truly 348px, with
no hidden clipping later. With it on, zones are designed against the full screen and extend
behind the taskbar. Zones keep their proportions when toggling, and every zone label shows the
real size a window will get.

## Blacking out a zone

Any zone can be covered with a pure-black, click-proof, never-focused panel — like turning that
"screen" off (on OLED panels the pixels are literally off). Toggle it from the tray menu, with
`Ctrl+Alt+B` (zone under the mouse), or via the CLI. Double-click a blacked-out zone to restore
it.

## Config

`%APPDATA%\Polyscreen\config.json` — created on first run with `halves` and `thirds` layouts
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
traffic is written to `%APPDATA%\Polyscreen\log.txt`.

## Limitations

- **Exclusive-fullscreen games bypass the desktop compositor entirely** and can't be clamped.
  Set the game to *windowed* or *borderless* mode in its video settings — Polyscreen strips the
  border and handles the rest. Most modern games default to borderless anyway.
- **Elevated (admin) windows** can't be moved by a non-elevated process. Run Polyscreen as
  administrator if you need to clamp them.
- A clamped game renders at the zone's resolution, which is usually what you want — but a few
  stubborn engines re-fullscreen themselves in a loop; the watchdog wins, but if it flickers,
  prefer the game's own windowed mode.
