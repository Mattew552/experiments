namespace FanGuardian;

internal static class CpuTemperatureReader
{
    // Use a CPU-owned package/control temperature. Motherboard labels such as
    // "CPU" can refer to a different thermistor and are not safe substitutes.
    public static float? Read(IEnumerable<SensorInfo> sensors)
    {
        var candidates = sensors
            .Where(s => s.Type == "Temperature" && IsCpuSensor(s.Id) && s.Value is > 0 and < 120)
            .Select(s => (Sensor: s, Rank: Rank(s.Name)))
            .Where(x => x.Rank < int.MaxValue)
            .ToArray();
        if (candidates.Length == 0) return null;
        var bestRank = candidates.Min(x => x.Rank);
        // On a multi-CPU machine the hottest package is the safe input.
        return candidates.Where(x => x.Rank == bestRank).Max(x => x.Sensor.Value);
    }

    private static bool IsCpuSensor(string id) =>
        id.StartsWith("/amdcpu/", StringComparison.OrdinalIgnoreCase) ||
        id.StartsWith("/intelcpu/", StringComparison.OrdinalIgnoreCase);

    private static int Rank(string name)
    {
        if (name.Contains("Distance", StringComparison.OrdinalIgnoreCase)) return int.MaxValue;
        if (name.Contains("Tctl/Tdie", StringComparison.OrdinalIgnoreCase)) return 0;
        if (name.Contains("CPU Package", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Package", StringComparison.OrdinalIgnoreCase)) return 1;
        if (name.Equals("Core (Tctl)", StringComparison.OrdinalIgnoreCase)) return 2;
        return int.MaxValue;
    }
}
