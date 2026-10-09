namespace PCStatus.Sensors;

public sealed record GpuReading(
    GpuInfo Info,
    float LoadPct,
    float? TempC,
    bool TempSkippedIdle,
    double MemUsedGB,
    double MemTotalGB);

public sealed record SensorSnapshot(
    float CpuPct,
    float? CpuTempC,
    float RamPct,
    double RamUsedGB,
    double RamTotalGB,
    IReadOnlyList<GpuReading> Gpus,
    bool PawnIoInstalled,
    double NetDownBps,
    double NetUpBps,
    string NetAdapters);
