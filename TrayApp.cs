using Microsoft.Win32;
using PCStatus.Sensors;
using PCStatus.UI;

namespace PCStatus;

public sealed class TrayApp : ApplicationContext
{
    private readonly SensorService _sensors = new();
    private readonly TrayIconRenderer _renderer = new();
    private readonly NotifyIcon _tray;
    private readonly DetailPanel _panel = new();
    private readonly ToolStripMenuItem _autostartItem;
    private readonly SynchronizationContext _ui;
    private readonly CancellationTokenSource _cts = new();

    private readonly History _cpuHistory = new();
    private readonly History _ramHistory = new();
    private readonly List<History> _gpuHistories;
    private readonly string _cpuName = ReadCpuName();

    private SensorSnapshot? _last;
    private bool _lightTaskbar = Theme.TaskbarIsLight();

    public TrayApp()
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _gpuHistories = _sensors.Gpus.Select(_ => new History()).ToList();

        _autostartItem = new ToolStripMenuItem("Start with Windows", null, OnToggleAutostart)
        {
            Checked = Autostart.IsEnabled(),
        };
        var menu = new ContextMenuStrip();
        menu.Items.Add(_autostartItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _tray = new NotifyIcon
        {
            Text = "PCStatus",
            Icon = _renderer.Render(new float[2 + _sensors.Gpus.Count], _lightTaskbar),
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.MouseClick += OnTrayClick;

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        _sampleLoop = Task.Run(() => SampleLoop(_cts.Token));
    }

    private readonly Task _sampleLoop;

    private async Task SampleLoop(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        do
        {
            try
            {
                var snap = _sensors.Sample();
                _ui.Post(_ => OnSnapshot(snap), null);
            }
            catch
            {
                // Keep going; a single failed sample shouldn't kill the app.
            }
        }
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
    }

    private void OnSnapshot(SensorSnapshot snap)
    {
        if (_cts.IsCancellationRequested) return;
        _last = snap;

        _cpuHistory.Add(snap.CpuPct);
        _ramHistory.Add(snap.RamPct);
        for (int i = 0; i < snap.Gpus.Count && i < _gpuHistories.Count; i++)
            _gpuHistories[i].Add(snap.Gpus[i].LoadPct);

        var values = new List<float> { snap.CpuPct };
        values.AddRange(snap.Gpus.Select(g => g.LoadPct));
        values.Add(snap.RamPct);
        _tray.Icon = _renderer.Render(values, _lightTaskbar);
        _tray.Text = BuildTooltip(snap);

        if (_panel.Visible)
            _panel.SetRows(BuildRows(snap));
    }

    private static string BuildTooltip(SensorSnapshot s)
    {
        var parts = new List<string> { $"CPU {s.CpuPct:0}%{Deg(s.CpuTempC)}" };
        parts.AddRange(s.Gpus.Select(g => $"{g.Info.ShortName} {g.LoadPct:0}%{Deg(g.TempC)}"));
        parts.Add($"RAM {s.RamPct:0}%");
        string text = string.Join(" | ", parts);
        return text.Length > 127 ? text[..127] : text;

        static string Deg(float? t) => t is { } v ? $" {v:0}°" : "";
    }

    private List<PanelRow> BuildRows(SensorSnapshot s) => BuildRows(s, _cpuName, _cpuHistory, _gpuHistories, _ramHistory);

    internal static List<PanelRow> BuildRows(SensorSnapshot s, string cpuName, History cpuHistory, List<History> gpuHistories, History ramHistory)
    {
        var rows = new List<PanelRow>();

        string cpuTemp = s.CpuTempC is { } ct ? $"  ·  {ct:0}°C" : "";
        string cpuDetail = s.CpuTempC is null && !s.PawnIoInstalled ? $"{cpuName} · no temp (PawnIO)" : cpuName;
        rows.Add(new PanelRow("CPU", $"{s.CpuPct:0}%{cpuTemp}", cpuDetail, cpuHistory, Theme.AccentCpu, s.CpuPct));

        for (int i = 0; i < s.Gpus.Count; i++)
        {
            var g = s.Gpus[i];
            string temp = g.TempC is { } t ? $"  ·  {t:0}°C" : g.TempSkippedIdle ? "  ·  idle" : "";
            string mem = g.Info.IsIntegrated
                ? $"{g.MemUsedGB:0.0} / {g.MemTotalGB:0.0} GB shared"
                : $"{g.MemUsedGB:0.0} / {g.MemTotalGB:0.0} GB VRAM";
            var accent = Theme.AccentGpus[i % Theme.AccentGpus.Length];
            rows.Add(new PanelRow(g.Info.ShortName, $"{g.LoadPct:0}%{temp}", $"{g.Info.Name} · {mem}", gpuHistories[i], accent, g.LoadPct));
        }

        rows.Add(new PanelRow("RAM", $"{s.RamPct:0}%", $"{s.RamUsedGB:0.0} / {s.RamTotalGB:0.0} GB", ramHistory, Theme.AccentRam, s.RamPct));
        return rows;
    }

    private void OnTrayClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;

        if (_panel.Visible)
        {
            _panel.HidePanel();
            return;
        }
        // Clicking the icon while the panel is open first deactivates (hides) it; don't reopen right away.
        if ((DateTime.UtcNow - _panel.LastHidden).TotalMilliseconds < 300)
            return;

        if (_last != null)
            _panel.SetRows(BuildRows(_last));
        _panel.ShowAt(Cursor.Position);
    }

    private void OnToggleAutostart(object? sender, EventArgs e)
    {
        bool ok = _autostartItem.Checked ? Autostart.Disable() : Autostart.Enable();
        if (!ok)
            MessageBox.Show("Couldn't update the scheduled task.", "PCStatus", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        _autostartItem.Checked = Autostart.IsEnabled();
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
            _lightTaskbar = Theme.TaskbarIsLight();
    }

    internal static string ReadCpuName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return key?.GetValue("ProcessorNameString") is string name ? GpuEnumerator.CleanName(name) : "Processor";
        }
        catch
        {
            return "Processor";
        }
    }

    protected override void ExitThreadCore()
    {
        _cts.Cancel();
        try { _sampleLoop.Wait(TimeSpan.FromSeconds(3)); } catch { }
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _tray.Visible = false;
        _tray.Dispose();
        _panel.Dispose();
        _renderer.Dispose();
        _sensors.Dispose();
        base.ExitThreadCore();
    }
}
