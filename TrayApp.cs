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
    private readonly History _netUpHistory = new();   // log-scaled 0–100, see TrayBar.NetFraction
    private readonly History _netDownHistory = new();
    private readonly Dictionary<long, History> _gpuHistories = new(); // by adapter LUID; GPUs can come and go
    private readonly string _cpuName = ReadCpuName();

    private SensorSnapshot? _last;
    private bool _lightTaskbar = Theme.TaskbarIsLight();

    public TrayApp()
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

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
            Icon = _renderer.Render(BuildBars(null, _sensors.Gpus.Count), _lightTaskbar),
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
        AddGpuSamples(_gpuHistories, snap);
        _netUpHistory.Add(TrayBar.NetFraction(snap.NetUpBps) * 100);
        _netDownHistory.Add(TrayBar.NetFraction(snap.NetDownBps) * 100);

        _tray.Icon = _renderer.Render(BuildBars(snap, snap.Gpus.Count), _lightTaskbar);
        _tray.Text = BuildTooltip(snap);

        if (_panel.Visible)
            _panel.SetRows(BuildRows(snap));
    }

    internal static void AddGpuSamples(Dictionary<long, History> histories, SensorSnapshot snap)
    {
        foreach (var g in snap.Gpus)
        {
            if (!histories.TryGetValue(g.Info.Luid, out var h))
                histories[g.Info.Luid] = h = new History();
            h.Add(g.LoadPct);
        }
        foreach (var gone in histories.Keys.Where(l => snap.Gpus.All(g => g.Info.Luid != l)).ToList())
            histories.Remove(gone);
    }

    /// <summary>CPU, one bar per GPU, RAM, network. A null snapshot gives empty bars (startup).</summary>
    internal static List<TrayBar> BuildBars(SensorSnapshot? s, int gpuCount)
    {
        var bars = new List<TrayBar> { TrayBar.ForLoad(s?.CpuPct ?? 0) };
        for (int i = 0; i < gpuCount; i++)
            bars.Add(TrayBar.ForLoad(s != null && i < s.Gpus.Count ? s.Gpus[i].LoadPct : 0));
        bars.Add(TrayBar.ForLoad(s?.RamPct ?? 0));
        bars.Add(TrayBar.ForNetwork(s?.NetUpBps ?? 0, s?.NetDownBps ?? 0));
        return bars;
    }

    internal static string FormatRate(double bytesPerSec) => bytesPerSec switch
    {
        < 1024 => $"{bytesPerSec:0} B/s",
        < 1024 * 1024 => $"{bytesPerSec / 1024:0} KB/s",
        < 1024 * 1024 * 1024 => $"{bytesPerSec / (1024 * 1024):0.0} MB/s",
        _ => $"{bytesPerSec / (1024 * 1024 * 1024):0.00} GB/s",
    };

    private static string BuildTooltip(SensorSnapshot s)
    {
        var parts = new List<string> { $"CPU {s.CpuPct:0}%{Deg(s.CpuTempC)}" };
        parts.AddRange(s.Gpus.Select(g => $"{g.Info.ShortName} {g.LoadPct:0}%{Deg(g.TempC)}"));
        parts.Add($"RAM {s.RamPct:0}%");
        parts.Add($"↓ {FormatRate(s.NetDownBps)} ↑ {FormatRate(s.NetUpBps)}");
        string text = string.Join(" | ", parts);
        return text.Length > 127 ? text[..127] : text;

        static string Deg(float? t) => t is { } v ? $" {v:0}°" : "";
    }

    private List<PanelRow> BuildRows(SensorSnapshot s) =>
        BuildRows(s, _cpuName, _cpuHistory, _gpuHistories, _ramHistory, _netUpHistory, _netDownHistory);

    internal static List<PanelRow> BuildRows(SensorSnapshot s, string cpuName, History cpuHistory, Dictionary<long, History> gpuHistories,
        History ramHistory, History netUpHistory, History netDownHistory)
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
            var history = gpuHistories.TryGetValue(g.Info.Luid, out var h) ? h : new History();
            rows.Add(new PanelRow(g.Info.ShortName, $"{g.LoadPct:0}%{temp}", $"{g.Info.Name} · {mem}", history, accent, g.LoadPct));
        }

        rows.Add(new PanelRow("RAM", $"{s.RamPct:0}%", $"{s.RamUsedGB:0.0} / {s.RamTotalGB:0.0} GB", ramHistory, Theme.AccentRam, s.RamPct));

        string adapters = s.NetAdapters.Length > 0 ? s.NetAdapters : "No active network adapter";
        rows.Add(new PanelRow("Network", $"↓ {FormatRate(s.NetDownBps)}   ↑ {FormatRate(s.NetUpBps)}",
            $"{adapters} · ↑ upload above, ↓ download below", netUpHistory, Theme.NetUp, 0, netDownHistory, Theme.NetDown));
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
