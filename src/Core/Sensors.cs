namespace Forja.Core
{
    public enum PerfMode
    {
        Silencioso,
        DiaADia,
        Desempenho,
        // Firmware profiles 6 and 5. Offered only when the environment test confirmed them
        // (see CapabilityProfile); the first three keep their old meaning and order.
        Economico,
        Turbo
    }

    public class SensorReading
    {
        public double? CpuTempC;
        public double? GpuTempC;
        public int? CpuFanRpm;
        public int? GpuFanRpm;
    }
}
