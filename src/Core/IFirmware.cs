namespace Forja.Core
{
    // All methods catch internally and return null/false; they never throw.
    public interface IFirmware
    {
        SensorReading ReadSensors();
        PerfMode? GetMode();
        bool SetMode(PerfMode m);
        bool SetFanAuto();
        bool SetFanPercent(int cpuPct, int gpuPct);
        bool Available { get; }
    }
}
