namespace FanGuardian;

internal static class ProbeAlgorithmTests
{
    public static void Run()
    {
        RunAsync().GetAwaiter().GetResult();
    }

    private static async Task RunAsync()
    {
        Check(!ProbeCoordinator.ReturnedRpm(100, 120, 120) && ProbeCoordinator.ReturnedRpm(100, 120, 100) &&
              !ProbeCoordinator.ReturnedRpm(800, 880, 880) && ProbeCoordinator.ReturnedRpm(800, 880, 805),
            "Слабая реакция ошибочно считается восстановленной, пока RPM остаются высокими");
        using (var hardware = new FakeProbeHardware())
        {
            var result = await Coordinator(hardware).RunAsync(null, CancellationToken.None);
            Check(result.Error is null && result.Results.Single().AssociationVerified &&
                  result.Results[0].RestoreVerified && result.Results[0].RpmId == FakeProbeHardware.RpmId &&
                  result.Results[0].BaselinePercent == 55 && result.Results[0].BaselineRpm == 800 &&
                  hardware.StartCount == 2 && hardware.EndCount == 2 && !hardware.High,
                "Повторяемая связь и возврат не подтверждены");
        }
        using (var hardware = new FakeProbeHardware { ReactOtherDeviceOnly = true })
        {
            var result = await Coordinator(hardware).RunAsync(null, CancellationToken.None);
            Check(result.Error is null && result.Results.All(r => !r.AssociationVerified),
                "RPM другого контроллера ошибочно приписан выходу");
        }
        using (var hardware = new FakeProbeHardware { DuplicateControls = true })
        {
            var result = await Coordinator(hardware).RunAsync(null, CancellationToken.None);
            Check(result.Error is null && result.Results.Count == 2 && result.Results.All(r => !r.AssociationVerified),
                "Общий тахометр двух выходов ошибочно разрешён как независимый");
        }
        using (var cancellation = new CancellationTokenSource())
        using (var hardware = new FakeProbeHardware())
        {
            Task Delay(TimeSpan _, CancellationToken token)
            {
                hardware.Advance();
                if (hardware.High) cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }
            var coordinator = new ProbeCoordinator(hardware, hardware.Start, hardware.End, Delay);
            var result = await coordinator.RunAsync(null, cancellation.Token);
            Check(result.Cancelled && result.Error is null && hardware.StartCount == 1 && hardware.EndCount == 1 && !hardware.High,
                "Отмена пробы пропустила восстановление");
        }
        using (var hardware = new FakeProbeHardware { FailToReturn = true, DuplicateControls = true })
        {
            var result = await Coordinator(hardware).RunAsync(null, CancellationToken.None);
            Check(result.Error is not null && hardware.StartCount == 1 && hardware.EndCount == 1,
                "После невосстановленного RPM началась следующая проба");
        }
        using (var hardware = new FakeProbeHardware { NoActiveTachometers = true })
        {
            var result = await Coordinator(hardware).RunAsync(null, CancellationToken.None);
            Check(result.Error is not null && hardware.StartCount == 0,
                "Команда отправлена без активного тахометра");
        }
        using (var hardware = new FakeProbeHardware { Duty = 100 })
        {
            var result = await Coordinator(hardware).RunAsync(null, CancellationToken.None);
            Check(result.Results.Count == 1 && !result.Results[0].AssociationVerified && hardware.StartCount == 0,
                "Исходные 100% ошибочно испытаны снижением");
        }
        using (var hardware = new FakeProbeHardware { Noisy = true })
        {
            var result = await Coordinator(hardware).RunAsync(null, CancellationToken.None);
            Check(result.Error is null && hardware.StartCount == 0,
                "Команда отправлена при нестабильном baseline");
        }
        using (var hardware = new FakeProbeHardware { CpuTemperature = 0 })
        {
            var result = await Coordinator(hardware).RunAsync(null, CancellationToken.None);
            Check(result.Error is not null && hardware.StartCount == 0,
                "Команда отправлена при недостоверной температуре");
        }
        var forbidden = new ControlChannel("/gpu/control/0", "/gpu", "GPU", HardwareOrigin.Gpu, RestoreStrategy.RestoreOriginal, true);
        using (var hardware = new FakeProbeHardware())
            Check(!FanProbe.MayStart(hardware.Read(), forbidden, 100), "Проба GPU разрешена");
    }

    private static ProbeCoordinator Coordinator(FakeProbeHardware hardware) => new(hardware, hardware.Start, hardware.End,
        (_, token) => { token.ThrowIfCancellationRequested(); hardware.Advance(); return Task.CompletedTask; });

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class FakeProbeHardware : IFanHardware
    {
        public const string DeviceId = "/lpc/test/0";
        public const string RpmId = DeviceId + "/fan/0";
        public int StartCount { get; private set; }
        public int EndCount { get; private set; }
        public bool High { get; private set; }
        public bool ReactOtherDeviceOnly { get; init; }
        public bool DuplicateControls { get; init; }
        public bool FailToReturn { get; init; }
        public bool NoActiveTachometers { get; init; }
        public bool Noisy { get; init; }
        public int Duty { get; init; } = 55;
        public float CpuTemperature { get; init; } = 44;
        private float rpm = 800;
        private int reads;

        public IReadOnlyList<ControlChannel> Channels => Enumerable.Range(0, DuplicateControls ? 2 : 1)
            .Select(i => new ControlChannel(DeviceId + "/control/" + i, DeviceId, "Fan " + i,
                HardwareOrigin.Motherboard, RestoreStrategy.RestoreOriginal, true)).ToArray();

        public IReadOnlyList<SensorInfo> Read()
        {
            reads++;
            var currentRpm = NoActiveTachometers ? 0 : rpm;
            if (Noisy) currentRpm += reads % 2 == 0 ? 80 : -80;
            var sensors = new List<SensorInfo>
            {
                new("/intelcpu/0/temperature/0", "CPU Package", "CPU", "Temperature", CpuTemperature),
                new(RpmId, "Fan 0", "Super I/O", "Fan", ReactOtherDeviceOnly ? 800 : currentRpm, DeviceId, HardwareOrigin.Motherboard),
                new("/lpc/other/0/fan/0", "Other fan", "Other device", "Fan", currentRpm, "/lpc/other/0", HardwareOrigin.Motherboard)
            };
            sensors.AddRange(Channels.Select(channel => new SensorInfo(channel.Id, channel.Name, "Super I/O", "Control",
                High ? 100 : Duty, DeviceId, HardwareOrigin.Motherboard)));
            return sensors;
        }

        public void Advance()
        {
            var target = High || (FailToReturn && StartCount > 0) ? 1200 : 800;
            rpm += Math.Clamp(target - rpm, -100, 100);
        }

        public void Start(string _) { StartCount++; High = true; }
        public void End(string _) { EndCount++; High = false; }
        public void Set(string _, int percent) => throw new Exception("Обычная команда в пробе запрещена");
        public void Default(string _) => throw new Exception("Не использован отдельный возврат пробы");
        public void Dispose() { }
    }
}
