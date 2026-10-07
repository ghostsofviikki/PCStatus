using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace PCStatus.Sensors;

/// <summary>
/// Reads CPU and per-GPU counters through the Windows PDH API — the same source Task Manager uses.
/// Wildcard counters pick up new process instances automatically on every collect.
/// </summary>
public sealed partial class PdhCounters : IDisposable
{
    private readonly IntPtr _query;
    private readonly IntPtr _cpuUtility;
    private readonly IntPtr _gpuEngine;
    private readonly IntPtr _gpuDedicated;
    private readonly IntPtr _gpuShared;

    public PdhCounters()
    {
        Check(PdhOpenQueryW(null, IntPtr.Zero, out _query));
        _cpuUtility = AddCounter(@"\Processor Information(_Total)\% Processor Utility");
        _gpuEngine = AddCounter(@"\GPU Engine(*)\Utilization Percentage");
        _gpuDedicated = AddCounter(@"\GPU Adapter Memory(*)\Dedicated Usage");
        _gpuShared = AddCounter(@"\GPU Adapter Memory(*)\Shared Usage");
        PdhCollectQueryData(_query); // rate counters need a first sample
    }

    public void Collect() => PdhCollectQueryData(_query);

    public bool HasCpuCounter => _cpuUtility != IntPtr.Zero;

    public float CpuPercent()
    {
        if (_cpuUtility == IntPtr.Zero) return 0;
        if (PdhGetFormattedCounterValue(_cpuUtility, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, out _, out var v) != 0
            || v.CStatus > PDH_CSTATUS_NEW_DATA)
            return 0;
        return (float)Math.Clamp(v.DoubleValue, 0, 100);
    }

    /// <summary>Per adapter LUID: busiest engine's utilization (sum over processes), like Task Manager.</summary>
    public Dictionary<long, float> GpuLoadByLuid()
    {
        var perEngine = new Dictionary<(long luid, string eng), double>();
        foreach (var (name, value) in ReadArray(_gpuEngine))
        {
            var m = EngineRegex().Match(name);
            if (!m.Success) continue;
            long luid = ParseLuid(m.Groups[1].Value, m.Groups[2].Value);
            var key = (luid, m.Groups[3].Value);
            perEngine[key] = perEngine.GetValueOrDefault(key) + value;
        }

        var result = new Dictionary<long, float>();
        foreach (var ((luid, _), v) in perEngine)
            result[luid] = Math.Max(result.GetValueOrDefault(luid), (float)Math.Clamp(v, 0, 100));
        return result;
    }

    public Dictionary<long, double> GpuDedicatedBytes() => ReadMemory(_gpuDedicated);
    public Dictionary<long, double> GpuSharedBytes() => ReadMemory(_gpuShared);

    private Dictionary<long, double> ReadMemory(IntPtr counter)
    {
        var result = new Dictionary<long, double>();
        foreach (var (name, value) in ReadArray(counter))
        {
            var m = LuidRegex().Match(name);
            if (!m.Success) continue;
            long luid = ParseLuid(m.Groups[1].Value, m.Groups[2].Value);
            result[luid] = result.GetValueOrDefault(luid) + value;
        }
        return result;
    }

    private IntPtr AddCounter(string path) =>
        PdhAddEnglishCounterW(_query, path, IntPtr.Zero, out var c) == 0 ? c : IntPtr.Zero;

    private static unsafe List<(string name, double value)> ReadArray(IntPtr counter)
    {
        var list = new List<(string, double)>();
        if (counter == IntPtr.Zero) return list;

        uint size = 0;
        uint status = PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out _, IntPtr.Zero);
        if (status != PDH_MORE_DATA || size == 0) return list;

        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            status = PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out uint count, buf);
            if (status != 0) return list;
            var items = (PDH_FMT_COUNTERVALUE_ITEM*)buf;
            for (int i = 0; i < count; i++)
            {
                if (items[i].Value.CStatus > PDH_CSTATUS_NEW_DATA) continue;
                string? name = Marshal.PtrToStringUni(items[i].Name);
                if (name != null) list.Add((name, items[i].Value.DoubleValue));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
        return list;
    }

    private static long ParseLuid(string high, string low) =>
        ((long)int.Parse(high, NumberStyles.HexNumber) << 32) | uint.Parse(low, NumberStyles.HexNumber);

    private static void Check(uint status)
    {
        if (status != 0) throw new InvalidOperationException($"PDH error 0x{status:X8}");
    }

    public void Dispose() => PdhCloseQuery(_query);

    [GeneratedRegex(@"luid_0x([0-9A-Fa-f]{8})_0x([0-9A-Fa-f]{8})_phys_\d+_eng_(\d+)")]
    private static partial Regex EngineRegex();

    [GeneratedRegex(@"luid_0x([0-9A-Fa-f]{8})_0x([0-9A-Fa-f]{8})")]
    private static partial Regex LuidRegex();

    // ---- PDH interop ----

    private const uint PDH_FMT_DOUBLE = 0x00000200;
    private const uint PDH_FMT_NOCAP100 = 0x00008000;
    private const uint PDH_MORE_DATA = 0x800007D2;
    private const uint PDH_CSTATUS_NEW_DATA = 1;

    [StructLayout(LayoutKind.Explicit)]
    private struct PDH_FMT_COUNTERVALUE
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double DoubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE_ITEM
    {
        public IntPtr Name;
        public PDH_FMT_COUNTERVALUE Value;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PDH_FMT_COUNTERVALUE value);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr buffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);
}
