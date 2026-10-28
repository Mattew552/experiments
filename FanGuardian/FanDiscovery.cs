namespace FanGuardian;

internal sealed record DetectedFan(string Id, string Hardware, string Name, float Rpm);

internal static class FanDiscovery
{
    // Detection is intentionally read-only. An active tachometer does not prove
    // which physical fan or motherboard header it belongs to.
    public static IReadOnlyList<DetectedFan> ActiveTachometers(IEnumerable<SensorInfo> sensors) =>
        sensors
            .Where(s => s.Type == "Fan" && s.Origin != HardwareOrigin.Gpu &&
                        !s.Id.StartsWith("/gpu", StringComparison.OrdinalIgnoreCase) &&
                        s.Value.HasValue && float.IsFinite(s.Value.Value) && s.Value.Value > 0)
            .Select(s => new DetectedFan(s.Id, s.Hardware, s.Name, s.Value!.Value))
            .OrderBy(s => s.Hardware, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Id, StringComparer.Ordinal)
            .ToArray();
}
