using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace PCStatus.UI;

/// <summary>
/// One bar in the tray icon: either a load bar (0–100, filled from the bottom) or a split network bar
/// (upload grows up from the middle, download grows down from it; fractions 0–1).
/// </summary>
public readonly record struct TrayBar(float Load, float? UpFraction = null, float? DownFraction = null)
{
    public static TrayBar ForLoad(float pct) => new(pct);
    public static TrayBar ForNetwork(double upBps, double downBps) => new(0, NetFraction(upBps), NetFraction(downBps));

    public bool IsNetwork => UpFraction.HasValue;

    /// <summary>Log scale: 1 KB/s = 0, 100 MB/s = 1, so light and heavy traffic are both visible.</summary>
    public static float NetFraction(double bytesPerSec)
    {
        const double min = 1024, max = 100 * 1024 * 1024;
        if (bytesPerSec <= min) return 0;
        return (float)Math.Clamp(Math.Log10(bytesPerSec / min) / Math.Log10(max / min), 0, 1);
    }
}

/// <summary>Draws one vertical bar per metric into a tray-sized icon.</summary>
public sealed class TrayIconRenderer : IDisposable
{
    private IntPtr _lastHandle;
    private IntPtr _olderHandle;

    public Icon Render(IReadOnlyList<TrayBar> bars, bool lightTaskbar)
    {
        using var bmp = RenderBitmap(bars, lightTaskbar, SystemInformation.SmallIconSize.Width);

        // Icon.FromHandle doesn't own the HICON. The caller is still showing the previous icon
        // while this one is created, so free the one from two renders ago.
        IntPtr handle = bmp.GetHicon();
        if (_olderHandle != IntPtr.Zero)
            DestroyIcon(_olderHandle);
        _olderHandle = _lastHandle;
        _lastHandle = handle;
        return Icon.FromHandle(handle);
    }

    public static Bitmap RenderBitmap(IReadOnlyList<float> loads, bool lightTaskbar, int size) =>
        RenderBitmap(loads.Select(TrayBar.ForLoad).ToList(), lightTaskbar, size);

    public static Bitmap RenderBitmap(IReadOnlyList<TrayBar> bars, bool lightTaskbar, int size)
    {
        var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.None;

            int n = Math.Max(1, bars.Count);
            int gap = size >= 24 ? 2 : 1;
            int barW = Math.Max(1, (size - (n - 1) * gap) / n);
            int used = n * barW + (n - 1) * gap;
            int x = (size - used) / 2;

            using var track = new SolidBrush(lightTaskbar ? Color.FromArgb(55, 0, 0, 0) : Color.FromArgb(70, 255, 255, 255));
            using var midLine = new SolidBrush(lightTaskbar ? Color.FromArgb(110, 0, 0, 0) : Color.FromArgb(130, 255, 255, 255));
            foreach (var bar in bars)
            {
                g.FillRectangle(track, x, 0, barW, size);
                if (bar.IsNetwork)
                {
                    int mid = size / 2;
                    int upH = Height(bar.UpFraction!.Value, mid);
                    int downH = Height(bar.DownFraction!.Value, size - mid);
                    using var up = new SolidBrush(Theme.NetUp);
                    using var down = new SolidBrush(Theme.NetDown);
                    g.FillRectangle(up, x, mid - upH, barW, upH);
                    g.FillRectangle(down, x, mid, barW, downH);
                    // Faint centre line so the network bar is recognisable even when idle.
                    if (upH == 0 && downH == 0)
                        g.FillRectangle(midLine, x, mid, barW, 1);
                }
                else
                {
                    int h = (int)Math.Round(Math.Clamp(bar.Load, 0, 100) / 100f * size);
                    if (bar.Load > 0.5f) h = Math.Max(h, 1);
                    using var fill = new SolidBrush(Theme.LoadColor(bar.Load));
                    g.FillRectangle(fill, x, size - h, barW, h);
                }
                x += barW + gap;
            }
        }
        return bmp;
    }

    private static int Height(float fraction, int max) =>
        fraction <= 0 ? 0 : Math.Max(1, (int)Math.Round(fraction * max));

    public void Dispose()
    {
        if (_olderHandle != IntPtr.Zero) DestroyIcon(_olderHandle);
        if (_lastHandle != IntPtr.Zero) DestroyIcon(_lastHandle);
        _olderHandle = _lastHandle = IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
