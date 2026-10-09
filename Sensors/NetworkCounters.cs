using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PCStatus.Sensors;

/// <summary>
/// Total download/upload rate across physical network adapters, from GetIfTable2.
/// Only "hardware interfaces" are counted, so virtual adapters (VPN, Hyper-V vEthernet, loopback)
/// don't double-count traffic that also crosses the physical NIC.
/// </summary>
public sealed class NetworkCounters
{
    private readonly Dictionary<ulong, (ulong inOctets, ulong outOctets)> _last = new();
    private readonly Stopwatch _clock = new();

    public (double downBps, double upBps, string adapters) Sample()
    {
        double seconds = _clock.Elapsed.TotalSeconds;
        _clock.Restart();

        ulong down = 0, up = 0;
        var names = new List<string>();
        var seen = new HashSet<ulong>();

        foreach (var row in ReadInterfaces())
        {
            seen.Add(row.Luid);
            names.Add(row.Alias);
            // Skip the first sample of an interface and counter resets (adapter re-enabled).
            if (_last.TryGetValue(row.Luid, out var prev) && row.InOctets >= prev.inOctets && row.OutOctets >= prev.outOctets)
            {
                down += row.InOctets - prev.inOctets;
                up += row.OutOctets - prev.outOctets;
            }
            _last[row.Luid] = (row.InOctets, row.OutOctets);
        }
        foreach (var gone in _last.Keys.Where(k => !seen.Contains(k)).ToList())
            _last.Remove(gone);

        if (seconds <= 0.05) return (0, 0, string.Join(" · ", names));
        return (down / seconds, up / seconds, string.Join(" · ", names));
    }

    private readonly record struct IfRow(ulong Luid, string Alias, ulong InOctets, ulong OutOctets);

    private static unsafe List<IfRow> ReadInterfaces()
    {
        var rows = new List<IfRow>();
        if (GetIfTable2(out IntPtr table) != 0)
            return rows;
        try
        {
            uint count = *(uint*)table;
            var first = (MIB_IF_ROW2*)((byte*)table + 8); // Table[] is 8-byte aligned after NumEntries
            for (int i = 0; i < count; i++)
            {
                var r = &first[i];
                bool hardware = (r->InterfaceAndOperStatusFlags & 0x01) != 0;
                bool up = r->OperStatus == 1; // IfOperStatusUp
                if (!hardware || !up || r->Type == 24 /* loopback */)
                    continue;
                rows.Add(new IfRow(r->InterfaceLuid, new string(r->Alias), r->InOctets, r->OutOctets));
            }
        }
        finally
        {
            FreeMibTable(table);
        }
        return rows;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct MIB_IF_ROW2
    {
        public ulong InterfaceLuid;
        public uint InterfaceIndex;
        public Guid InterfaceGuid;
        public fixed char Alias[257];
        public fixed char Description[257];
        public uint PhysicalAddressLength;
        public fixed byte PhysicalAddress[32];
        public fixed byte PermanentPhysicalAddress[32];
        public uint Mtu;
        public uint Type;
        public uint TunnelType;
        public uint MediaType;
        public uint PhysicalMediumType;
        public uint AccessType;
        public uint DirectionType;
        public byte InterfaceAndOperStatusFlags; // bit 0 = HardwareInterface
        public uint OperStatus;
        public uint AdminStatus;
        public uint MediaConnectState;
        public Guid NetworkGuid;
        public uint ConnectionType;
        public ulong TransmitLinkSpeed;
        public ulong ReceiveLinkSpeed;
        public ulong InOctets;
        public ulong InUcastPkts;
        public ulong InNUcastPkts;
        public ulong InDiscards;
        public ulong InErrors;
        public ulong InUnknownProtos;
        public ulong InUcastOctets;
        public ulong InMulticastOctets;
        public ulong InBroadcastOctets;
        public ulong OutOctets;
        public ulong OutUcastPkts;
        public ulong OutNUcastPkts;
        public ulong OutDiscards;
        public ulong OutErrors;
        public ulong OutUcastOctets;
        public ulong OutMulticastOctets;
        public ulong OutBroadcastOctets;
        public ulong OutQLen;
    }

    // Guards the hand-written layout against the native struct size (1352 bytes on 64-bit).
    internal static unsafe int RowSize => sizeof(MIB_IF_ROW2);

    [DllImport("iphlpapi.dll")]
    private static extern uint GetIfTable2(out IntPtr table);

    [DllImport("iphlpapi.dll")]
    private static extern void FreeMibTable(IntPtr memory);
}
