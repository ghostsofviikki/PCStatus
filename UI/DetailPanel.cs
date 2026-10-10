using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace PCStatus.UI;

/// <summary>
/// One panel row. With <paramref name="DownHistory"/> set, the sparkline is mirrored like the tray bar:
/// <paramref name="History"/> (load or upload) above the centre line in <paramref name="Accent"/>,
/// <paramref name="DownHistory"/> (RAM / VRAM or download) below in <paramref name="DownAccent"/>.
/// </summary>
public sealed record PanelRow(string Title, string Value, string Detail, History History, Color Accent, float Load,
    History? DownHistory = null, Color DownAccent = default);

/// <summary>Borderless popup shown above the tray: one row per metric with a 60 s sparkline.</summary>
public sealed class DetailPanel : Form
{
    private const int BaseWidth = 320;
    private const int Pad = 14;
    private const int RowHeight = 74;
    private const int RowGap = 10;
    private const int SparkHeight = 30;

    private IReadOnlyList<PanelRow> _rows = [];
    private Point _anchor;
    private float _fontScale;
    private Font? _titleFont, _valueFont, _detailFont;

    public DateTime LastHidden { get; private set; }

    public DetailPanel()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.PanelBg;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80;        // WS_EX_TOOLWINDOW: keep out of Alt+Tab
            cp.ClassStyle |= 0x20000;  // CS_DROPSHADOW
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => false;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
    }

    public void SetRows(IReadOnlyList<PanelRow> rows)
    {
        bool countChanged = rows.Count != _rows.Count;
        _rows = rows;
        if (!Visible) return;
        if (countChanged) Relayout();
        Invalidate();
    }

    /// <summary>Show above the given screen point (usually the cursor at click time).</summary>
    public void ShowAt(Point anchor)
    {
        _anchor = anchor;
        Location = anchor; // put it on the right monitor first so DeviceDpi is correct
        Show();
        Relayout();
        Activate();
        SetForegroundWindow(Handle);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        HidePanel();
    }

    public void HidePanel()
    {
        if (!Visible) return;
        Hide();
        LastHidden = DateTime.UtcNow;
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        Relayout();
    }

    private float S => DeviceDpi / 96f;

    private void Relayout()
    {
        float s = S;
        EnsureFonts(s);
        int w = (int)(BaseWidth * s);
        int h = (int)((Pad * 2 + _rows.Count * RowHeight + Math.Max(0, _rows.Count - 1) * RowGap) * s);

        var wa = Screen.FromPoint(_anchor).WorkingArea;
        int margin = (int)(12 * s);
        int x = Math.Clamp(_anchor.X - w / 2, wa.Left + margin, wa.Right - w - margin);
        // Taskbar is normally at the bottom; fall back to below the anchor if it's at the top.
        int y = _anchor.Y > wa.Top + wa.Height / 2 ? wa.Bottom - h - margin : wa.Top + margin;
        Bounds = new Rectangle(x, y, w, h);
        Invalidate();
    }

    private void EnsureFonts(float s)
    {
        if (_titleFont != null && Math.Abs(_fontScale - s) < 0.01f) return;
        _titleFont?.Dispose(); _valueFont?.Dispose(); _detailFont?.Dispose();
        _titleFont = new Font("Segoe UI Semibold", 13 * s, GraphicsUnit.Pixel);
        _valueFont = new Font("Segoe UI Semibold", 13 * s, GraphicsUnit.Pixel);
        _detailFont = new Font("Segoe UI", 11 * s, GraphicsUnit.Pixel);
        _fontScale = s;
    }

    protected override void OnPaint(PaintEventArgs e) => PaintRows(e.Graphics, ClientSize.Width, S);

    /// <summary>Renders the panel off-screen (used by --selftest).</summary>
    public Bitmap RenderToBitmap(IReadOnlyList<PanelRow> rows, float s)
    {
        _rows = rows;
        int w = (int)(BaseWidth * s);
        int h = (int)((Pad * 2 + rows.Count * RowHeight + Math.Max(0, rows.Count - 1) * RowGap) * s);
        var bmp = new Bitmap(w, h);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Theme.PanelBg);
        PaintRows(g, w, s);
        return bmp;
    }

    private void PaintRows(Graphics g, int width, float s)
    {
        EnsureFonts(s);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        float pad = Pad * s;
        float innerW = width - 2 * pad;
        float y = pad;

        using var primary = new SolidBrush(Theme.TextPrimary);
        using var secondary = new SolidBrush(Theme.TextSecondary);
        var right = new StringFormat { Alignment = StringAlignment.Far };

        foreach (var row in _rows)
        {
            using var accentDot = new SolidBrush(row.Accent);
            float dot = 8 * s;
            g.FillEllipse(accentDot, pad, y + 5 * s, dot, dot);
            g.DrawString(row.Title, _titleFont!, primary, pad + dot + 6 * s, y);

            using var valueBrush = new SolidBrush(row.Load >= 60 ? Theme.LoadColor(row.Load) : Theme.TextPrimary);
            g.DrawString(row.Value, _valueFont!, valueBrush, new RectangleF(pad, y, innerW, 20 * s), right);

            g.DrawString(row.Detail, _detailFont!, secondary, new RectangleF(pad, y + 20 * s, innerW, 16 * s),
                new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap });

            var spark = new RectangleF(pad, y + (RowHeight - SparkHeight) * s, innerW, SparkHeight * s);
            if (row.DownHistory != null)
                DrawMirroredSparkline(g, spark, row.History, row.Accent, row.DownHistory, row.DownAccent, s);
            else
                DrawSparkline(g, spark, row.History, row.Accent, s);

            y += (RowHeight + RowGap) * s;
        }
    }

    private static void DrawSparkline(Graphics g, RectangleF r, History h, Color accent, float s)
    {
        using (var bg = new SolidBrush(Color.FromArgb(26, 26, 26)))
            g.FillRectangle(bg, r);
        using (var grid = new Pen(Theme.SparkGrid, 1))
            g.DrawLine(grid, r.Left, r.Top + r.Height / 2, r.Right, r.Top + r.Height / 2);

        if (h.Count < 2) return;

        float step = r.Width / (h.Capacity - 1);
        var pts = new PointF[h.Count];
        for (int i = 0; i < h.Count; i++)
        {
            float x = r.Right - (h.Count - 1 - i) * step;
            float v = Math.Clamp(h[i], 0, 100) / 100f;
            pts[i] = new PointF(x, r.Bottom - v * (r.Height - 2 * s) - s);
        }

        var poly = new PointF[pts.Length + 2];
        pts.CopyTo(poly, 0);
        poly[^2] = new PointF(pts[^1].X, r.Bottom);
        poly[^1] = new PointF(pts[0].X, r.Bottom);
        using (var fill = new SolidBrush(Color.FromArgb(55, accent)))
            g.FillPolygon(fill, poly);
        using (var line = new Pen(accent, 1.5f * s) { LineJoin = LineJoin.Round })
            g.DrawLines(line, pts);
    }

    /// <summary>One series above the centre line, the other below; values are 0–100 (network already log-scaled).</summary>
    private static void DrawMirroredSparkline(Graphics g, RectangleF r, History up, Color upColor, History down, Color downColor, float s)
    {
        using (var bg = new SolidBrush(Color.FromArgb(26, 26, 26)))
            g.FillRectangle(bg, r);
        float mid = r.Top + r.Height / 2;
        using (var grid = new Pen(Theme.SparkGrid, 1))
            g.DrawLine(grid, r.Left, mid, r.Right, mid);

        DrawHalf(up, upColor, -1);
        DrawHalf(down, downColor, +1);

        void DrawHalf(History h, Color color, int dir)
        {
            if (h.Count < 2) return;
            float step = r.Width / (h.Capacity - 1);
            float half = r.Height / 2 - s;
            var pts = new PointF[h.Count];
            for (int i = 0; i < h.Count; i++)
            {
                float x = r.Right - (h.Count - 1 - i) * step;
                pts[i] = new PointF(x, mid + dir * Math.Clamp(h[i], 0, 100) / 100f * half);
            }
            var poly = new PointF[pts.Length + 2];
            pts.CopyTo(poly, 0);
            poly[^2] = new PointF(pts[^1].X, mid);
            poly[^1] = new PointF(pts[0].X, mid);
            using (var fill = new SolidBrush(Color.FromArgb(55, color)))
                g.FillPolygon(fill, poly);
            using (var line = new Pen(color, 1.5f * s) { LineJoin = LineJoin.Round })
                g.DrawLines(line, pts);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _titleFont?.Dispose(); _valueFont?.Dispose(); _detailFont?.Dispose();
        }
        base.Dispose(disposing);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}
