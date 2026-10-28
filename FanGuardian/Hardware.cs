using LibreHardwareMonitor.Hardware;

namespace FanGuardian;

internal sealed record SensorInfo(string Id, string Name, string Hardware, string Type, float? Value,
    string DeviceId = "", HardwareOrigin Origin = HardwareOrigin.Unknown);
internal interface IFanHardware : IDisposable
{
    IReadOnlyList<ControlChannel> Channels => Array.Empty<ControlChannel>();
    IReadOnlyList<SensorInfo> Read();
    void Set(string controlId, int percent);
    void Default(string controlId);
}
internal sealed class LhmHardware : IFanHardware
{
    private readonly Computer computer = new() { IsCpuEnabled = true, IsMotherboardEnabled = true, IsGpuEnabled = false,
        IsControllerEnabled = false, IsStorageEnabled = false, IsMemoryEnabled = false, IsNetworkEnabled = false };
    private readonly Dictionary<string, IControl> controls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IControl> rollbackControls = new(StringComparer.Ordinal);
    public IReadOnlyList<ControlChannel> Channels { get; private set; } = Array.Empty<ControlChannel>();
    public LhmHardware() { computer.Open(); }
    private IEnumerable<IHardware> AllHardware()
    {
        static IEnumerable<IHardware> Traverse(IHardware hardware)
        {
            yield return hardware;
            foreach (var child in hardware.SubHardware)
                foreach (var item in Traverse(child)) yield return item;
        }
        return computer.Hardware.SelectMany(Traverse);
    }
    public IReadOnlyList<SensorInfo> Read()
    {
        // Retain the last control handles if an update fails, so the worker can
        // still attempt rollback using the instance that captured the old mode.
        var nextControls = new Dictionary<string, IControl>(StringComparer.Ordinal);
        var output = new List<SensorInfo>();
        var channels = new List<ControlChannel>();
        foreach (var h in AllHardware())
        {
            // Preserve the prohibition even if another hardware group is enabled later.
            if (h.HardwareType.ToString().StartsWith("Gpu", StringComparison.OrdinalIgnoreCase)) continue;
            h.Update();
            var deviceId = h.Identifier.ToString();
            var origin = h.HardwareType is HardwareType.Motherboard or HardwareType.SuperIO
                ? HardwareOrigin.Motherboard : HardwareOrigin.Unknown;
            foreach (var s in h.Sensors)
            {
                if (s.SensorType is not (SensorType.Temperature or SensorType.Fan or SensorType.Control)) continue;
                var id = s.Identifier.ToString();
                output.Add(new(id, s.Name, h.Name, s.SensorType.ToString(), s.Value, deviceId, origin));
                // GPU is absent at Computer construction and additionally excluded here.
                if (h.HardwareType == HardwareType.SuperIO && s.SensorType == SensorType.Control && s.Control != null)
                {
                    nextControls[id] = s.Control;
                    var restore = SuperIoRestoreCatalog.RestoreFor(deviceId);
                    channels.Add(new ControlChannel(id, deviceId, s.Name, HardwareOrigin.Motherboard,
                        restore, restore != RestoreStrategy.None,
                        (int)Math.Ceiling(s.Control.MinSoftwareValue), (int)Math.Floor(s.Control.MaxSoftwareValue)));
                }
            }
        }
        controls.Clear();
        foreach (var (id, control) in nextControls) controls[id] = control;
        Channels = channels.ToArray();
        foreach (var channel in Channels.Where(c => c.AllowsFanWrites))
            rollbackControls[channel.Id] = nextControls[channel.Id];
        return output;
    }
    public void Set(string controlId, int percent)
    {
        var channel = Channels.FirstOrDefault(c => c.Id == controlId);
        if (channel?.AllowsFanWrites != true || !controls.TryGetValue(controlId, out var c))
            throw new InvalidOperationException("Канал управления недоступен");
        if (percent < c.MinSoftwareValue || percent > c.MaxSoftwareValue) throw new ArgumentOutOfRangeException(nameof(percent));
        c.SetSoftware(percent);
    }
    public void Default(string controlId)
    {
        // A control can disappear from a later sensor snapshot. Its original
        // backend object must remain available for rollback of an earlier write.
        if (!rollbackControls.TryGetValue(controlId, out var c))
            throw new InvalidOperationException("Возврат режима этого канала недоступен");
        c.SetDefault();
    }
    public void Dispose() => computer.Close();
}

internal sealed class SafeHardware : IFanHardware
{
    private readonly IFanHardware inner;
    private readonly Dictionary<string, FanBinding> allowed;
    private readonly bool ownsInner;
    public IReadOnlyList<ControlChannel> Channels => inner.Channels;
    public SafeHardware(IFanHardware inner, IEnumerable<FanBinding> bindings, bool ownsInner = true)
    {
        this.inner = inner;
        this.ownsInner = ownsInner;
        allowed = bindings.Where(b => (b.Role == FanRole.Cpu || FanRoles.IsCase(b.Role)) &&
            b.MappingVerified && b.ReturnVerified && b.SpeedVerified)
            .GroupBy(b => b.ControlId, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
    }
    public IReadOnlyList<SensorInfo> Read() => inner.Read();
    private FanBinding Require(string id)
    {
        if (!allowed.TryGetValue(id, out var binding) ||
            !inner.Channels.Any(c => c.Id == id && c.AllowsFanWrites))
            throw new InvalidOperationException("Запись запрещена: канал или его возможности не подтверждены");
        return binding;
    }
    public void Set(string controlId, int percent)
    {
        ValidateSet(controlId, percent);
        inner.Set(controlId, percent);
    }
    public void ValidateSet(string controlId, int percent)
    {
        var b = Require(controlId);
        var channel = inner.Channels.Single(c => c.Id == controlId);
        var minimum = Math.Max(channel.MinimumPercent, b.Role == FanRole.Cpu ? Math.Max(30, b.MinimumPercent) : b.MinimumPercent);
        if (percent < 0 || percent > channel.MaximumPercent || percent > 100 || (percent != 0 && percent < minimum))
            throw new ArgumentOutOfRangeException(nameof(percent));
        if (percent == 0 && (!FanRoles.IsCase(b.Role) || !b.StopVerified || channel.MinimumPercent > 0))
            throw new InvalidOperationException("Остановка канала не подтверждена");
    }
    public void Default(string controlId) { Require(controlId); inner.Default(controlId); }
    public void Dispose() { if (ownsInner) inner.Dispose(); }
}
