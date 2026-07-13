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

    private readonly Engine _engine;
    private readonly MarshalForm _marshal;
    private readonly NotifyIcon _tray;
    private readonly PipeServer _pipe;

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
        Log.Write($"started, layout '{_engine.Config.ActiveLayout}', config {Config.ConfigPath}");
    }

    private void RegisterHotkeys()
    {
        uint mods = Native.MOD_CONTROL | Native.MOD_ALT;
        for (int i = 1; i <= 9; i++)
            Native.RegisterHotKey(_marshal.Handle, i, mods, (uint)('0' + i)); // Ctrl+Alt+1..9: assign
        Native.RegisterHotKey(_marshal.Handle, HotkeyReleaseId, mods, '0');   // Ctrl+Alt+0: release
        Native.RegisterHotKey(_marshal.Handle, HotkeyZonesId, mods, 'Z');     // Ctrl+Alt+Z: show zones
    }

    private void OnHotkey(int id)
    {
        if (id == HotkeyZonesId)
        {
            OverlayForm.Flash(_engine.Config.ActiveZones);
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
            var item = new ToolStripMenuItem(name, null, (_, _) => _engine.SetLayout(name))
            {
                Checked = name == _engine.Config.ActiveLayout,
            };
            layoutMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(layoutMenu);

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

        menu.Items.Add(new ToolStripMenuItem("Release all", null, (_, _) => _engine.ReleaseAll()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Reload config", null, (_, _) => _engine.ReloadConfig()));
        menu.Items.Add(new ToolStripMenuItem("Open config file", null, (_, _) =>
            System.Diagnostics.Process.Start("notepad.exe", Config.ConfigPath)));
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
                return sb.ToString();
            }
            case "layout":
            {
                if (args.Length < 2)
                    return "layouts: " + string.Join(", ",
                        _engine.Config.Layouts.Keys.Select(k => k == _engine.Config.ActiveLayout ? k + " (active)" : k));
                if (!_engine.Config.Layouts.ContainsKey(args[1])) return $"unknown layout '{args[1]}'";
                _engine.SetLayout(args[1]);
                return $"layout -> {args[1]}";
            }
            case "zones":
                OverlayForm.Flash(_engine.Config.ActiveZones);
                return string.Join(Environment.NewLine, _engine.Config.ActiveZones.Select(z => z.ToString()));
            case "reload":
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
          zones                              flash the zone overlay
          reload                             reload config.json
          quit                               exit ZoneEnforcer
        Hotkeys: Ctrl+Alt+1..9 assign focused window, Ctrl+Alt+0 release, Ctrl+Alt+Z show zones.
        """;

    protected override void ExitThreadCore()
    {
        _tray.Visible = false;
        _pipe.Dispose();
        _engine.Dispose(); // releases all windows back to their original state
        for (int i = 1; i <= HotkeyZonesId; i++) Native.UnregisterHotKey(_marshal.Handle, i);
        base.ExitThreadCore();
    }
}
