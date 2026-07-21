namespace Polyscreen;

public class Assignment
{
    public IntPtr Hwnd;
    public Zone Zone = new();
    public IntPtr OrigStyle;
    public IntPtr OrigExStyle;
    public RECT OrigRect;
    public bool WasMaximized;
    public bool IsTopmost;
    public DateTime LastEnforce = DateTime.MinValue;
}

/// <summary>
/// Watches assigned windows via a WinEvent hook and keeps them borderless
/// and clamped to their zone, re-clamping whenever the app tries to go
/// fullscreen or otherwise move/resize itself.
/// </summary>
public class Engine : IDisposable
{
    private readonly Dictionary<IntPtr, Assignment> _assignments = new();
    private readonly Native.WinEventDelegate _hookProc; // field keeps the delegate alive for the native hook
    private IntPtr _hook;
    private IntPtr _fgHook;
    private readonly System.Windows.Forms.Timer _poll;
    private static readonly TimeSpan EnforceThrottle = TimeSpan.FromMilliseconds(50);

    public Config Config { get; private set; }
    public event Action? AssignmentsChanged;
    public event Action<IntPtr>? MoveSizeStart;
    public event Action<IntPtr>? MoveSizeEnd;

    public Engine(Config config)
    {
        Config = config;
        _hookProc = OnWinEvent;
        _hook = Native.SetWinEventHook(Native.EVENT_OBJECT_DESTROY, Native.EVENT_OBJECT_LOCATIONCHANGE,
            IntPtr.Zero, _hookProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
        _fgHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_MOVESIZEEND,
            IntPtr.Zero, _hookProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT);

        // Safety net for events missed while throttled, and for cleanup of dead windows.
        // Also reconciles topmost state: foreground events are not reliably delivered
        // (e.g. around no-activate windows appearing), so poll the true foreground too.
        _poll = new System.Windows.Forms.Timer { Interval = 250 };
        _poll.Tick += (_, _) =>
        {
            EnforceAll();
            UpdateTopmost(Native.GetForegroundWindow());
        };
        _poll.Start();
    }

    public IReadOnlyDictionary<IntPtr, Assignment> Assignments => _assignments;

    public void ReloadConfig()
    {
        Config = Config.Load();
        // Re-resolve zones by name in the new active layout; keep old rect if the name is gone.
        foreach (var a in _assignments.Values)
        {
            var zone = Config.FindZone(a.Zone.Name);
            if (zone != null) a.Zone = zone;
        }
        EnforceAll();
        AssignmentsChanged?.Invoke();
    }

    public void SetLayout(string name)
    {
        if (!Config.Layouts.ContainsKey(name)) return;
        Config.ActiveLayout = name;
        Config.Save();
        foreach (var a in _assignments.Values)
        {
            var zone = Config.FindZone(a.Zone.Name);
            if (zone != null) a.Zone = zone;
        }
        EnforceAll();
        AssignmentsChanged?.Invoke();
    }

    /// <summary>Deletes a layout (case-insensitive). Returns an error message, or null on success.</summary>
    public string? DeleteLayout(string name)
    {
        var key = Config.Layouts.Keys.FirstOrDefault(k =>
            k.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (key == null) return $"unknown layout '{name}'";
        if (Config.Layouts.Count == 1) return "cannot delete the only layout";

        bool wasActive = key.Equals(Config.ActiveLayout, StringComparison.OrdinalIgnoreCase);
        Config.Layouts.Remove(key);
        bool wasQuick = key.Equals(Config.QuickZonesLayout, StringComparison.OrdinalIgnoreCase);
        Log.Write($"layout deleted: {key}");

        if (wasActive) SetLayout(Config.Layouts.Keys.First()); // re-resolves zones and saves
        if (wasQuick) Config.QuickZonesLayout = Config.ActiveLayout;
        Config.Save();
        AssignmentsChanged?.Invoke();
        return null;
    }

    public bool Assign(IntPtr hwnd, Zone zone)
    {
        if (!Config.ForcedZonesEnabled) return false;
        if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return false;
        if (IsShellWindow(hwnd)) return false;

        if (_assignments.TryGetValue(hwnd, out var existing))
        {
            existing.Zone = zone;
            Enforce(existing, force: true);
            AssignmentsChanged?.Invoke();
            return true;
        }

        var a = new Assignment
        {
            Hwnd = hwnd,
            Zone = zone,
            OrigStyle = Native.GetWindowLongPtr(hwnd, Native.GWL_STYLE),
            OrigExStyle = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE),
            WasMaximized = Native.IsZoomed(hwnd),
        };
        Native.GetWindowRect(hwnd, out a.OrigRect);

        _assignments[hwnd] = a;
        Log.Write($"assign {hwnd} \"{Native.GetWindowTitle(hwnd)}\" -> {zone}");
        Enforce(a, force: true);
        UpdateTopmost(Native.GetForegroundWindow());
        AssignmentsChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// The focused clamped window goes always-on-top so it covers the taskbar the way a
    /// real fullscreen window would; it drops back as soon as focus moves elsewhere.
    /// </summary>
    private void UpdateTopmost(IntPtr foreground)
    {
        foreach (var a in _assignments.Values)
        {
            bool onTop = Config.TopmostOnFocus && a.Hwnd == foreground;
            if (onTop)
            {
                if (!a.IsTopmost)
                {
                    bool ok = Native.SetWindowPos(a.Hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                        Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                    if (ok) a.IsTopmost = true;
                    Log.Write($"topmost set for {a.Hwnd} (fg={foreground}, ok={ok})");
                }
                // Topmost windows share one z-band with the taskbar and blackout panels,
                // and any of those being raised (e.g. a tray click) reorders the band —
                // so re-raise to the top of the band on every focus, not just the first.
                Native.SetWindowPos(a.Hwnd, Native.HWND_TOP, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            }
            else if (a.IsTopmost)
            {
                bool ok = Native.SetWindowPos(a.Hwnd, Native.HWND_NOTOPMOST, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                if (ok) a.IsTopmost = false;
                Log.Write($"topmost cleared for {a.Hwnd} (fg={foreground}, ok={ok})");
            }
        }
    }

    public void SetTopmostOnFocus(bool enabled)
    {
        Config.TopmostOnFocus = enabled;
        Config.Save();
        UpdateTopmost(Native.GetForegroundWindow());
    }

    /// <summary>Toggle Forced Zones. Disabling releases every clamped window.</summary>
    public void SetForcedZonesEnabled(bool enabled)
    {
        Config.ForcedZonesEnabled = enabled;
        if (!enabled) ReleaseAll();
        Config.Save();
    }

    public void SetQuickZonesEnabled(bool enabled)
    {
        Config.QuickZonesEnabled = enabled;
        Config.Save();
    }

    public bool Release(IntPtr hwnd)
    {
        if (!_assignments.Remove(hwnd, out var a)) return false;
        Log.Write($"release {hwnd} \"{Native.GetWindowTitle(hwnd)}\"");
        if (Native.IsWindow(hwnd))
        {
            Native.SetWindowPos(hwnd, Native.HWND_NOTOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            Native.SetWindowLongPtr(hwnd, Native.GWL_STYLE, a.OrigStyle);
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, a.OrigExStyle);
            ApplyDwmDecoration(hwnd, clamped: false);
            Native.SetWindowPos(hwnd, IntPtr.Zero, a.OrigRect.Left, a.OrigRect.Top,
                a.OrigRect.Width, a.OrigRect.Height,
                Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER | Native.SWP_FRAMECHANGED);
            if (a.WasMaximized) Native.ShowWindow(hwnd, Native.SW_MAXIMIZE);
        }
        AssignmentsChanged?.Invoke();
        return true;
    }

    public void ReleaseAll()
    {
        foreach (var hwnd in _assignments.Keys.ToList()) Release(hwnd);
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild,
        uint thread, uint time)
    {
        if (idObject != Native.OBJID_WINDOW || idChild != 0 || hwnd == IntPtr.Zero) return;

        switch (eventType)
        {
            case Native.EVENT_SYSTEM_FOREGROUND:
                UpdateTopmost(hwnd);
                break;

            case Native.EVENT_SYSTEM_MOVESIZESTART:
                MoveSizeStart?.Invoke(hwnd);
                break;

            case Native.EVENT_SYSTEM_MOVESIZEEND:
                MoveSizeEnd?.Invoke(hwnd);
                break;

            case Native.EVENT_OBJECT_DESTROY:
                if (_assignments.Remove(hwnd)) AssignmentsChanged?.Invoke();
                break;

            case Native.EVENT_OBJECT_LOCATIONCHANGE:
                if (_assignments.TryGetValue(hwnd, out var a) &&
                    DateTime.UtcNow - a.LastEnforce >= EnforceThrottle)
                {
                    Enforce(a);
                }
                break;

            case Native.EVENT_OBJECT_SHOW:
                TryAutoAssign(hwnd);
                break;
        }
    }

    private void TryAutoAssign(IntPtr hwnd)
    {
        if (!Config.ForcedZonesEnabled) return;
        if (Config.AutoRules.Count == 0 || _assignments.ContainsKey(hwnd)) return;
        if (!Native.IsWindowVisible(hwnd)) return;
        if (Native.GetWindow(hwnd, Native.GW_OWNER) != IntPtr.Zero) return; // skip owned dialogs

        var title = Native.GetWindowTitle(hwnd);
        if (title.Length == 0) return;

        string? process = null;
        foreach (var rule in Config.AutoRules)
        {
            var zone = Config.FindZone(rule.Zone);
            if (zone == null) continue;
            if (rule.TitleContains != null &&
                !title.Contains(rule.TitleContains, StringComparison.OrdinalIgnoreCase)) continue;

            process ??= Native.GetProcessName(hwnd);
            if (!string.Equals(process, rule.Process, StringComparison.OrdinalIgnoreCase)) continue;

            Assign(hwnd, zone);
            return;
        }
    }

    private void EnforceAll()
    {
        foreach (var a in _assignments.Values.ToList())
        {
            if (!Native.IsWindow(a.Hwnd))
            {
                _assignments.Remove(a.Hwnd);
                AssignmentsChanged?.Invoke();
                continue;
            }
            Enforce(a);
        }
    }

    private void Enforce(Assignment a, bool force = false)
    {
        var hwnd = a.Hwnd;
        if (!Native.IsWindow(hwnd)) return;
        if (Native.IsIconic(hwnd)) return; // let the user keep it minimized

        // Apps toggling fullscreen often restore their own caption/frame; strip it again.
        long style = Native.GetWindowLongPtr(hwnd, Native.GWL_STYLE).ToInt64();
        long stripped = style & ~(Native.WS_CAPTION | Native.WS_THICKFRAME | Native.WS_SYSMENU |
                                  Native.WS_MINIMIZEBOX | Native.WS_MAXIMIZEBOX);
        bool frameChanged = stripped != style;
        if (frameChanged) Native.SetWindowLongPtr(hwnd, Native.GWL_STYLE, (IntPtr)stripped);

        if (Native.IsZoomed(hwnd)) Native.ShowWindow(hwnd, Native.SW_RESTORE);

        Native.GetWindowRect(hwnd, out var r);
        var z = a.Zone;
        bool moved = r.Left != z.X || r.Top != z.Y || r.Width != z.Width || r.Height != z.Height;

        if (force || frameChanged) ApplyDwmDecoration(hwnd, clamped: true);

        if (force || moved || frameChanged)
        {
            // SWP_NOSENDCHANGING skips WM_WINDOWPOSCHANGING, so the app cannot veto
            // the move — fullscreen Chromium windows otherwise pin themselves to the
            // monitor bounds and the clamp silently never lands.
            uint flags = Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER |
                         Native.SWP_NOSENDCHANGING;
            if (frameChanged || force) flags |= Native.SWP_FRAMECHANGED;
            Native.SetWindowPos(hwnd, IntPtr.Zero, z.X, z.Y, z.Width, z.Height, flags);
            a.LastEnforce = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Clamped windows get no DWM outline and square corners, so zones tile seamlessly;
    /// the compositor draws both outside the window styles, hence the separate API.
    /// </summary>
    private static void ApplyDwmDecoration(IntPtr hwnd, bool clamped)
    {
        int corner = clamped ? Native.DWMWCP_DONOTROUND : Native.DWMWCP_DEFAULT;
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        int border = unchecked((int)(clamped ? Native.DWMWA_COLOR_NONE : Native.DWMWA_COLOR_DEFAULT));
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_BORDER_COLOR, ref border, sizeof(int));
    }

    private static bool IsShellWindow(IntPtr hwnd)
    {
        var cls = Native.GetWindowClass(hwnd);
        return cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    }

    public void Dispose()
    {
        _poll.Dispose();
        if (_hook != IntPtr.Zero)
        {
            Native.UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
        if (_fgHook != IntPtr.Zero)
        {
            Native.UnhookWinEvent(_fgHook);
            _fgHook = IntPtr.Zero;
        }
        ReleaseAll();
    }
}
