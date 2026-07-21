using System.Text;

namespace Polyscreen;

/// <summary>Hidden window that owns the global hotkeys and serves as the UI-thread invoke target.</summary>
public class MarshalForm : Form
{
    public event Action<int>? Hotkey;

    public MarshalForm()
    {
        ShowInTaskbar = false;
        WindowState = FormWindowState.Minimized;
        FormBorderStyle = FormBorderStyle.None;
        Opacity = 0;
        _ = Handle; // force handle creation without showing the form
    }

    protected override bool ShowWithoutActivation => true;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY)
            Hotkey?.Invoke(m.WParam.ToInt32());
        base.WndProc(ref m);
    }
}

public class TrayContext : ApplicationContext
{
    private const int HotkeyReleaseId = 10;
    private const int HotkeyZonesId = 11;
    private const int HotkeyBlackoutId = 12;
    private const int HotkeyPanicId = 13;

    private readonly Engine _engine;
    private readonly MarshalForm _marshal;
    private readonly NotifyIcon _tray;
    private readonly PipeServer _pipe;
    private readonly Dictionary<string, BlackoutForm> _blackouts = new(StringComparer.OrdinalIgnoreCase);
    private readonly QuickZones _quickZones;
    private readonly List<Form> _zoneOverlays = new();
    private int _overlayState; // 0 = hidden, 1 = forced zones shown, 2 = quick zones shown
    private LayoutEditorForm? _editor;

    public TrayContext()
    {
        _engine = new Engine(Config.Load());
        _quickZones = new QuickZones(_engine);
        _marshal = new MarshalForm();
        _marshal.Hotkey += OnHotkey;
        RegisterHotkeys();

        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Polyscreen",
            Visible = true,
            ContextMenuStrip = new ContextMenuStrip(),
        };
        _tray.ContextMenuStrip.Opening += (_, _) => RebuildMenu();
        _tray.ContextMenuStrip.Closing += KeepOpenOnItemClick;
        _tray.DoubleClick += (_, _) => CycleZoneOverlay();

        _pipe = new PipeServer(_marshal, HandleCommand);
        StartupManager.HealPathIfStale();
        Log.Write($"started, layout '{_engine.Config.ActiveLayout}', config {Config.ConfigPath}");
    }

    private void RegisterHotkeys()
    {
        uint mods = Native.MOD_CONTROL | Native.MOD_ALT;
        for (int i = 1; i <= 9; i++)
            Native.RegisterHotKey(_marshal.Handle, i, mods, (uint)('0' + i)); // Ctrl+Alt+1..9: assign
        Native.RegisterHotKey(_marshal.Handle, HotkeyReleaseId, mods, '0');   // Ctrl+Alt+0: release
        Native.RegisterHotKey(_marshal.Handle, HotkeyZonesId, mods, 'Z');     // Ctrl+Alt+Z: show zones
        Native.RegisterHotKey(_marshal.Handle, HotkeyBlackoutId, mods, 'B');  // Ctrl+Alt+B: black out zone under cursor
        if (!Native.RegisterHotKey(_marshal.Handle, HotkeyPanicId, mods, (uint)Keys.Escape)) // Ctrl+Alt+Esc: reset everything
            Log.Write("failed to register Ctrl+Alt+Esc (in use by another app)");
    }

    /// <summary>Panic reset: release every window, restore every blacked-out zone, close the editor.</summary>
    private void ResetAll()
    {
        CloseAllBlackouts();
        CloseZoneOverlays();
        _engine.ReleaseAll();
        _editor?.Close();
    }

    private void CloseZoneOverlays()
    {
        foreach (var f in _zoneOverlays) f.Close();
        _zoneOverlays.Clear();
        _overlayState = 0;
    }

    /// <summary>Ctrl+Alt+Z: forced zones → quick zones → hidden; disabled features are skipped.</summary>
    private string CycleZoneOverlay()
    {
        var cfg = _engine.Config;
        int next = _overlayState;
        do
        {
            next = (next + 1) % 3;
        } while ((next == 1 && !cfg.ForcedZonesEnabled) || (next == 2 && !cfg.QuickZonesEnabled));

        CloseZoneOverlays();
        _overlayState = next;

        switch (next)
        {
            case 1:
                _zoneOverlays.AddRange(OverlayForm.ShowPersistent(cfg.ActiveZones,
                    $"Forced Zones — {cfg.ActiveLayout}"));
                return $"showing forced zones ({cfg.ActiveLayout})";
            case 2:
                _zoneOverlays.AddRange(OverlayForm.ShowPersistent(cfg.QuickZones,
                    $"Quick Zones — {cfg.QuickZonesLayout}"));
                return $"showing quick zones ({cfg.QuickZonesLayout})";
            default:
                return "zone overlays hidden";
        }
    }

    private void OnHotkey(int id)
    {
        if (id == HotkeyZonesId)
        {
            CycleZoneOverlay();
            return;
        }

        if (id == HotkeyPanicId)
        {
            ResetAll();
            Notify("Released all windows and blackouts");
            return;
        }

        if (id == HotkeyBlackoutId)
        {
            Native.GetCursorPos(out var pt);
            var z = _engine.Config.ActiveZones.FirstOrDefault(z => z.Contains(pt.X, pt.Y));
            if (z != null) ToggleBlackout(z);
            return;
        }

        var hwnd = Native.GetForegroundWindow();
        if (hwnd == IntPtr.Zero || hwnd == _marshal.Handle) return;

        if (id == HotkeyReleaseId)
        {
            if (_engine.Release(hwnd))
                Notify($"Released: {Native.GetWindowTitle(hwnd)}");
            return;
        }

        if (!_engine.Config.ForcedZonesEnabled)
        {
            Notify("Forced Zones is disabled (enable it in the tray menu)");
            return;
        }

        var zones = _engine.Config.ActiveZones;
        if (id < 1 || id > zones.Count)
        {
            Notify($"No zone {id} in layout '{_engine.Config.ActiveLayout}' ({zones.Count} zones)");
            return;
        }
        var zone = zones[id - 1];
        if (_engine.Assign(hwnd, zone))
            Notify($"{Native.GetWindowTitle(hwnd)} → {zone.Name}");
    }

    private void ToggleBlackout(Zone zone)
    {
        if (_blackouts.Remove(zone.Name, out var form))
        {
            form.Close();
            return;
        }
        var f = new BlackoutForm(zone);
        f.FormClosed += (_, _) => _blackouts.Remove(f.ZoneName);
        _blackouts[zone.Name] = f;
        f.Show();
    }

    private void CloseAllBlackouts()
    {
        foreach (var f in _blackouts.Values.ToList()) f.Close();
        _blackouts.Clear();
    }

    /// <summary>Open the visual editor for a layout, or with a blank canvas when null (create).</summary>
    private void OpenEditor(string? layoutName)
    {
        if (_editor != null && !_editor.IsDisposed)
        {
            _editor.Activate();
            return;
        }
        CloseAllBlackouts();
        CloseZoneOverlays();
        var screen = Screen.PrimaryScreen!.Bounds;
        LayoutDef? def = null;
        if (layoutName != null) _engine.Config.Layouts.TryGetValue(layoutName, out def);
        _editor = new LayoutEditorForm(screen, layoutName ?? "", def?.Zones ?? new List<Zone>(),
            def?.OverTaskbar ?? false,
            (name, zones, overTaskbar) =>
            {
                _engine.Config.Layouts[name] = new LayoutDef { Zones = zones, OverTaskbar = overTaskbar };
                if (name.Equals(_engine.Config.ActiveLayout, StringComparison.OrdinalIgnoreCase))
                    _engine.SetLayout(_engine.Config.ActiveLayout); // re-clamp assigned windows to the new zones
                else
                    _engine.Config.Save();
                OverlayForm.Flash(zones);
            });
        _editor.FormClosed += (_, _) => _editor = null;
        _editor.Show();
    }

    private void Notify(string text)
    {
        _tray.BalloonTipTitle = "Polyscreen";
        _tray.BalloonTipText = text;
        _tray.ShowBalloonTip(1500);
    }

    /// <summary>
    /// Clicking a toggle shouldn't dismiss the menu — so item clicks never auto-close it.
    /// Actions that open something else (editor, config, exit) call CloseMenu explicitly,
    /// and clicking outside or pressing Esc still closes normally.
    /// </summary>
    private static void KeepOpenOnItemClick(object? sender, ToolStripDropDownClosingEventArgs e)
    {
        if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked) e.Cancel = true;
    }

    private void CloseMenu() => _tray.ContextMenuStrip!.Close(ToolStripDropDownCloseReason.CloseCalled);

    /// <summary>Parse an on/off CLI argument; null if it is neither.</summary>
    private static bool? ParseToggle(string s) =>
        s.Equals("on", StringComparison.OrdinalIgnoreCase) ? true
        : s.Equals("off", StringComparison.OrdinalIgnoreCase) ? false
        : null;

    private static string OnOff(bool b) => b ? "on" : "off";

    /// <summary>Refresh the radio checkmarks of sibling items sharing a Tag, in place.</summary>
    private static void RefreshRadio(ToolStripMenuItem parent, string tag, string checkedText)
    {
        foreach (ToolStripItem it in parent.DropDownItems)
            if (it is ToolStripMenuItem mi && Equals(mi.Tag, tag))
                mi.Checked = mi.Text!.Equals(checkedText, StringComparison.OrdinalIgnoreCase);
    }

    private void RebuildMenu()
    {
        var menu = _tray.ContextMenuStrip!;
        menu.Items.Clear();

        menu.Items.Add(new ToolStripMenuItem("Show zones  (Ctrl+Alt+Z cycles)", null,
            (_, _) => CycleZoneOverlay()));

        var layoutMenu = new ToolStripMenuItem("Forced Zones");
        layoutMenu.DropDown.Closing += KeepOpenOnItemClick;
        var fzEnabled = new ToolStripMenuItem("Enabled") { Checked = _engine.Config.ForcedZonesEnabled };
        fzEnabled.Click += (_, _) =>
        {
            _engine.SetForcedZonesEnabled(!_engine.Config.ForcedZonesEnabled);
            CloseZoneOverlays();
            fzEnabled.Checked = _engine.Config.ForcedZonesEnabled;
        };
        layoutMenu.DropDownItems.Add(fzEnabled);
        layoutMenu.DropDownItems.Add(new ToolStripSeparator());
        foreach (var name in _engine.Config.Layouts.Keys)
        {
            var item = new ToolStripMenuItem(name, null, (_, _) =>
            {
                CloseAllBlackouts();
                CloseZoneOverlays();
                _engine.SetLayout(name);
                RefreshRadio(layoutMenu, "fz-layout", _engine.Config.ActiveLayout);
            })
            {
                Checked = name == _engine.Config.ActiveLayout,
                Tag = "fz-layout",
            };
            layoutMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(layoutMenu);

        var qzMenu = new ToolStripMenuItem("Quick Zones  (Shift+drag)");
        qzMenu.DropDown.Closing += KeepOpenOnItemClick;
        var qzEnabled = new ToolStripMenuItem("Enabled") { Checked = _engine.Config.QuickZonesEnabled };
        qzEnabled.Click += (_, _) =>
        {
            _engine.SetQuickZonesEnabled(!_engine.Config.QuickZonesEnabled);
            CloseZoneOverlays();
            qzEnabled.Checked = _engine.Config.QuickZonesEnabled;
        };
        qzMenu.DropDownItems.Add(qzEnabled);
        qzMenu.DropDownItems.Add(new ToolStripSeparator());
        foreach (var name in _engine.Config.Layouts.Keys)
        {
            qzMenu.DropDownItems.Add(new ToolStripMenuItem(name, null, (_, _) =>
            {
                _engine.Config.QuickZonesLayout = name;
                _engine.Config.Save();
                RefreshRadio(qzMenu, "qz-layout", name);
            })
            {
                Checked = name.Equals(_engine.Config.QuickZonesLayout, StringComparison.OrdinalIgnoreCase),
                Tag = "qz-layout",
            });
        }
        menu.Items.Add(qzMenu);

        menu.Items.Add(new ToolStripMenuItem("Create layout…", null, (_, _) =>
        {
            CloseMenu();
            OpenEditor(null);
        }));

        var editMenu = new ToolStripMenuItem("Edit layout");
        foreach (var name in _engine.Config.Layouts.Keys)
            editMenu.DropDownItems.Add(new ToolStripMenuItem(name, null, (_, _) =>
            {
                CloseMenu();
                OpenEditor(name);
            }));
        menu.Items.Add(editMenu);

        var deleteMenu = new ToolStripMenuItem("Delete layout")
        {
            Enabled = _engine.Config.Layouts.Count > 1,
        };
        foreach (var name in _engine.Config.Layouts.Keys)
        {
            deleteMenu.DropDownItems.Add(new ToolStripMenuItem(name, null, (_, _) =>
            {
                CloseMenu();
                if (MessageBox.Show($"Delete layout '{name}'?", "Polyscreen",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                bool wasActive = name.Equals(_engine.Config.ActiveLayout, StringComparison.OrdinalIgnoreCase);
                if (wasActive) CloseAllBlackouts();
                var error = _engine.DeleteLayout(name);
                if (error != null) Notify(error);
                else Notify(wasActive
                    ? $"Deleted '{name}', switched to '{_engine.Config.ActiveLayout}'"
                    : $"Deleted '{name}'");
            }));
        }
        menu.Items.Add(deleteMenu);

        var blackoutMenu = new ToolStripMenuItem("Black out zone  (Ctrl+Alt+B)");
        blackoutMenu.DropDown.Closing += KeepOpenOnItemClick;
        foreach (var zone in _engine.Config.ActiveZones)
        {
            var z = zone;
            var item = new ToolStripMenuItem(z.Name) { Checked = _blackouts.ContainsKey(z.Name), Tag = "bo" };
            item.Click += (_, _) =>
            {
                ToggleBlackout(z);
                item.Checked = _blackouts.ContainsKey(z.Name);
            };
            blackoutMenu.DropDownItems.Add(item);
        }
        blackoutMenu.DropDownItems.Add(new ToolStripSeparator());
        blackoutMenu.DropDownItems.Add(new ToolStripMenuItem("Restore all", null, (_, _) =>
        {
            CloseAllBlackouts();
            foreach (ToolStripItem it in blackoutMenu.DropDownItems)
                if (it is ToolStripMenuItem mi && Equals(mi.Tag, "bo")) mi.Checked = false;
        }));
        menu.Items.Add(blackoutMenu);

        var assigned = new ToolStripMenuItem("Assigned windows");
        assigned.DropDown.Closing += KeepOpenOnItemClick;
        foreach (var a in _engine.Assignments.Values)
        {
            var title = Native.GetWindowTitle(a.Hwnd);
            if (title.Length > 40) title = title[..40] + "…";
            var hwnd = a.Hwnd;
            var item = new ToolStripMenuItem($"{title}  [{a.Zone.Name}]  — click to release");
            item.Click += (_, _) =>
            {
                if (_engine.Release(hwnd))
                {
                    item.Text = $"{title}  (released)";
                    item.Enabled = false;
                }
            };
            assigned.DropDownItems.Add(item);
        }
        if (assigned.DropDownItems.Count == 0)
        {
            assigned.DropDownItems.Add(new ToolStripMenuItem("(none — focus a window, press Ctrl+Alt+1..9)")
            {
                Enabled = false,
            });
        }
        menu.Items.Add(assigned);

        menu.Items.Add(new ToolStripMenuItem("Release all  (Ctrl+Alt+Esc)", null, (_, _) => ResetAll()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Reload config", null, (_, _) =>
        {
            CloseMenu();
            CloseAllBlackouts();
            CloseZoneOverlays();
            _engine.ReloadConfig();
        }));
        menu.Items.Add(new ToolStripMenuItem("Open config file", null, (_, _) =>
        {
            CloseMenu();
            System.Diagnostics.Process.Start("notepad.exe", Config.ConfigPath);
        }));
        var ontopItem = new ToolStripMenuItem("Focused window covers taskbar")
        {
            Checked = _engine.Config.TopmostOnFocus,
        };
        ontopItem.Click += (_, _) =>
        {
            _engine.SetTopmostOnFocus(!_engine.Config.TopmostOnFocus);
            ontopItem.Checked = _engine.Config.TopmostOnFocus;
        };
        menu.Items.Add(ontopItem);
        var startupItem = new ToolStripMenuItem("Run at startup") { Checked = StartupManager.IsEnabled };
        startupItem.Click += (_, _) =>
        {
            if (StartupManager.IsEnabled) StartupManager.Disable();
            else StartupManager.Enable();
            startupItem.Checked = StartupManager.IsEnabled;
        };
        menu.Items.Add(startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) =>
        {
            CloseMenu();
            ExitThread();
        }));
    }

    public string HandleCommand(string[] args)
    {
        if (args.Length == 0) return HelpText();
        switch (args[0].ToLowerInvariant())
        {
            case "assign":
            {
                if (args.Length < 3) return "usage: assign <zone> <process-or-title>";
                if (!_engine.Config.ForcedZonesEnabled)
                    return "forced zones are disabled (enable with: forcedzones on)";
                var zone = _engine.Config.FindZone(args[1]);
                if (zone == null)
                    return $"unknown zone '{args[1]}'. zones: {string.Join(", ", _engine.Config.ActiveZones.Select(z => z.Name))}";
                var match = string.Join(' ', args[2..]);
                var win = WindowFinder.Find(match);
                if (win == null) return $"no window matching '{match}'";
                return _engine.Assign(win.Hwnd, zone)
                    ? $"assigned \"{win.Title}\" ({win.Process}) -> {zone}"
                    : $"could not assign \"{win.Title}\"";
            }
            case "release":
            {
                if (args.Length < 2) return "usage: release <process-or-title> | release all";
                var match = string.Join(' ', args[1..]);
                if (match.Equals("all", StringComparison.OrdinalIgnoreCase))
                {
                    int n = _engine.Assignments.Count;
                    _engine.ReleaseAll();
                    return $"released {n} window(s)";
                }
                foreach (var a in _engine.Assignments.Values.ToList())
                {
                    var title = Native.GetWindowTitle(a.Hwnd);
                    var process = Native.GetProcessName(a.Hwnd);
                    if (title.Contains(match, StringComparison.OrdinalIgnoreCase) ||
                        process.Equals(match, StringComparison.OrdinalIgnoreCase))
                    {
                        _engine.Release(a.Hwnd);
                        return $"released \"{title}\"";
                    }
                }
                return $"no assigned window matching '{match}'";
            }
            case "list":
            {
                var sb = new StringBuilder();
                sb.AppendLine($"forced zones layout: {_engine.Config.ActiveLayout}" +
                              (_engine.Config.ForcedZonesEnabled ? "" : " (disabled)"));
                foreach (var z in _engine.Config.ActiveZones) sb.AppendLine($"  zone {z}");
                sb.AppendLine($"quick zones layout: {_engine.Config.QuickZonesLayout}" +
                              (_engine.Config.QuickZonesEnabled ? "" : " (disabled)"));
                sb.AppendLine($"assigned: {_engine.Assignments.Count}");
                foreach (var a in _engine.Assignments.Values)
                {
                    Native.GetWindowRect(a.Hwnd, out var r);
                    sb.AppendLine($"  \"{Native.GetWindowTitle(a.Hwnd)}\" ({Native.GetProcessName(a.Hwnd)}) -> {a.Zone.Name} @ {r}");
                }
                if (_blackouts.Count > 0)
                    sb.AppendLine($"blacked out: {string.Join(", ", _blackouts.Keys)}");
                return sb.ToString();
            }
            case "layout":
            {
                if (args.Length < 2)
                    return "layouts: " + string.Join(", ",
                        _engine.Config.Layouts.Select(kv =>
                            kv.Key
                            + (kv.Key == _engine.Config.ActiveLayout ? " (active)" : "")
                            + (kv.Value.OverTaskbar ? " [over taskbar]" : "")));
                if (args.Length >= 3 &&
                    (args[1].Equals("delete", StringComparison.OrdinalIgnoreCase) ||
                     args[1].Equals("remove", StringComparison.OrdinalIgnoreCase)))
                {
                    bool wasActive = args[2].Equals(_engine.Config.ActiveLayout, StringComparison.OrdinalIgnoreCase);
                    if (wasActive) CloseAllBlackouts();
                    var error = _engine.DeleteLayout(args[2]);
                    if (error != null) return error;
                    return wasActive
                        ? $"deleted '{args[2]}', active layout -> {_engine.Config.ActiveLayout}"
                        : $"deleted '{args[2]}'";
                }
                if (!_engine.Config.Layouts.ContainsKey(args[1])) return $"unknown layout '{args[1]}'";
                CloseAllBlackouts();
                CloseZoneOverlays();
                _engine.SetLayout(args[1]);
                return $"layout -> {args[1]}";
            }
            case "blackout":
            {
                if (args.Length < 2) return "usage: blackout <zone> | blackout off";
                if (args[1].Equals("off", StringComparison.OrdinalIgnoreCase))
                {
                    CloseAllBlackouts();
                    return "all zones restored";
                }
                var zone = _engine.Config.FindZone(args[1]);
                if (zone == null)
                    return $"unknown zone '{args[1]}'. zones: {string.Join(", ", _engine.Config.ActiveZones.Select(z => z.Name))}";
                ToggleBlackout(zone);
                return _blackouts.ContainsKey(zone.Name)
                    ? $"zone '{zone.Name}' blacked out"
                    : $"zone '{zone.Name}' restored";
            }
            case "edit":
            {
                if (args.Length > 1 && args[1].Equals("close", StringComparison.OrdinalIgnoreCase))
                {
                    _editor?.Close();
                    return "editor closed";
                }
                if (args.Length > 1 && args[1].Equals("new", StringComparison.OrdinalIgnoreCase))
                {
                    OpenEditor(null);
                    return "layout editor opened with a blank canvas (Enter saves, Esc cancels)";
                }
                string? target = args.Length > 1
                    ? _engine.Config.Layouts.Keys.FirstOrDefault(k =>
                        k.Equals(string.Join(' ', args[1..]), StringComparison.OrdinalIgnoreCase))
                    : _engine.Config.ActiveLayout;
                if (target == null) return $"unknown layout '{string.Join(' ', args[1..])}'";
                OpenEditor(target);
                return $"layout editor opened for '{target}' (Enter saves, Esc cancels)";
            }
            case "zones":
                return CycleZoneOverlay();
            case "forcedzones":
            {
                if (args.Length < 2)
                    return $"forced zones: {OnOff(_engine.Config.ForcedZonesEnabled)}, " +
                           $"layout: {_engine.Config.ActiveLayout} (usage: forcedzones on|off)";
                if (ParseToggle(args[1]) is not bool on) return "usage: forcedzones on|off";
                _engine.SetForcedZonesEnabled(on);
                CloseZoneOverlays();
                return on ? "forced zones: on" : "forced zones: off (all windows released)";
            }
            case "quickzones":
            {
                if (args.Length < 2)
                    return $"quick zones: {OnOff(_engine.Config.QuickZonesEnabled)}, " +
                           $"layout: {_engine.Config.QuickZonesLayout} " +
                           "(usage: quickzones on|off | quickzones layout <name>)";
                if (args[1].Equals("layout", StringComparison.OrdinalIgnoreCase))
                {
                    if (args.Length < 3) return "usage: quickzones layout <name>";
                    var key = _engine.Config.Layouts.Keys.FirstOrDefault(k =>
                        k.Equals(args[2], StringComparison.OrdinalIgnoreCase));
                    if (key == null) return $"unknown layout '{args[2]}'";
                    _engine.Config.QuickZonesLayout = key;
                    _engine.Config.Save();
                    return $"quick zones layout: {key}";
                }
                if (ParseToggle(args[1]) is not bool on) return "usage: quickzones on|off | quickzones layout <name>";
                _engine.SetQuickZonesEnabled(on);
                CloseZoneOverlays();
                return $"quick zones: {OnOff(on)}";
            }
            case "ontop":
            {
                if (args.Length < 2)
                    return $"focused window covers taskbar: {OnOff(_engine.Config.TopmostOnFocus)} (usage: ontop on|off)";
                if (ParseToggle(args[1]) is not bool on) return "usage: ontop on|off";
                _engine.SetTopmostOnFocus(on);
                return $"focused window covers taskbar: {OnOff(on)}";
            }
            case "startup":
            {
                if (args.Length < 2)
                    return $"run at startup: {OnOff(StartupManager.IsEnabled)} (usage: startup on|off)";
                if (ParseToggle(args[1]) is not bool on) return "usage: startup on|off";
                if (on) StartupManager.Enable(); else StartupManager.Disable();
                return $"run at startup: {OnOff(on)}";
            }
            case "reset":
            {
                int n = _engine.Assignments.Count;
                int b = _blackouts.Count;
                ResetAll();
                return $"reset: released {n} window(s), restored {b} blackout(s)";
            }
            case "reload":
                CloseAllBlackouts();
                CloseZoneOverlays();
                _engine.ReloadConfig();
                return $"config reloaded from {Config.ConfigPath}";
            case "quit":
                _marshal.BeginInvoke(ExitThread);
                return "shutting down";
            default:
                return HelpText();
        }
    }

    private static string HelpText() => """
        Polyscreen commands:
          assign <zone> <process-or-title>   clamp a window into a zone
          release <process-or-title> | all   restore a window
          list                               show zones and assigned windows
          layout [name]                      show or switch the Forced Zones layout
          layout delete <name>               delete a layout
          blackout <zone> | blackout off     toggle a black panel over a zone
          edit [name|new|close]              edit a layout (active by default) or create one
          zones                              cycle overlays: forced -> quick -> hidden
          reset                              release all windows and blackouts
          startup [on|off]                   run Polyscreen when Windows starts
          ontop [on|off]                     focused clamped window covers the taskbar
          forcedzones on|off                 clamping windows to zones (off releases all)
          quickzones on|off|layout <name>    Shift+drag snapping and its own layout
          reload                             reload config.json
          quit                               exit Polyscreen
        Hotkeys: Ctrl+Alt+1..9 assign focused window, Ctrl+Alt+0 release,
                 Ctrl+Alt+Z show zones, Ctrl+Alt+B black out zone under cursor,
                 Ctrl+Alt+Esc release everything.
        """;

    protected override void ExitThreadCore()
    {
        _tray.Visible = false;
        CloseAllBlackouts();
        CloseZoneOverlays();
        _editor?.Close();
        _quickZones.Dispose();
        _pipe.Dispose();
        _engine.Dispose(); // releases all windows back to their original state
        for (int i = 1; i <= HotkeyPanicId; i++) Native.UnregisterHotKey(_marshal.Handle, i);
        base.ExitThreadCore();
    }
}
