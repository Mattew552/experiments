namespace FanGuardian;

internal sealed record ProbeResponse(string ControlId, IReadOnlyList<DetectedFan> IncreasedFans)
{
    public string Description => IncreasedFans.Count switch
    {
        0 => "рост RPM не обнаружен; наличие вентилятора не определено",
        1 => $"вероятная пара: {IncreasedFans[0].Name} [{IncreasedFans[0].Id}]",
        _ => "несколько RPM изменились; соответствие неоднозначно: " +
             string.Join(", ", IncreasedFans.Select(f => f.Name + " [" + f.Id + "]"))
    };
}

internal static class FanProbe
{
    public static bool MayStart(IEnumerable<SensorInfo> sensors, string controlId, int percent) =>
        percent == 100 && controlId.StartsWith("/lpc/", StringComparison.OrdinalIgnoreCase) &&
        CpuTemperatureReader.Read(sensors) is >= 20 and < 65 &&
        sensors.Any(s => s.Type == "Control" && s.Id == controlId && s.Value is >= 0 and < 95);

    public static IReadOnlyList<SensorInfo> CandidateControls(IEnumerable<SensorInfo> sensors) => sensors
        .Where(s => s.Type == "Control" && s.Id.StartsWith("/lpc/", StringComparison.OrdinalIgnoreCase)
                    && s.Value is >= 0 and < 95)
        .OrderBy(s => s.Id, StringComparer.Ordinal)
        .ToArray();

    public static ProbeResponse Compare(string controlId, IEnumerable<SensorInfo> before, IEnumerable<SensorInfo> after)
    {
        var baseline = before.Where(IsTachometer).ToDictionary(s => s.Id, s => s.Value!.Value, StringComparer.Ordinal);
        var increased = after.Where(IsTachometer)
            .Where(s => baseline.TryGetValue(s.Id, out var rpm) && s.Value!.Value >= 250 &&
                        s.Value.Value - rpm >= Math.Max(150, rpm * 0.15f))
            .Select(s => new DetectedFan(s.Id, s.Hardware, s.Name, s.Value!.Value))
            .OrderBy(s => s.Id, StringComparer.Ordinal)
            .ToArray();
        return new ProbeResponse(controlId, increased);
    }

    private static bool IsTachometer(SensorInfo s) => s.Type == "Fan" &&
        !s.Id.StartsWith("/gpu/", StringComparison.OrdinalIgnoreCase) && s.Value is >= 0 and < 10000;
}
