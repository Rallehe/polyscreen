namespace ZoneEnforcer;

/// <summary>
/// FancyZones-style drag snapping: hold Shift while dragging a window and drop
/// it into a zone of the Quick Zones layout. A one-time move — the window keeps
/// its border and is not clamped, so it never interferes with enforced windows.
/// </summary>
public class QuickZones : IDisposable
{
    private readonly Engine _engine;
    private readonly System.Windows.Forms.Timer _timer;
    private SnapOverlayForm? _overlay;
    private IntPtr _dragHwnd;
    private RECT _startRect;
    private bool _isResize;
    private bool _shiftSeen; // Shift state at the last poll — the drop handler runs async,
                             // so a fresh key read can miss a Shift released right after the button
    private readonly HashSet<IntPtr> _squared = new(); // windows whose corners we un-rounded

    public QuickZones(Engine engine)
    {
        _engine = engine;
        engine.MoveSizeStart += OnStart;
        engine.MoveSizeEnd += OnEnd;
        _timer = new System.Windows.Forms.Timer { Interval = 30 };
        _timer.Tick += (_, _) => Tick();
    }

    private static bool ShiftDown => (Native.GetAsyncKeyState(Native.VK_SHIFT) & 0x8000) != 0;

    private void OnStart(IntPtr hwnd)
    {
        if (!_engine.Config.QuickZonesEnabled) return;
        if (_engine.Assignments.ContainsKey(hwnd)) return; // enforced windows are already managed

        _dragHwnd = hwnd;
        Native.GetWindowRect(hwnd, out _startRect);
        _isResize = false;
        _shiftSeen = ShiftDown;
        _timer.Start();
    }

    private void Tick()
    {
        if (_dragHwnd == IntPtr.Zero || !Native.IsWindow(_dragHwnd))
        {
            Cancel();
            return;
        }

        // A resize drag changes the size; snapping only applies to move drags.
        Native.GetWindowRect(_dragHwnd, out var r);
        if (r.Width != _startRect.Width || r.Height != _startRect.Height) _isResize = true;

        _shiftSeen = ShiftDown;
        if (_shiftSeen && !_isResize)
        {
            Native.GetCursorPos(out var pt);
            _overlay ??= new SnapOverlayForm();
            _overlay.ShowZones(_engine.Config.QuickZones, pt);
        }
        else
        {
            HideOverlay();
        }
    }

    private void OnEnd(IntPtr hwnd)
    {
        _timer.Stop();
        if (hwnd == _dragHwnd && (_shiftSeen || ShiftDown) && !_isResize)
        {
            Native.GetCursorPos(out var pt);
            var zone = _engine.Config.QuickZones.FirstOrDefault(z => z.Contains(pt.X, pt.Y));
            if (zone != null)
            {
                if (Native.IsZoomed(hwnd)) Native.ShowWindow(hwnd, Native.SW_RESTORE);

                // Snapped windows are normal windows (not topmost), so keep them out from
                // under the taskbar by clipping the zone to the monitor's work area.
                var target = Rectangle.Intersect(
                    new Rectangle(zone.X, zone.Y, zone.Width, zone.Height),
                    Screen.FromPoint(new Point(pt.X, pt.Y)).WorkingArea);
                if (target.IsEmpty) target = new Rectangle(zone.X, zone.Y, zone.Width, zone.Height);

                // The window rect extends past the visible frame by the invisible resize
                // borders; oversize the target by that margin so the visible window fills
                // the zone edge-to-edge instead of leaving a ~7px gap on each side.
                int x = target.X, y = target.Y, w = target.Width, h = target.Height;
                if (Native.GetWindowRect(hwnd, out var wr) &&
                    Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_EXTENDED_FRAME_BOUNDS,
                        out var fb, System.Runtime.InteropServices.Marshal.SizeOf<RECT>()) == 0)
                {
                    int left = fb.Left - wr.Left, top = fb.Top - wr.Top;
                    int right = wr.Right - fb.Right, bottom = wr.Bottom - fb.Bottom;
                    if (left is >= 0 and < 32 && top is >= 0 and < 32 &&
                        right is >= 0 and < 32 && bottom is >= 0 and < 32)
                    {
                        x -= left; y -= top; w += left + right; h += top + bottom;
                    }
                }

                SetCorners(hwnd, round: false);
                _squared.Add(hwnd);
                Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h,
                    Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
                Log.Write($"quick-snap {hwnd} \"{Native.GetWindowTitle(hwnd)}\" -> {zone}");
            }
            else
            {
                RestoreCornersIfSquared(hwnd);
            }
        }
        else if (hwnd == _dragHwnd && !_isResize)
        {
            // Dragged away without snapping: give the window its rounded corners back.
            RestoreCornersIfSquared(hwnd);
        }
        Cancel();
    }

    private static void SetCorners(IntPtr hwnd, bool round)
    {
        int pref = round ? Native.DWMWCP_DEFAULT : Native.DWMWCP_DONOTROUND;
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
    }

    private void RestoreCornersIfSquared(IntPtr hwnd)
    {
        if (_squared.Remove(hwnd) && Native.IsWindow(hwnd))
            SetCorners(hwnd, round: true);
    }

    private void HideOverlay()
    {
        _overlay?.Close();
        _overlay = null;
    }

    private void Cancel()
    {
        _timer.Stop();
        HideOverlay();
        _dragHwnd = IntPtr.Zero;
    }

    public void Dispose()
    {
        _engine.MoveSizeStart -= OnStart;
        _engine.MoveSizeEnd -= OnEnd;
        Cancel();
        _timer.Dispose();
        foreach (var hwnd in _squared.ToList()) RestoreCornersIfSquared(hwnd);
    }
}
