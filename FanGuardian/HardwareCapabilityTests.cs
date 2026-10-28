using System.Text.Json;

namespace FanGuardian;

internal static class HardwareCapabilityTests
{
    private sealed class Fake : IFanHardware
    {
        public IReadOnlyList<ControlChannel> Channels { get; init; } = [];
        public List<string> Writes { get; } = [];
        public IReadOnlyList<SensorInfo> Read() => [];
        public void Set(string id, int percent) => Writes.Add(id + ":" + percent);
        public void Default(string id) => Writes.Add(id + ":default");
        public void Dispose() { }
    }

    public static void Run()
    {
        var valid = new ControlChannel("port-1", "board-1", "Case", HardwareOrigin.Motherboard,
            RestoreStrategy.RestoreOriginal, true);
        var gpu = valid with { Id = "/lpc/fake/control/0", Origin = HardwareOrigin.Gpu };
        var shared = valid with { Id = "shared", IsIndependent = false };
        var fallback = valid with { Id = "fallback", Restore = RestoreStrategy.SafeFallback };
        var unknown = valid with { Id = "unknown", Origin = HardwareOrigin.Unknown };
        var fake = new Fake { Channels = [valid, gpu, shared, fallback, unknown] };
        FanBinding Binding(string id) => new()
        {
            ControlId = id, Role = FanRole.Case, MinimumPercent = 40,
            MappingVerified = true, ReturnVerified = true, SpeedVerified = true
        };
        using var safe = new SafeHardware(fake, fake.Channels.Select(c => Binding(c.Id)));
        foreach (var denied in new[] { gpu.Id, shared.Id, fallback.Id, unknown.Id, "missing" })
        {
            Denied(() => safe.Set(denied, 80));
            Denied(() => safe.Default(denied));
        }
        Denied(() => safe.Set(valid.Id, 39));
        Denied(() => safe.Set(valid.Id, 0));
        safe.Set(valid.Id, 40);
        safe.Default(valid.Id);
        if (!fake.Writes.SequenceEqual(new[] { "port-1:40", "port-1:default" }))
            throw new Exception("Capabilities permitted an unauthorized hardware command");

        if (SuperIoRestoreCatalog.RestoreFor("/lpc/nct6797d/0") != RestoreStrategy.RestoreOriginal ||
            SuperIoRestoreCatalog.RestoreFor("/lpc/f71889ad/0") != RestoreStrategy.RestoreOriginal ||
            SuperIoRestoreCatalog.RestoreFor("/lpc/it8689e/0") != RestoreStrategy.None ||
            SuperIoRestoreCatalog.RestoreFor("/lpc/w83627hf/0") != RestoreStrategy.None ||
            SuperIoRestoreCatalog.RestoreFor("/lpc/nct6687dr/0") != RestoreStrategy.None)
            throw new Exception("Unaudited Super I/O restore was authorized");

        var snapshot = new HardwareSnapshot(
            [new SensorInfo("rpm-1", "Fan", "Board", "Fan", 900, "board-1", HardwareOrigin.Motherboard)], [valid]);
        var roundTrip = JsonSerializer.Deserialize<HardwareSnapshot>(JsonSerializer.Serialize(snapshot));
        if (roundTrip is null || roundTrip.Channels.Single() != valid || roundTrip.Sensors.Single() != snapshot.Sensors.Single())
            throw new Exception("Worker snapshot lost hardware capabilities");
    }

    private static void Denied(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        catch (ArgumentOutOfRangeException) { return; }
        throw new Exception("Expected capability refusal");
    }
}
