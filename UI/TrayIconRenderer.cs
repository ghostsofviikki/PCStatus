using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace PCStatus.UI;

/// <summary>Draws one vertical bar per metric into a tray-sized icon.</summary>
public sealed class TrayIconRenderer : IDisposable
{
    private IntPtr _lastHandle;
    private IntPtr _olderHandle;

    public Icon Render(IReadOnlyList<float> values, bool lightTaskbar)
    {
        using var bmp = RenderBitmap(values, lightTaskbar, SystemInformation.SmallIconSize.Width);

        // Icon.FromHandle doesn't own the HICON. The caller is still showing the previous icon
        // while this one is created, so free the one from two renders ago.
        IntPtr handle = bmp.GetHicon();
        if (_olderHandle != IntPtr.Zero)
            DestroyIcon(_olderHandle);
        _olderHandle = _lastHandle;
        _lastHandle = handle;
        return Icon.FromHandle(handle);
    }

    public static Bitmap RenderBitmap(IReadOnlyList<float> values, bool lightTaskbar, int size)
    {
        var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.None;

            int n = Math.Max(1, values.Count);
            int gap = size >= 24 ? 2 : 1;
            int barW = Math.Max(1, (size - (n - 1) * gap) / n);
            int used = n * barW + (n - 1) * gap;
            int x = (size - used) / 2;

            using var track = new SolidBrush(lightTaskbar ? Color.FromArgb(55, 0, 0, 0) : Color.FromArgb(70, 255, 255, 255));
            foreach (float v in values)
            {
                g.FillRectangle(track, x, 0, barW, size);
                int h = (int)Math.Round(Math.Clamp(v, 0, 100) / 100f * size);
                if (v > 0.5f) h = Math.Max(h, 1);
                using var fill = new SolidBrush(Theme.LoadColor(v));
                g.FillRectangle(fill, x, size - h, barW, h);
                x += barW + gap;
            }
        }
        return bmp;
    }

    public void Dispose()
    {
        if (_olderHandle != IntPtr.Zero) DestroyIcon(_olderHandle);
        if (_lastHandle != IntPtr.Zero) DestroyIcon(_lastHandle);
        _olderHandle = _lastHandle = IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
