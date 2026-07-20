namespace ZoneEnforcer;

/// <summary>
/// Fullscreen visual layout editor (FancyZones-style). Click a zone to split it
/// vertically, Shift+click to split horizontally, drag shared borders to resize
/// (snaps to halves/thirds/quarters), right-click a zone to remove it, Enter to
/// save, Esc to cancel. Zones form a binary split tree, so every edit keeps the
/// screen fully tiled with no gaps or overlaps.
/// </summary>
public class LayoutEditorForm : Form
{
    private class Node
    {
        public Node? A, B;
        public bool Vertical;      // true: A and B side by side (vertical divider)
        public double Ratio = 0.5; // fraction of this node's rect given to A
        public string? Name;       // preserved zone name (leaves only)
        public bool IsLeaf => A == null;
    }

    private const int MinZonePx = 150;
    private const int GrabPx = 8;
    private static readonly double[] SnapFractions = { 0.25, 1 / 3.0, 0.5, 2 / 3.0, 0.75 };
    private const int SnapPx = 14;

    private readonly Rectangle _screen;
    private readonly string _layoutName;
    private readonly Action<string, List<Zone>, bool> _onSave;
    private bool _overTaskbar;
    private Node _root;

    private Node? _drag;
    private Rectangle _dragRect;

    public LayoutEditorForm(Rectangle screen, string layoutName, IReadOnlyList<Zone> zones,
        bool overTaskbar, Action<string, List<Zone>, bool> onSave)
    {
        _screen = screen;
        _layoutName = layoutName;
        _overTaskbar = overTaskbar;
        _onSave = onSave;
        _root = TryImport(zones) ?? new Node();

        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer, true);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = screen;
        BackColor = Color.FromArgb(18, 18, 22);
        Opacity = 0.97;
        TopMost = true;
        ShowInTaskbar = false;
        KeyPreview = true;
        Cursor = Cursors.Cross;
    }

    private Rectangle RootRect => new(0, 0, _screen.Width, _screen.Height);

    // ---- tree layout ----

    private void Walk(Node n, Rectangle r, List<(Node n, Rectangle r)>? leaves,
        List<(Node n, Rectangle r)>? dividers)
    {
        if (n.IsLeaf)
        {
            leaves?.Add((n, r));
            return;
        }
        dividers?.Add((n, r));
        var (ra, rb) = ChildRects(n, r);
        Walk(n.A!, ra, leaves, dividers);
        Walk(n.B!, rb, leaves, dividers);
    }

    private static (Rectangle a, Rectangle b) ChildRects(Node n, Rectangle r)
    {
        if (n.Vertical)
        {
            int wA = Math.Max(1, (int)Math.Round(r.Width * n.Ratio));
            return (new Rectangle(r.X, r.Y, wA, r.Height),
                    new Rectangle(r.X + wA, r.Y, r.Width - wA, r.Height));
        }
        int hA = Math.Max(1, (int)Math.Round(r.Height * n.Ratio));
        return (new Rectangle(r.X, r.Y, r.Width, hA),
                new Rectangle(r.X, r.Y + hA, r.Width, r.Height - hA));
    }

    private List<(Node n, Rectangle r)> Leaves()
    {
        var list = new List<(Node, Rectangle)>();
        Walk(_root, RootRect, list, null);
        return list.OrderBy(t => t.Item2.X).ThenBy(t => t.Item2.Y).ToList();
    }

    private List<(Node n, Rectangle r)> Dividers()
    {
        var list = new List<(Node, Rectangle)>();
        Walk(_root, RootRect, null, list);
        return list;
    }

    private static int DividerPos(Node n, Rectangle r) => n.Vertical
        ? r.X + (int)Math.Round(r.Width * n.Ratio)
        : r.Y + (int)Math.Round(r.Height * n.Ratio);

    private (Node n, Rectangle r)? HitDivider(Point p)
    {
        foreach (var (n, r) in Dividers())
        {
            int pos = DividerPos(n, r);
            bool hit = n.Vertical
                ? Math.Abs(p.X - pos) <= GrabPx && p.Y >= r.Y && p.Y <= r.Bottom
                : Math.Abs(p.Y - pos) <= GrabPx && p.X >= r.X && p.X <= r.Right;
            if (hit) return (n, r);
        }
        return null;
    }

    private (Node n, Rectangle r)? HitLeaf(Point p) =>
        Leaves().Where(t => t.r.Contains(p)).Cast<(Node, Rectangle)?>().FirstOrDefault();

    private static Node? FindParent(Node current, Node child)
    {
        if (current.IsLeaf) return null;
        if (current.A == child || current.B == child) return current;
        return FindParent(current.A!, child) ?? FindParent(current.B!, child);
    }

    // ---- editing ----

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            var divider = HitDivider(e.Location);
            if (divider != null)
            {
                (_drag, _dragRect) = divider.Value;
                return;
            }
            var leaf = HitLeaf(e.Location);
            if (leaf != null) Split(leaf.Value.n, leaf.Value.r, e.Location,
                horizontal: (ModifierKeys & Keys.Shift) != 0);
        }
        else if (e.Button == MouseButtons.Right)
        {
            var leaf = HitLeaf(e.Location);
            if (leaf != null) Remove(leaf.Value.n);
        }
    }

    private void Split(Node leaf, Rectangle r, Point at, bool horizontal)
    {
        int span = horizontal ? r.Height : r.Width;
        if (span < 2 * MinZonePx) return; // too small to split

        int px = horizontal ? at.Y - r.Y : at.X - r.X;
        px = Snap(px, span);
        px = Math.Clamp(px, MinZonePx, span - MinZonePx);

        leaf.A = new Node { Name = leaf.Name };
        leaf.B = new Node();
        leaf.Vertical = !horizontal;
        leaf.Ratio = (double)px / span;
        leaf.Name = null;
        Invalidate();
    }

    private void Remove(Node leaf)
    {
        var parent = FindParent(_root, leaf);
        if (parent == null) return; // last remaining zone
        var sibling = parent.A == leaf ? parent.B! : parent.A!;
        var grand = FindParent(_root, parent);
        if (grand == null) _root = sibling;
        else if (grand.A == parent) grand.A = sibling;
        else grand.B = sibling;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_drag != null)
        {
            int span = _drag.Vertical ? _dragRect.Width : _dragRect.Height;
            int px = _drag.Vertical ? e.X - _dragRect.X : e.Y - _dragRect.Y;
            px = Snap(px, span);
            px = Math.Clamp(px, MinZonePx, Math.Max(MinZonePx, span - MinZonePx));
            _drag.Ratio = (double)px / span;
            Invalidate();
            return;
        }
        var divider = HitDivider(e.Location);
        Cursor = divider == null ? Cursors.Cross
            : divider.Value.n.Vertical ? Cursors.SizeWE : Cursors.SizeNS;
    }

    private static int Snap(int px, int span)
    {
        foreach (var f in SnapFractions)
        {
            int target = (int)Math.Round(span * f);
            if (Math.Abs(px - target) <= SnapPx) return target;
        }
        return px;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _drag = null;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) Close();
        else if (e.KeyCode == Keys.Enter) SaveAndClose();
    }

    private void SaveAndClose()
    {
        using var prompt = new NamePrompt(_layoutName, _overTaskbar);
        if (prompt.ShowDialog(this) != DialogResult.OK || prompt.Value.Length == 0) return;
        _overTaskbar = prompt.OverTaskbar;

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var zones = new List<Zone>();
        foreach (var (n, r) in Leaves())
        {
            string? name = n.Name;
            if (name == null || used.Contains(name))
            {
                int k = 1;
                do { name = "zone" + k++; } while (used.Contains(name));
            }
            used.Add(name);
            zones.Add(new Zone
            {
                Name = name,
                X = r.X + _screen.X,
                Y = r.Y + _screen.Y,
                Width = r.Width,
                Height = r.Height,
            });
        }
        _onSave(prompt.Value, zones, _overTaskbar);
        Close();
    }

    // ---- import: rebuild the split tree from an existing layout ----

    private Node? TryImport(IReadOnlyList<Zone> zones)
    {
        if (zones.Count == 0) return null;
        var items = zones.Select(z => (
            r: new Rectangle(z.X - _screen.X, z.Y - _screen.Y, z.Width, z.Height),
            name: z.Name)).ToList();
        return Build(items, RootRect);
    }

    /// <summary>Recursive guillotine-cut recovery; null if the rects don't tile the bounds.</summary>
    private static Node? Build(List<(Rectangle r, string name)> items, Rectangle bounds)
    {
        if (items.Count == 0) return null;
        if (items.Count == 1)
            return RectsClose(items[0].r, bounds) ? new Node { Name = items[0].name } : null;

        foreach (int x in items.Select(i => i.r.Right).Distinct())
        {
            if (x <= bounds.Left + 2 || x >= bounds.Right - 2) continue;
            var left = items.Where(i => i.r.Right <= x + 2).ToList();
            var right = items.Where(i => i.r.Left >= x - 2).ToList();
            if (left.Count == 0 || right.Count == 0 || left.Count + right.Count != items.Count) continue;
            var a = Build(left, new Rectangle(bounds.X, bounds.Y, x - bounds.X, bounds.Height));
            var b = Build(right, new Rectangle(x, bounds.Y, bounds.Right - x, bounds.Height));
            if (a != null && b != null)
                return new Node { Vertical = true, Ratio = (double)(x - bounds.X) / bounds.Width, A = a, B = b };
        }
        foreach (int y in items.Select(i => i.r.Bottom).Distinct())
        {
            if (y <= bounds.Top + 2 || y >= bounds.Bottom - 2) continue;
            var top = items.Where(i => i.r.Bottom <= y + 2).ToList();
            var bottom = items.Where(i => i.r.Top >= y - 2).ToList();
            if (top.Count == 0 || bottom.Count == 0 || top.Count + bottom.Count != items.Count) continue;
            var a = Build(top, new Rectangle(bounds.X, bounds.Y, bounds.Width, y - bounds.Y));
            var b = Build(bottom, new Rectangle(bounds.X, y, bounds.Width, bounds.Bottom - y));
            if (a != null && b != null)
                return new Node { Vertical = false, Ratio = (double)(y - bounds.Y) / bounds.Height, A = a, B = b };
        }
        return null;
    }

    private static bool RectsClose(Rectangle a, Rectangle b) =>
        Math.Abs(a.Left - b.Left) <= 2 && Math.Abs(a.Top - b.Top) <= 2 &&
        Math.Abs(a.Right - b.Right) <= 2 && Math.Abs(a.Bottom - b.Bottom) <= 2;

    // ---- painting ----

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        var leaves = Leaves();

        using var numberFont = new Font("Segoe UI", 44, FontStyle.Bold);
        using var infoFont = new Font("Segoe UI", 13);
        using var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

        for (int i = 0; i < leaves.Count; i++)
        {
            var (n, r) = leaves[i];
            var c = OverlayForm.Palette[i % OverlayForm.Palette.Length];
            using var fill = new SolidBrush(Color.FromArgb(46, c));
            using var pen = new Pen(c, 2);
            g.FillRectangle(fill, r);
            g.DrawRectangle(pen, Rectangle.Inflate(r, -1, -1));

            string label = $"{i + 1}";
            string info = $"{n.Name ?? "new zone"}\n{r.Width} × {r.Height}";
            var mid = new RectangleF(r.X, r.Y, r.Width, r.Height);
            using var white = new SolidBrush(Color.FromArgb(235, 235, 235));
            using var gray = new SolidBrush(Color.FromArgb(160, 160, 160));
            g.DrawString(label, numberFont, white, new RectangleF(mid.X, mid.Y - 30, mid.Width, mid.Height), center);
            g.DrawString(info, infoFont, gray, new RectangleF(mid.X, mid.Y + 45, mid.Width, mid.Height), center);
        }

        const string help = "Click: split   Shift+Click: split horizontally   Right-click: remove   " +
                            "Drag borders: resize   Enter: save   Esc: cancel";
        using var barBrush = new SolidBrush(Color.FromArgb(200, 12, 12, 14));
        using var helpBrush = new SolidBrush(Color.FromArgb(220, 220, 220));
        using var helpFont = new Font("Segoe UI", 12);
        var bar = new RectangleF(0, 0, Width, 40);
        g.FillRectangle(barBrush, bar);
        g.DrawString(help, helpFont, helpBrush, bar, center);
    }

    private sealed class NamePrompt : Form
    {
        private readonly TextBox _box = new();
        private readonly CheckBox _overTaskbar = new();
        public string Value => _box.Text.Trim();
        public bool OverTaskbar => _overTaskbar.Checked;

        public NamePrompt(string initial, bool overTaskbar)
        {
            Text = "Save layout";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(320, 136);
            MinimizeBox = MaximizeBox = false;
            ShowInTaskbar = false;
            TopMost = true;

            var label = new Label { Text = "Layout name (existing name overwrites):", Left = 12, Top = 10, AutoSize = true };
            _box.SetBounds(12, 32, 296, 24);
            _box.Text = initial;
            _box.SelectAll();
            _overTaskbar.Text = "Over taskbar (Quick Zones snap across it)";
            _overTaskbar.SetBounds(12, 62, 296, 24);
            _overTaskbar.Checked = overTaskbar;
            var ok = new Button { Text = "Save", DialogResult = DialogResult.OK };
            ok.SetBounds(152, 98, 75, 28);
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
            cancel.SetBounds(233, 98, 75, 28);
            AcceptButton = ok;
            CancelButton = cancel;
            Controls.AddRange(new Control[] { label, _box, _overTaskbar, ok, cancel });
        }
    }
}
