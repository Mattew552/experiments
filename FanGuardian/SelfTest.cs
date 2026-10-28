namespace FanGuardian;

internal static class SelfTest
{
    private sealed class Fake : IFanHardware
    {
        public readonly List<string> Writes = new();
        public IReadOnlyList<ControlChannel> Channels { get; } = Enumerable.Range(0, 7)
            .Select(i => new ControlChannel("/lpc/chip/control/" + i, "/lpc/chip", "Fan " + i,
                HardwareOrigin.Motherboard, RestoreStrategy.RestoreOriginal, true)).ToArray();
        public IReadOnlyList<SensorInfo> Sensors = Array.Empty<SensorInfo>();
        public IReadOnlyList<SensorInfo> Read() => Sensors;
        public void Set(string id, int value) => Writes.Add(id + ":" + value);
        public void Default(string id) => Writes.Add(id + ":default");
        public void Dispose() { }
    }
    public static void Run()
    {
        ModelMigrationTests.Run();
        ProbeAlgorithmTests.Run();
        SetupTests.Run();
        HardwareCapabilityTests.Run();
        WorkerHostTests.Run();
        const string cpuFan = "/lpc/nct6797d/0/fan/1";
        const string frontPair = "/lpc/nct6797d/0/fan/4";
        const string frontTop = "/lpc/nct6797d/0/fan/5";
        var fake = new Fake();
        var cpu = new FanBinding { ControlId = "/lpc/chip/control/0", Role = FanRole.Cpu, MappingVerified = true, ReturnVerified = true, SpeedVerified = true, MinimumPercent = 40 };
        var caseFan = new FanBinding { ControlId = "/lpc/chip/control/1", Role = FanRole.FrontTop, MappingVerified = true, ReturnVerified = true, SpeedVerified = true, StopVerified = true };
        using (var safe = new SafeHardware(fake, new[] { cpu, caseFan, new FanBinding { ControlId = "/gpu/control/0", MappingVerified = true, ReturnVerified = true, SpeedVerified = true },
            new FanBinding { ControlId = "/lpc/chip/control/2", Role = (FanRole)99, MappingVerified = true, ReturnVerified = true, SpeedVerified = true },
            new FanBinding { ControlId = "/lpc/chip/control/3", Role = FanRole.FrontPair, ReturnVerified = true, SpeedVerified = true } }))
        {
            ExpectDenied(() => safe.Set("/gpu/control/0", 90));
            ExpectDenied(() => safe.Default("/gpu/control/0"));
            ExpectDenied(() => safe.Set(cpu.ControlId, 0));
            ExpectDenied(() => safe.Set("/lpc/chip/control/9", 80));
            ExpectDenied(() => safe.Set("/lpc/chip/control/2", 80));
            ExpectDenied(() => safe.Set("/lpc/chip/control/3", 80));
            safe.Set(caseFan.ControlId, 0);
            safe.Default(cpu.ControlId);
        }
        if (fake.Writes.Any(x => x.Contains("gpu", StringComparison.OrdinalIgnoreCase)) || fake.Writes.Count != 2)
            throw new Exception("Запись на запрещённый канал");
        var invalidTemperatureHardware = new Fake { Sensors =
        [new SensorInfo("/amdcpu/0/temperature/2", "Core (Tctl/Tdie)", "AMD Ryzen 5 5600X", "Temperature", 0)] };
        using (var engine = new Engine(invalidTemperatureHardware, new Settings { Bindings = [cpu] }))
        {
            engine.Tick();
            ExpectDenied(() => engine.SelectMode(FanMode.Manual));
        }
        if (invalidTemperatureHardware.Writes.Count != 0) throw new Exception("При 0 °C отправлена аппаратная команда");
        var temperatures = new[]
        {
            new SensorInfo("/lpc/chip/temperature/0", "CPU", "Motherboard", "Temperature", 90),
            new SensorInfo("/intelcpu/0/temperature/0", "CPU Package", "Intel Core", "Temperature", 54),
            new SensorInfo("/intelcpu/0/temperature/1", "Core #1 Distance to TjMax", "Intel Core", "Temperature", 40)
        };
        if (CpuTemperatureReader.Read(temperatures) != 54 ||
            CpuTemperatureReader.Read([temperatures[0]]) is not null ||
            CpuTemperatureReader.Read([temperatures[1] with { Value = 0 }]) is not null)
            throw new Exception("Температура CPU должна выбираться из достоверного датчика самого процессора");
        if (CpuTemperatureReader.Read([
                new SensorInfo("/amdcpu/0/temperature/2", "Core (Tctl/Tdie)", "AMD Ryzen", "Temperature", 46),
                new SensorInfo("/amdcpu/0/temperature/3", "CCD1 (Tdie)", "AMD Ryzen", "Temperature", 44)
            ]) != 46)
            throw new Exception("AMD Tctl/Tdie должен сохраняться как источник температуры");
        Curves.Validate(Curves.Default(FanRole.Cpu), 40);
        var boundSettings = new Settings { MachineFingerprint = "machine-a" };
        if (!boundSettings.BelongsTo("machine-a") || boundSettings.BelongsTo("machine-b") || boundSettings.BelongsTo(""))
            throw new Exception("Привязки нельзя переносить между ПК");
        if (!FanRoles.IsFront(FanRole.FrontTop) || !FanRoles.IsFront(FanRole.FrontPair) || FanRoles.IsFront(FanRole.Cpu))
            throw new Exception("Неверная физическая схема каналов");
        if (!FanRoles.Label(FanRole.FrontPair).Contains("SYS_FAN3") || !FanRoles.Label(FanRole.FrontTop).Contains("SYS_FAN4"))
            throw new Exception("Неверное соответствие передних групп разъёмам");
        var telemetry = new[] { new SensorInfo(frontPair, "Fan #5", "Nuvoton NCT6797D", "Fan", 1030) };
        if (Telemetry.Rpm(telemetry, frontPair) != 1030 || Telemetry.Rpm(telemetry, frontTop) != null)
            throw new Exception("Автоматическое чтение RPM не должно подменять отсутствующий датчик");
        var pairBinding = new FanBinding { Role = FanRole.FrontPair, RpmId = frontPair, MappingVerified = true, ReturnVerified = true, SpeedVerified = true };
        if (Telemetry.RpmForRole(telemetry, [pairBinding], FanRole.FrontPair) != 1030 ||
            Telemetry.RpmForRole(telemetry, [], FanRole.FrontPair) != null)
            throw new Exception("Нельзя подписывать RPM без проверенной привязки на этом ПК");
        var discovered = FanDiscovery.ActiveTachometers([
            new SensorInfo("/lpc/chip/0/fan/0", "Fan #1", "Super I/O", "Fan", 980),
            new SensorInfo("/lpc/chip/0/fan/1", "Fan #2", "Super I/O", "Fan", 0),
            new SensorInfo("/gpu/0/fan/0", "GPU", "Graphics", "Fan", 1200)
        ]);
        if (discovered.Count != 1 || discovered[0].Rpm != 980)
            throw new Exception("Автообнаружение должно показывать только активные тахометры платы без записи GPU");
        var embeddedFan = FanDiscovery.ActiveTachometers([
            new SensorInfo("/ec/0/fan/1", "Chassis Fan", "Embedded Controller", "Fan", 840)
        ]);
        if (embeddedFan.Count != 1 || !Telemetry.Caption(embeddedFan[0], []).StartsWith("Датчик:", StringComparison.Ordinal))
            throw new Exception("Новый тахометр должен отображаться без выдуманной физической роли");
        var processes = Diagnostics.ClassifyProcesses(["MSI.CentralServer", "MSI_Central_Service", "FanControl"]);
        if (processes.MsiBackground.Length != 2 || processes.OtherFanPrograms.Length != 1 || processes.OtherFanPrograms[0] != "FanControl")
            throw new Exception("Фоновые службы MSI ошибочно признаны активной программой управления");
        var elevation = Program.ElevatedStartInfo();
        if (elevation.Verb != "runas" || !elevation.UseShellExecute)
            throw new Exception("Обычный запуск должен запрашивать доступ к датчикам через UAC");
        if (Curves.Evaluate(new[] { new CurvePoint(30, 40), new CurvePoint(70, 80) }, 50) != 60) throw new Exception("Интерполяция");
        if (Curves.Evaluate(Curves.Default(FanRole.FrontTop, 0), 40) >= Curves.Evaluate(Curves.Default(FanRole.FrontTop, 1), 40) ||
            Curves.Evaluate(Curves.Default(FanRole.FrontTop, 1), 40) >= Curves.Evaluate(Curves.Default(FanRole.FrontTop, 2), 40))
            throw new Exception("Три профиля должны задавать разные скорости");
        var live = new Fake { Sensors =
        [
            new SensorInfo("/amdcpu/0/temperature/2", "Core (Tctl/Tdie)", "AMD Ryzen 5 5600X", "Temperature", 45),
            new SensorInfo(cpuFan, "Fan #2", "Nuvoton NCT6797D", "Fan", 1000),
            new SensorInfo(frontPair, "Fan #5", "Nuvoton NCT6797D", "Fan", 1000),
            new SensorInfo(frontTop, "Fan #6", "Nuvoton NCT6797D", "Fan", 1000)
        ] };
        var verified = new Settings { Bindings =
        [
            new FanBinding { ControlId = "/lpc/chip/control/1", RpmId = cpuFan, Role = FanRole.Cpu, MappingVerified = true, ReturnVerified = true, SpeedVerified = true, MinimumPercent = 40 },
            new FanBinding { ControlId = "/lpc/chip/control/4", RpmId = frontPair, Role = FanRole.FrontPair, MappingVerified = true, ReturnVerified = true, SpeedVerified = true, StopVerified = true, MinimumPercent = 40 },
            new FanBinding { ControlId = "/lpc/chip/control/5", RpmId = frontTop, Role = FanRole.FrontTop, MappingVerified = true, ReturnVerified = true, SpeedVerified = true, StopVerified = true, MinimumPercent = 40 }
        ] };
        using (var engine = new Engine(live, verified))
        {
            engine.Tick();
            engine.SelectMode(FanMode.CaseOff);
            if (!live.Writes.Contains("/lpc/chip/control/4:0") || !live.Writes.Contains("/lpc/chip/control/5:0") || live.Writes.Any(x => x.Contains("/control/1:0")))
                throw new Exception("Остановка корпуса затронула неверные каналы");
            engine.SelectMode(FanMode.Manual);
            if (engine.Mode != FanMode.Bios) throw new Exception("После остановки ручной режим должен ждать раскрутки");
            engine.Tick();
            if (engine.Mode != FanMode.Manual) throw new Exception("После восстановления RPM ручной режим не включился");
            live.Sensors = live.Sensors.Select(s => s.Type == "Temperature" ? s with { Value = 70 } : s).ToArray();
            engine.SelectMode(FanMode.CaseOff);
            engine.Tick();
            if (engine.Mode != FanMode.Bios) throw new Exception("При 70 °C режим остановки должен вернуть BIOS");
        }
        Console.WriteLine("Self-test OK");
    }
    private static void ExpectDenied(Action action)
    {
        try { action(); } catch (InvalidOperationException) { return; } catch (ArgumentOutOfRangeException) { return; }
        throw new Exception("Запрещённая запись прошла");
    }
}
