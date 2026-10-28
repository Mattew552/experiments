using LibreHardwareMonitor.Hardware;

namespace FanGuardian;

internal sealed record SensorInfo(string Id, string Name, string Hardware, string Type, float? Value);
internal interface IFanHardware : IDisposable
{
    IReadOnlyList<SensorInfo> Read();
    void Set(string controlId, int percent);
    void Default(string controlId);
}
internal sealed class LhmHardware : IFanHardware
{
    private readonly Computer computer = new() { IsCpuEnabled = true, IsMotherboardEnabled = true, IsGpuEnabled = false,
        IsControllerEnabled = false, IsStorageEnabled = false, IsMemoryEnabled = false, IsNetworkEnabled = false };
    private readonly Dictionary<string, IControl> controls = new(StringComparer.Ordinal);
    public LhmHardware() { computer.Open(); }
    private IEnumerable<IHardware> AllHardware()
    {
        foreach (var h in computer.Hardware)
        {
            yield return h;
            foreach (var sub in h.SubHardware) yield return sub;
        }
    }
    public IReadOnlyList<SensorInfo> Read()
    {
        controls.Clear();
        var output = new List<SensorInfo>();
        foreach (var h in AllHardware())
        {
            h.Update();
            foreach (var s in h.Sensors)
            {
                if (s.SensorType is not (SensorType.Temperature or SensorType.Fan or SensorType.Control)) continue;
                var id = s.Identifier.ToString();
                output.Add(new(id, s.Name, h.Name, s.SensorType.ToString(), s.Value));
                // GPU is absent at Computer construction and additionally excluded here.
                if (h.HardwareType == HardwareType.SuperIO && s.SensorType == SensorType.Control && s.Control != null)
                    controls[id] = s.Control;
            }
        }
        return output;
    }
    public void Set(string controlId, int percent)
    {
        if (!controls.TryGetValue(controlId, out var c)) throw new InvalidOperationException("Канал управления недоступен");
        if (percent < c.MinSoftwareValue || percent > c.MaxSoftwareValue) throw new ArgumentOutOfRangeException(nameof(percent));
        c.SetSoftware(percent);
    }
    public void Default(string controlId)
    {
        if (!controls.TryGetValue(controlId, out var c)) throw new InvalidOperationException("Канал управления недоступен");
        c.SetDefault();
    }
    public void Dispose() => computer.Close();
}

internal sealed class SafeHardware : IFanHardware
{
    private readonly IFanHardware inner;
    private readonly Dictionary<string, FanBinding> allowed;
    public SafeHardware(IFanHardware inner, IEnumerable<FanBinding> bindings)
    {
        this.inner = inner;
        allowed = bindings.Where(b => Enum.IsDefined(b.Role) && b.MappingVerified && b.ReturnVerified && b.SpeedVerified && b.ControlId.StartsWith("/lpc/", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(b => b.ControlId, StringComparer.Ordinal);
    }
    public IReadOnlyList<SensorInfo> Read() => inner.Read();
    private FanBinding Require(string id)
    {
        if (!allowed.TryGetValue(id, out var binding)) throw new InvalidOperationException("Запись запрещена: канал не подтверждён");
        return binding;
    }
    public void Set(string controlId, int percent)
    {
        var b = Require(controlId);
        if (percent < (b.Role == FanRole.Cpu ? Math.Max(30, b.MinimumPercent) : 0) || percent > 100)
            throw new ArgumentOutOfRangeException(nameof(percent));
        if (percent == 0 && (!FanRoles.IsFront(b.Role) || !b.StopVerified))
            throw new InvalidOperationException("Остановка канала не подтверждена");
        inner.Set(controlId, percent);
    }
    public void Default(string controlId) { Require(controlId); inner.Default(controlId); }
    public void Dispose() => inner.Dispose();
}
