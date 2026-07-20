namespace Polyscreen;

/// <summary>
/// Pure-black topmost panel covering one zone â€” on an OLED the pixels are
/// simply off. Never takes focus; double-click it to restore the zone.
/// </summary>
public class BlackoutForm : Form
{
    public string ZoneName { get; }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= (int)(Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
            return cp;
        }
    }

    public BlackoutForm(Zone zone)
    {
        ZoneName = zone.Name;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = new Rectangle(zone.X, zone.Y, zone.Width, zone.Height);
        BackColor = Color.Black;
        TopMost = true;
        ShowInTaskbar = false;

        // Brief hint, then true black.
        var hint = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(70, 70, 70),
            BackColor = Color.Black,
            Font = new Font("Segoe UI", 14),
            Text = "zone blacked out â€” double-click to restore",
        };
        hint.DoubleClick += (_, _) => Close();
        Controls.Add(hint);

        var timer = new System.Windows.Forms.Timer { Interval = 2500 };
        timer.Tick += (_, _) => { timer.Dispose(); if (!IsDisposed) Controls.Remove(hint); };
        timer.Start();

        DoubleClick += (_, _) => Close();
    }
}
