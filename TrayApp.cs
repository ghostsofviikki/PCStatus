using System.Diagnostics;
using System.Runtime.InteropServices;
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
    private readonly Dictionary<long, History> _gpuMemHistories = new();
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
        menu.Items.Add("Exit", null, (_, _) => { Log.Write("Exit chosen from tray menu"); ExitThread(); });

        // We usually run elevated, and UIPI then blocks Explorer's "TaskbarCreated" broadcast. Without it the icon
        // is never re-added after Explorer restarts (which can happen around sleep/lock) and the app looks gone.
        ChangeWindowMessageFilter(RegisterWindowMessage("TaskbarCreated"), MsgfltAdd);

        _tray = new NotifyIcon
        {
            Text = "PCStatus",
            Icon = RenderIcon(null),
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.MouseClick += OnTrayClick;

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionEnding += OnSessionEnding;
        _sampleLoop = Task.Run(() => SampleLoop(_cts.Token));
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        Log.Write($"Session {e.Reason}");
        if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect)
            ReAddTrayIcon();
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        Log.Write($"Power {e.Mode}");
        if (e.Mode == PowerModes.Resume)
            ReAddTrayIcon();
    }

    private void OnSessionEnding(object? sender, SessionEndingEventArgs e) => Log.Write($"Session ending ({e.Reason})");

    /// <summary>Re-registers the tray icon in case Explorer dropped it while the PC was locked or asleep.</summary>
    private void ReAddTrayIcon()
    {
        try
        {
            _tray.Visible = false;
            _tray.Visible = true;
        }
        catch (Exception ex)
        {
            Log.Error("re-adding tray icon", ex);
        }
    }

    private readonly Task _sampleLoop;

    private async Task SampleLoop(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var heartbeat = Stopwatch.StartNew();
        int errors = 0;
        do
        {
            try
            {
                var snap = _sensors.Sample();
                _ui.Post(_ => OnSnapshot(snap), null);
            }
            catch (Exception ex)
            {
                // Keep going; a single failed sample shouldn't kill the app. Log the first few only.
                if (++errors <= 20)
                    Log.Error("sampling", ex);
            }

            if (heartbeat.Elapsed >= TimeSpan.FromMinutes(10))
            {
                heartbeat.Restart();
                using var me = Process.GetCurrentProcess();
                Log.Write($"alive: gpus={_sensors.Gpus.Count} private={me.PrivateMemorySize64 / 1048576} MB handles={me.HandleCount} sampleErrors={errors}");
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
        AddGpuSamples(_gpuHistories, _gpuMemHistories, snap);
        _netUpHistory.Add(TrayBar.NetFraction(snap.NetUpBps) * 100);
        _netDownHistory.Add(TrayBar.NetFraction(snap.NetDownBps) * 100);

        _tray.Icon = RenderIcon(snap);
        _tray.Text = BuildTooltip(snap);

        if (_panel.Visible)
            _panel.SetRows(BuildRows(snap));
    }

    internal static void AddGpuSamples(Dictionary<long, History> loadHistories, Dictionary<long, History> memHistories, SensorSnapshot snap)
    {
        foreach (var g in snap.Gpus)
        {
            Add(loadHistories, g.LoadPct);
            Add(memHistories, GpuMemPct(g));

            void Add(Dictionary<long, History> histories, float value)
            {
                if (!histories.TryGetValue(g.Info.Luid, out var h))
                    histories[g.Info.Luid] = h = new History();
                h.Add(value);
            }
        }
        foreach (var histories in new[] { loadHistories, memHistories })
            foreach (var gone in histories.Keys.Where(l => snap.Gpus.All(g => g.Info.Luid != l)).ToList())
                histories.Remove(gone);
    }

    private Icon RenderIcon(SensorSnapshot? s) =>
        _renderer.Render(BuildBars(s, s?.Gpus.Count ?? _sensors.Gpus.Count), _lightTaskbar);

    /// <summary>
    /// CPU load above the middle of its bar with RAM below, each GPU's load above its own VRAM, then network.
    /// A null snapshot gives empty bars (startup).
    /// </summary>
    internal static List<TrayBar> BuildBars(SensorSnapshot? s, int gpuCount)
    {
        var bars = new List<TrayBar> { TrayBar.ForLoadAndMemory(s?.CpuPct ?? 0, s?.RamPct ?? 0) };
        for (int i = 0; i < gpuCount; i++)
        {
            var g = s != null && i < s.Gpus.Count ? s.Gpus[i] : null;
            bars.Add(TrayBar.ForLoadAndMemory(g?.LoadPct ?? 0, GpuMemPct(g)));
        }
        bars.Add(TrayBar.ForNetwork(s?.NetUpBps ?? 0, s?.NetDownBps ?? 0));
        return bars;
    }

    private static float GpuMemPct(GpuReading? g) =>
        g is { MemTotalGB: > 0 } ? (float)(g.MemUsedGB / g.MemTotalGB * 100) : 0;

    internal static string FormatRate(double bytesPerSec) => bytesPerSec switch
    {
        < 1024 => $"{bytesPerSec:0} B/s",
        < 1024 * 1024 => $"{bytesPerSec / 1024:0} KB/s",
        < 1024 * 1024 * 1024 => $"{bytesPerSec / (1024 * 1024):0.0} MB/s",
        _ => $"{bytesPerSec / (1024 * 1024 * 1024):0.00} GB/s",
    };

    /// <summary>One line per bar, each device with its memory, e.g. "RTX 4090 12% 45° · VRAM 3.1/24.0 GB".</summary>
    internal static string BuildTooltip(SensorSnapshot s)
    {
        var lines = new List<string> { $"CPU {s.CpuPct:0}%{Deg(s.CpuTempC)} · RAM {s.RamUsedGB:0.0}/{s.RamTotalGB:0.0} GB" };
        lines.AddRange(s.Gpus.Select(g =>
            $"{g.Info.ShortName} {g.LoadPct:0}%{Deg(g.TempC)} · {(g.Info.IsIntegrated ? "Shared" : "VRAM")} {g.MemUsedGB:0.0}/{g.MemTotalGB:0.0} GB"));
        lines.Add($"↓ {FormatRate(s.NetDownBps)} ↑ {FormatRate(s.NetUpBps)}");
        string text = string.Join("\n", lines);
        return text.Length > 127 ? text[..127] : text; // NotifyIcon.Text limit

        static string Deg(float? t) => t is { } v ? $" {v:0}°" : "";
    }

    private List<PanelRow> BuildRows(SensorSnapshot s) =>
        BuildRows(s, _cpuName, _cpuHistory, _gpuHistories, _gpuMemHistories, _ramHistory, _netUpHistory, _netDownHistory);

    internal static List<PanelRow> BuildRows(SensorSnapshot s, string cpuName, History cpuHistory, Dictionary<long, History> gpuHistories,
        Dictionary<long, History> gpuMemHistories, History ramHistory, History netUpHistory, History netDownHistory)
    {
        var rows = new List<PanelRow>();

        string cpuTemp = s.CpuTempC is { } ct ? $"  ·  {ct:0}°C" : "";
        string cpuDetail = s.CpuTempC is null && !s.PawnIoInstalled ? $"{cpuName} · no temp (PawnIO)" : cpuName;
        cpuDetail += $" · RAM {s.RamUsedGB:0.0} / {s.RamTotalGB:0.0} GB";
        rows.Add(new PanelRow("CPU", $"{s.CpuPct:0}%{cpuTemp}  ·  RAM {s.RamPct:0}%", cpuDetail, cpuHistory, Theme.AccentCpu, s.CpuPct,
            ramHistory, Theme.AccentRam));

        for (int i = 0; i < s.Gpus.Count; i++)
        {
            var g = s.Gpus[i];
            string temp = g.TempC is { } t ? $"  ·  {t:0}°C" : g.TempSkippedIdle ? "  ·  idle" : "";
            string memLabel = g.Info.IsIntegrated ? "Shared" : "VRAM";
            var accent = Theme.AccentGpus[i % Theme.AccentGpus.Length];
            var history = gpuHistories.TryGetValue(g.Info.Luid, out var h) ? h : new History();
            var memHistory = gpuMemHistories.TryGetValue(g.Info.Luid, out var mh) ? mh : new History();
            rows.Add(new PanelRow(g.Info.ShortName, $"{g.LoadPct:0}%{temp}  ·  {memLabel} {GpuMemPct(g):0}%",
                $"{g.Info.Name} · {memLabel} {g.MemUsedGB:0.0} / {g.MemTotalGB:0.0} GB", history, accent, g.LoadPct, memHistory, Theme.AccentRam));
        }

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
        Log.Write("Shutting down");
        _cts.Cancel();
        try { _sampleLoop.Wait(TimeSpan.FromSeconds(3)); } catch { }
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionEnding -= OnSessionEnding;
        _tray.Visible = false;
        _tray.Dispose();
        _panel.Dispose();
        _renderer.Dispose();
        _sensors.Dispose();
        base.ExitThreadCore();
    }

    private const uint MsgfltAdd = 1;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll")]
    private static extern bool ChangeWindowMessageFilter(uint message, uint flag);
}
