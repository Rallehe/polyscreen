namespace Polyscreen;

/// <summary>
/// Click-through overlay shown while Shift-dragging a window: dims the screen,
/// outlines the Quick Zones layout, and highlights the zone under the cursor.
/// </summary>
public class SnapOverlayForm : Form
{
    private IReadOnlyList<Zone> _zones = Array.Empty<Zone>();
    private int _hovered = -1;

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // Transparent to mouse input so the drag underneath is never disturbed.
            cp.ExStyle |= (int)(Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
            return cp;
        }
    }

    public SnapOverlayForm()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer, true);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = SystemInformation.VirtualScreen;
        BackColor = Color.FromArgb(10, 10, 12);
        Opacity = 0.35;
        TopMost = true;
        ShowInTaskbar = false;
    }

    /// <summary>Update zones and the highlighted zone from the current cursor position.</summary>
    public void ShowZones(IReadOnlyList<Zone> zones, Native.POINT cursor)
    {
        int hovered = -1;
        for (int i = 0; i < zones.Count; i++)
            if (zones[i].Contains(cursor.X, cursor.Y)) { hovered = i; break; }

        bool changed = !ReferenceEquals(zones, _zones) || hovered != _hovered;
        _zones = zones;
        _hovered = hovered;
        if (!Visible) Show();
        if (changed) Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        using var nameFont = new Font("Segoe UI", 26, FontStyle.Bold);
        using var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

        for (int i = 0; i < _zones.Count; i++)
        {
            var z = _zones[i];
            var r = new Rectangle(z.X - Bounds.X, z.Y - Bounds.Y, z.Width, z.Height);
            var c = OverlayForm.Palette[i % OverlayForm.Palette.Length];

            if (i == _hovered)
            {
                using var fill = new SolidBrush(Color.FromArgb(150, c));
                g.FillRectangle(fill, r);
                using var pen = new Pen(Color.White, 5);
                g.DrawRectangle(pen, Rectangle.Inflate(r, -3, -3));
            }
            else
            {
                using var fill = new SolidBrush(Color.FromArgb(40, c));
                g.FillRectangle(fill, r);
                using var pen = new Pen(c, 2);
                g.DrawRectangle(pen, Rectangle.Inflate(r, -1, -1));
            }

            using var text = new SolidBrush(Color.White);
            g.DrawString(z.Name, nameFont, text, r, center);
        }
    }
}
