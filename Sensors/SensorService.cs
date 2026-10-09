using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;

namespace PCStatus.Sensors;

/// <summary>Owns all sensor sources and produces one <see cref="SensorSnapshot"/> per call.</summary>
public sealed class SensorService : IDisposable
{
    // Any source can be missing on some machines (no GPU, VMs, disabled perf counters); degrade instead of failing.
    private readonly PdhCounters? _pdh = TryCreate(() => new PdhCounters());
    // GPUs can come and go (eGPU plugged in or out), so the list is re-scanned every few seconds.
    private List<GpuInfo> _gpus = TryCreate(GpuEnumerator.Enumerate) ?? [];
    private DateTime _nextGpuScan = DateTime.UtcNow + GpuScanInterval;
    private static readonly TimeSpan GpuScanInterval = TimeSpan.FromSeconds(5);
    private readonly CpuTimes _cpuTimes = new();
    private readonly NetworkCounters _net = new();
    private readonly bool _pawnIoInstalled = IsPawnIoInstalled();

    private Computer? _lhmCpuComputer;
    private IHardware? _lhmCpu;
    private volatile bool _lhmCpuReady;
    // Replaced as a whole (never mutated) when GPUs change, so the sampling thread always sees a consistent pair.
    private volatile LhmGpus? _lhmGpus;
    private sealed record LhmGpus(Computer Computer, Dictionary<long, IHardware> ByLuid);
    private int _lhmGpuInitRunning;

    private static readonly TimeSpan GpuTempHold = TimeSpan.FromSeconds(30);
    private readonly Dictionary<long, (float temp, DateTime at)> _lastGpuTemp = new();

    public IReadOnlyList<GpuInfo> Gpus => _gpus;

    public SensorService()
    {
        // LHM's Open() can take a second or two; don't block startup.
        Task.Run(InitLhmCpu);
        StartLhmGpuInit(_gpus);
    }

    private void InitLhmCpu()
    {
        try
        {
            // Separate Computer instances for CPU and GPU: one combined instance used ~4x the memory of both apart.
            var cpuComputer = new Computer { IsCpuEnabled = true };
            cpuComputer.Open();
            _lhmCpuComputer = cpuComputer;
            _lhmCpu = cpuComputer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
            _lhmCpuReady = true;
        }
        catch
        {
            // CPU temperature simply stays unavailable.
        }
    }

    private void StartLhmGpuInit(List<GpuInfo> gpus)
    {
        if (Interlocked.Exchange(ref _lhmGpuInitRunning, 1) == 1)
        {
            _gpuRescanPending = true; // picked up when the running init finishes
            return;
        }
        Task.Run(() =>
        {
            try { InitLhmGpus(gpus); }
            finally
            {
                Interlocked.Exchange(ref _lhmGpuInitRunning, 0);
                if (_gpuRescanPending)
                {
                    _gpuRescanPending = false;
                    StartLhmGpuInit(_gpus);
                }
            }
        });
    }

    private volatile bool _gpuRescanPending;

    private void InitLhmGpus(List<GpuInfo> gpus)
    {
        Computer? c = null;
        try
        {
            c = new Computer { IsGpuEnabled = true };
            c.Open();
            var byLuid = new Dictionary<long, IHardware>();
            var lhmGpus = c.Hardware.Where(h => h.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuIntel or HardwareType.GpuAmd).ToList();
            foreach (var g in gpus)
            {
                var type = g.VendorId switch
                {
                    GpuEnumerator.VendorNvidia => HardwareType.GpuNvidia,
                    GpuEnumerator.VendorIntel => HardwareType.GpuIntel,
                    GpuEnumerator.VendorAmd => HardwareType.GpuAmd,
                    _ => (HardwareType?)null,
                };
                var candidates = lhmGpus.Where(h => h.HardwareType == type && !byLuid.ContainsValue(h)).ToList();
                var match = candidates.FirstOrDefault(h => NamesMatch(h.Name, g.Name)) ?? (candidates.Count == 1 ? candidates[0] : null);
                if (match != null)
                    byLuid[g.Luid] = match;
            }
            var old = _lhmGpus;
            _lhmGpus = new LhmGpus(c, byLuid);
            c = null;
            // Give an in-flight Sample() a moment to finish with the old instance before closing it.
            if (old != null)
                Task.Delay(TimeSpan.FromSeconds(3)).ContinueWith(_ => { try { old.Computer.Close(); } catch { } });
        }
        catch
        {
            // GPU temperatures simply stay unavailable.
        }
        finally
        {
            try { c?.Close(); } catch { }
        }
    }

    /// <summary>Re-enumerates GPUs; when the set changes, LHM is re-opened so new GPUs get temperatures.</summary>
    private void RescanGpusIfDue()
    {
        if (DateTime.UtcNow < _nextGpuScan) return;
        _nextGpuScan = DateTime.UtcNow + GpuScanInterval;

        var fresh = TryCreate(GpuEnumerator.Enumerate);
        if (fresh == null) return;
        if (fresh.Select(g => g.Luid).SequenceEqual(_gpus.Select(g => g.Luid))) return;

        _gpus = fresh;
        foreach (var gone in _lastGpuTemp.Keys.Where(l => fresh.All(g => g.Luid != l)).ToList())
            _lastGpuTemp.Remove(gone);
        StartLhmGpuInit(fresh);
    }

    private static bool NamesMatch(string a, string b) =>
        a.Contains(b, StringComparison.OrdinalIgnoreCase) || b.Contains(a, StringComparison.OrdinalIgnoreCase);

    public SensorSnapshot Sample()
    {
        RescanGpusIfDue();
        _pdh?.Collect();
        float cpuFallback = _cpuTimes.Percent();
        float cpu = _pdh?.HasCpuCounter == true ? _pdh.CpuPercent() : cpuFallback;
        var load = _pdh?.GpuLoadByLuid() ?? [];
        var dedicated = _pdh?.GpuDedicatedBytes() ?? [];
        var shared = _pdh?.GpuSharedBytes() ?? [];

        float? cpuTemp = null;
        if (_lhmCpuReady && _lhmCpu != null)
        {
            try
            {
                _lhmCpu.Update();
                cpuTemp = PickCpuTemp(_lhmCpu);
            }
            catch { }
        }

        var lhmGpus = _lhmGpus;
        var gpus = new List<GpuReading>(_gpus.Count);
        foreach (var g in _gpus)
        {
            float l = load.GetValueOrDefault(g.Luid);
            double used = g.IsIntegrated ? shared.GetValueOrDefault(g.Luid) : dedicated.GetValueOrDefault(g.Luid);
            double total = g.IsIntegrated ? g.SharedGB : g.DedicatedGB;

            float? temp = null;
            bool skippedIdle = false;
            if (lhmGpus != null && lhmGpus.ByLuid.TryGetValue(g.Luid, out var hw))
            {
                // Querying a powered-down hybrid dGPU wakes it up, so only ask while it's doing work.
                // Keep showing the last reading for a while so the value doesn't flicker to "idle".
                if (!g.IsIntegrated && l < 0.5f)
                {
                    if (_lastGpuTemp.TryGetValue(g.Luid, out var lastTemp) && DateTime.UtcNow - lastTemp.at < GpuTempHold)
                        temp = lastTemp.temp;
                    else
                        skippedIdle = true;
                }
                else
                {
                    try
                    {
                        hw.Update();
                        temp = PickGpuTemp(hw);
                        if (temp is { } t)
                            _lastGpuTemp[g.Luid] = (t, DateTime.UtcNow);
                    }
                    catch { }
                }
            }
            gpus.Add(new GpuReading(g, l, temp, skippedIdle, used / 1073741824.0, total));
        }

        var (ramPct, ramUsed, ramTotal) = ReadRam();

        (double down, double up, string adapters) net = (0, 0, "");
        try { net = _net.Sample(); } catch { }

        return new SensorSnapshot(cpu, cpuTemp, ramPct, ramUsed, ramTotal, gpus, _pawnIoInstalled,
            net.down, net.up, net.adapters);
    }

    private static float? PickCpuTemp(IHardware cpu)
    {
        var temps = cpu.Sensors.Where(s => s.SensorType == SensorType.Temperature && s.Value is > 0 and < 150).ToList();
        return (temps.FirstOrDefault(s => s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase))
             ?? temps.FirstOrDefault(s => s.Name.Contains("Core Max", StringComparison.OrdinalIgnoreCase))
             ?? temps.FirstOrDefault(s => s.Name.Contains("Tctl", StringComparison.OrdinalIgnoreCase))
             ?? temps.FirstOrDefault())?.Value;
    }

    private static float? PickGpuTemp(IHardware gpu)
    {
        var temps = gpu.Sensors.Where(s => s.SensorType == SensorType.Temperature && s.Value is > 0 and < 150).ToList();
        return (temps.FirstOrDefault(s => s.Name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase))
             ?? temps.FirstOrDefault())?.Value;
    }

    private static (float pct, double usedGB, double totalGB) ReadRam()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref m))
            return (0, 0, 0);
        double total = m.ullTotalPhys / 1073741824.0;
        double used = (m.ullTotalPhys - m.ullAvailPhys) / 1073741824.0;
        return ((float)(used / total * 100), used, total);
    }

    private static bool IsPawnIoInstalled()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO");
        return key != null;
    }

    private static T? TryCreate<T>(Func<T> create) where T : class
    {
        try { return create(); }
        catch { return null; }
    }

    /// <summary>CPU % from GetSystemTimes — fallback when the PDH counter isn't available.</summary>
    private sealed class CpuTimes
    {
        private ulong _idle, _kernel, _user;

        public float Percent()
        {
            if (!GetSystemTimes(out var idle, out var kernel, out var user))
                return 0;
            ulong dIdle = idle - _idle, dKernel = kernel - _kernel, dUser = user - _user;
            bool first = _kernel == 0;
            (_idle, _kernel, _user) = (idle, kernel, user);
            ulong total = dKernel + dUser; // kernel time includes idle time
            if (first || total == 0) return 0;
            return (float)Math.Clamp((total - dIdle) * 100.0 / total, 0, 100);
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
    }

    public void Dispose()
    {
        _pdh?.Dispose();
        try { _lhmGpus?.Computer.Close(); } catch { }
        try { _lhmCpuComputer?.Close(); } catch { }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);
}
