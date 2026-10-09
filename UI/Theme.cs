using Microsoft.Win32;

namespace PCStatus.UI;

public static class Theme
{
    public static readonly Color Green = Color.FromArgb(76, 194, 110);
    public static readonly Color Yellow = Color.FromArgb(242, 194, 48);
    public static readonly Color Red = Color.FromArgb(229, 83, 75);

    public static readonly Color PanelBg = Color.FromArgb(32, 32, 32);
    public static readonly Color PanelBorder = Color.FromArgb(60, 60, 60);
    public static readonly Color TextPrimary = Color.FromArgb(240, 240, 240);
    public static readonly Color TextSecondary = Color.FromArgb(150, 150, 150);
    public static readonly Color SparkGrid = Color.FromArgb(48, 48, 48);

    public static readonly Color AccentCpu = Color.FromArgb(74, 158, 255);
    public static readonly Color AccentRam = Color.FromArgb(255, 160, 70);
    public static readonly Color NetDown = Color.FromArgb(64, 196, 255);
    public static readonly Color NetUp = Color.FromArgb(176, 128, 255);
    public static readonly Color[] AccentGpus =
    [
        Color.FromArgb(76, 194, 110),
        Color.FromArgb(180, 120, 255),
        Color.FromArgb(255, 110, 170),
        Color.FromArgb(80, 210, 210),
    ];

    public static Color LoadColor(float pct) => pct >= 85 ? Red : pct >= 60 ? Yellow : Green;

    /// <summary>True when the taskbar uses the light theme.</summary>
    public static bool TaskbarIsLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int v && v == 1;
        }
        catch
        {
            return false;
        }
    }
}
