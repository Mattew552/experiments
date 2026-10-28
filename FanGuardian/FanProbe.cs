namespace FanGuardian;

internal static class FanProbe
{
    public static bool MayStart(IEnumerable<SensorInfo> sensors, ControlChannel channel, int percent)
    {
        var snapshot = sensors.ToArray();
        return percent == 100 && channel.AllowsFanWrites && channel.MaximumPercent >= 100 &&
               CpuTemperatureReader.Read(snapshot) is >= 20 and < 65 &&
               snapshot.Any(s => s.Type == "Control" && s.Id == channel.Id &&
                                 s.Origin == channel.Origin && DeviceId(s) == channel.DeviceId && s.Value is >= 0 and < 95);
    }

    internal static bool IsTachometer(SensorInfo s) => s.Type == "Fan" && s.Origin != HardwareOrigin.Gpu &&
        !s.Id.StartsWith("/gpu", StringComparison.OrdinalIgnoreCase) && s.Value.HasValue &&
        float.IsFinite(s.Value.Value) && s.Value.Value >= 0;

    internal static string DeviceId(SensorInfo sensor) =>
        string.IsNullOrEmpty(sensor.DeviceId) ? ParentDevice(sensor.Id) : sensor.DeviceId;

    private static string ParentDevice(string sensorId)
    {
        var separator = sensorId.LastIndexOf('/');
        if (separator <= 0) return sensorId;
        separator = sensorId.LastIndexOf('/', separator - 1);
        return separator <= 0 ? sensorId : sensorId[..separator];
    }
}
