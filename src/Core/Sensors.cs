namespace Forja.Core
{
    public enum PerfMode
    {
        Silencioso,
        DiaADia,
        Desempenho
    }

    public class SensorReading
    {
        public double? CpuTempC;
        public double? GpuTempC;
        public int? CpuFanRpm;
        public int? GpuFanRpm;
    }
}
