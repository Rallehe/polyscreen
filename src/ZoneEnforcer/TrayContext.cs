using System.Text;

namespace ZoneEnforcer;

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
    private LayoutEditorForm? _editor;

    public TrayContext()
    {
        _engine = new Engine(Config.Load());
        _marshal = new MarshalForm();
        _marshal.Hotkey += OnHotkey;
        RegisterHotkeys();

        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "ZoneEnforcer",
            Visible = true,
            ContextMenuStrip = new ContextMenuStrip(),
        };
        _tray.ContextMenuStrip.Opening += (_, _) => RebuildMenu();
        _tray.DoubleClick += (_, _) => OverlayForm.Flash(_engine.Config.ActiveZones);

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
        _engine.ReleaseAll();
        _editor?.Close();
    }

    private void OnHotkey(int id)
    {
        if (id == HotkeyZonesId)
        {
            OverlayForm.Flash(_engine.Config.ActiveZones);
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

    private void OpenEditor()
    {
        if (_editor != null && !_editor.IsDisposed)
        {
            _editor.Activate();
            return;
        }
        CloseAllBlackouts();
        var screen = Screen.PrimaryScreen!.Bounds;
        _editor = new LayoutEditorForm(screen, _engine.Config.ActiveLayout, _engine.Config.ActiveZones,
            (name, zones) =>
            {
                _engine.Config.Layouts[name] = zones;
                _engine.SetLayout(name); // persists the config and re-clamps assigned windows
                OverlayForm.Flash(zones);
            });
        _editor.FormClosed += (_, _) => _editor = null;
        _editor.Show();
    }

    private void Notify(string text)
    {
        _tray.BalloonTipTitle = "ZoneEnforcer";
        _tray.BalloonTipText = text;
        _tray.ShowBalloonTip(1500);
    }

    private void RebuildMenu()
    {
        var menu = _tray.ContextMenuStrip!;
        menu.Items.Clear();

        menu.Items.Add(new ToolStripMenuItem("Show zones  (Ctrl+Alt+Z)", null,
            (_, _) => OverlayForm.Flash(_engine.Config.ActiveZones)));

        var layoutMenu = new ToolStripMenuItem("Layout");
        foreach (var name in _engine.Config.Layouts.Keys)
        {
            var item = new ToolStripMenuItem(name, null, (_, _) =>
            {
                CloseAllBlackouts();
                _engine.SetLayout(name);
            })
            {
                Checked = name == _engine.Config.ActiveLayout,
            };
            layoutMenu.DropDownItems.Add(item);
        }
        if (_engine.Config.Layouts.Count > 1)
        {
            layoutMenu.DropDownItems.Add(new ToolStripSeparator());
            var deleteMenu = new ToolStripMenuItem("Delete layout");
            foreach (var name in _engine.Config.Layouts.Keys)
            {
                deleteMenu.DropDownItems.Add(new ToolStripMenuItem(name, null, (_, _) =>
                {
                    if (MessageBox.Show($"Delete layout '{name}'?", "ZoneEnforcer",
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
            layoutMenu.DropDownItems.Add(deleteMenu);
        }
        menu.Items.Add(layoutMenu);

        menu.Items.Add(new ToolStripMenuItem("Edit layout…", null, (_, _) => OpenEditor()));

        var blackoutMenu = new ToolStripMenuItem("Black out zone  (Ctrl+Alt+B)");
        foreach (var zone in _engine.Config.ActiveZones)
        {
            var z = zone;
            blackoutMenu.DropDownItems.Add(new ToolStripMenuItem(z.Name, null, (_, _) => ToggleBlackout(z))
            {
                Checked = _blackouts.ContainsKey(z.Name),
            });
        }
        if (_blackouts.Count > 0)
        {
            blackoutMenu.DropDownItems.Add(new ToolStripSeparator());
            blackoutMenu.DropDownItems.Add(new ToolStripMenuItem("Restore all", null, (_, _) => CloseAllBlackouts()));
        }
        menu.Items.Add(blackoutMenu);

        var assigned = new ToolStripMenuItem("Assigned windows");
        foreach (var a in _engine.Assignments.Values)
        {
            var title = Native.GetWindowTitle(a.Hwnd);
            if (title.Length > 40) title = title[..40] + "…";
            var hwnd = a.Hwnd;
            assigned.DropDownItems.Add(new ToolStripMenuItem(
                $"{title}  [{a.Zone.Name}]  — click to release", null,
                (_, _) => _engine.Release(hwnd)));
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
            CloseAllBlackouts();
            _engine.ReloadConfig();
        }));
        menu.Items.Add(new ToolStripMenuItem("Open config file", null, (_, _) =>
            System.Diagnostics.Process.Start("notepad.exe", Config.ConfigPath)));
        menu.Items.Add(new ToolStripMenuItem("Run at startup", null, (_, _) =>
        {
            if (StartupManager.IsEnabled) StartupManager.Disable();
            else StartupManager.Enable();
        })
        {
            Checked = StartupManager.IsEnabled,
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitThread()));
    }

    public string HandleCommand(string[] args)
    {
        if (args.Length == 0) return HelpText();
        switch (args[0].ToLowerInvariant())
        {
            case "assign":
            {
                if (args.Length < 3) return "usage: assign <zone> <process-or-title>";
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
                sb.AppendLine($"layout: {_engine.Config.ActiveLayout}");
                foreach (var z in _engine.Config.ActiveZones) sb.AppendLine($"  zone {z}");
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
                        _engine.Config.Layouts.Keys.Select(k => k == _engine.Config.ActiveLayout ? k + " (active)" : k));
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
                OpenEditor();
                return "layout editor opened (Enter saves, Esc cancels)";
            }
            case "zones":
                OverlayForm.Flash(_engine.Config.ActiveZones);
                return string.Join(Environment.NewLine, _engine.Config.ActiveZones.Select(z => z.ToString()));
            case "startup":
            {
                if (args.Length < 2)
                    return $"run at startup: {(StartupManager.IsEnabled ? "on" : "off")} (usage: startup on|off)";
                if (args[1].Equals("on", StringComparison.OrdinalIgnoreCase))
                {
                    StartupManager.Enable();
                    return "run at startup: on";
                }
                if (args[1].Equals("off", StringComparison.OrdinalIgnoreCase))
                {
                    StartupManager.Disable();
                    return "run at startup: off";
                }
                return "usage: startup on|off";
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
        ZoneEnforcer commands:
          assign <zone> <process-or-title>   clamp a window into a zone
          release <process-or-title> | all   restore a window
          list                               show zones and assigned windows
          layout [name]                      show or switch layout
          layout delete <name>               delete a layout
          blackout <zone> | blackout off     toggle a black panel over a zone
          edit [close]                       open the visual layout editor
          zones                              flash the zone overlay
          reset                              release all windows and blackouts
          startup [on|off]                   run ZoneEnforcer when Windows starts
          reload                             reload config.json
          quit                               exit ZoneEnforcer
        Hotkeys: Ctrl+Alt+1..9 assign focused window, Ctrl+Alt+0 release,
                 Ctrl+Alt+Z show zones, Ctrl+Alt+B black out zone under cursor,
                 Ctrl+Alt+Esc release everything.
        """;

    protected override void ExitThreadCore()
    {
        _tray.Visible = false;
        CloseAllBlackouts();
        _editor?.Close();
        _pipe.Dispose();
        _engine.Dispose(); // releases all windows back to their original state
        for (int i = 1; i <= HotkeyPanicId; i++) Native.UnregisterHotKey(_marshal.Handle, i);
        base.ExitThreadCore();
    }
}
