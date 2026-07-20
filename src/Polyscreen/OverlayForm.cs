namespace Polyscreen;

/// <summary>Translucent flash showing where the zones of the active layout sit.</summary>
public class OverlayForm : Form
{
    public static readonly Color[] Palette =
    {
        Color.FromArgb(0, 120, 215),   // blue
        Color.FromArgb(16, 137, 62),   // green
        Color.FromArgb(194, 57, 179),  // magenta
        Color.FromArgb(247, 99, 12),   // orange
        Color.FromArgb(0, 153, 188),   // teal
    };

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= (int)Native.WS_EX_TOOLWINDOW; // keep out of alt-tab
            return cp;
        }
    }

    private OverlayForm(Zone zone, int index, bool persistent)
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = new Rectangle(zone.X, zone.Y, zone.Width, zone.Height);
        BackColor = Palette[index % Palette.Length];
        Opacity = 0.35;
        TopMost = true;
        ShowInTaskbar = false;

        Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 42, FontStyle.Bold),
            Text = $"{index + 1}\n{zone.Name}",
        });

        if (!persistent)
        {
            var timer = new System.Windows.Forms.Timer { Interval = 1600 };
            timer.Tick += (_, _) => { timer.Dispose(); Close(); };
            timer.Start();
        }
    }

    public static void Flash(IReadOnlyList<Zone> zones)
    {
        for (int i = 0; i < zones.Count; i++)
            new OverlayForm(zones[i], i, persistent: false).Show();
    }

    /// <summary>Overlays that stay up until the caller closes them, with a banner naming the set.</summary>
    public static List<Form> ShowPersistent(IReadOnlyList<Zone> zones, string title)
    {
        var forms = new List<Form>();
        for (int i = 0; i < zones.Count; i++)
        {
            var f = new OverlayForm(zones[i], i, persistent: true);
            f.Show();
            forms.Add(f);
        }
        var banner = new BannerForm(title);
        banner.Show();
        forms.Add(banner);
        return forms;
    }

    private sealed class BannerForm : Form
    {
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

        public BannerForm(string title)
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            ShowInTaskbar = false;
            BackColor = Color.FromArgb(20, 20, 24);
            var screen = Screen.PrimaryScreen!.Bounds;
            Size = new Size(560, 56);
            Location = new Point(screen.X + (screen.Width - Width) / 2, screen.Y + 10);
            Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 16, FontStyle.Bold),
                Text = title,
            });
        }
    }
}
