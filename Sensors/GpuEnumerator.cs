using System.Runtime.InteropServices;

namespace PCStatus.Sensors;

public sealed record GpuInfo(
    string Name,
    string ShortName,
    long Luid,
    uint VendorId,
    bool IsIntegrated,
    double DedicatedGB,
    double SharedGB);

/// <summary>Lists the physical GPUs via DXGI (once, at startup).</summary>
public static class GpuEnumerator
{
    public const uint VendorNvidia = 0x10DE;
    public const uint VendorIntel = 0x8086;
    public const uint VendorAmd = 0x1002;
    private const uint VendorMicrosoft = 0x1414;
    private const uint DxgiAdapterFlagSoftware = 2;
    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);

    public static List<GpuInfo> Enumerate()
    {
        var result = new List<GpuInfo>();
        var iid = typeof(IDXGIFactory1).GUID;
        if (CreateDXGIFactory1(in iid, out var factory) < 0 || factory is null)
            return result;

        try
        {
            for (uint i = 0; ; i++)
            {
                int hr = factory.EnumAdapters1(i, out var adapter);
                if (hr == DxgiErrorNotFound || adapter is null)
                    break;
                try
                {
                    if (adapter.GetDesc1(out var d) < 0)
                        continue;
                    if ((d.Flags & DxgiAdapterFlagSoftware) != 0 || d.VendorId == VendorMicrosoft)
                        continue;

                    long luid = ((long)d.LuidHigh << 32) | d.LuidLow;
                    if (result.Any(g => g.Luid == luid))
                        continue;

                    double dedicatedGB = (ulong)d.DedicatedVideoMemory / 1073741824.0;
                    double sharedGB = (ulong)d.SharedSystemMemory / 1073741824.0;
                    // iGPUs report only a small BIOS carve-out as "dedicated".
                    bool integrated = dedicatedGB < 1.0;
                    result.Add(new GpuInfo(CleanName(d.Description), "", luid, d.VendorId, integrated, dedicatedGB, sharedGB));
                }
                finally
                {
                    Marshal.ReleaseComObject(adapter);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }

        // Integrated first, then dedicated; assign unique short names.
        result = result.OrderBy(g => g.IsIntegrated ? 0 : 1).ToList();
        var bases = result.Select(MakeShortName).ToList();
        for (int i = 0; i < result.Count; i++)
        {
            string shortName = bases[i];
            if (bases.Count(b => b == shortName) > 1)
                shortName += " " + (bases.Take(i).Count(b => b == shortName) + 1);
            result[i] = result[i] with { ShortName = shortName };
        }
        return result;
    }

    /// <summary>"Intel(R) Arc(TM) 140V GPU (16GB)" -> "Intel Arc 140V GPU (16GB)".</summary>
    public static string CleanName(string name) =>
        string.Join(' ', name.Replace("(R)", "").Replace("(TM)", "").Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static string MakeShortName(GpuInfo g)
    {
        string n = g.Name;
        if (n.Contains("Arc", StringComparison.OrdinalIgnoreCase)) return "Arc";
        if (n.Contains("RTX", StringComparison.OrdinalIgnoreCase)) return "RTX";
        if (n.Contains("GTX", StringComparison.OrdinalIgnoreCase)) return "GTX";
        if (n.Contains("Radeon", StringComparison.OrdinalIgnoreCase)) return "Radeon";
        return g.VendorId switch
        {
            VendorNvidia => "NVIDIA",
            VendorIntel => "Intel",
            VendorAmd => "AMD",
            _ => "GPU",
        };
    }

    // ---- DXGI interop ----

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(in Guid riid, [MarshalAs(UnmanagedType.Interface)] out IDXGIFactory1? factory);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }

    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        // IDXGIObject
        void SetPrivateData();
        void SetPrivateDataInterface();
        void GetPrivateData();
        void GetParent();
        // IDXGIFactory
        void EnumAdapters();
        void MakeWindowAssociation();
        void GetWindowAssociation();
        void CreateSwapChain();
        void CreateSoftwareAdapter();
        // IDXGIFactory1
        [PreserveSig]
        int EnumAdapters1(uint index, [MarshalAs(UnmanagedType.Interface)] out IDXGIAdapter1? adapter);
        void IsCurrent();
    }

    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        // IDXGIObject
        void SetPrivateData();
        void SetPrivateDataInterface();
        void GetPrivateData();
        void GetParent();
        // IDXGIAdapter
        void EnumOutputs();
        void GetDesc();
        void CheckInterfaceSupport();
        // IDXGIAdapter1
        [PreserveSig]
        int GetDesc1(out DXGI_ADAPTER_DESC1 desc);
    }
}
