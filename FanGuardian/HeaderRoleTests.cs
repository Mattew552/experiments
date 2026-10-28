namespace FanGuardian;

internal static class HeaderRoleTests
{
    public static void Run()
    {
        var channel = new ControlChannel("/lpc/nct6797d/0/control/1", "/lpc/nct6797d/0", "Fan #2", HardwareOrigin.Motherboard, RestoreStrategy.RestoreOriginal, true);
        HeaderHint Known(ControlChannel c) => HeaderRoles.Resolve(c, "Micro-Star International Co., Ltd.", "B450 TOMAHAWK MAX (MS-7C02)");
        Check(Known(channel).Role == FanRole.Cpu && Known(channel).Name == "CPU_FAN1", "Профиль MSI не определил CPU header");
        Check(Known(channel with { Id = "/lpc/nct6797d/0/control/4" }).Name == "SYS_FAN3", "Профиль MSI не определил SYS3");
        Check(HeaderRoles.Resolve(channel, "Other", "Other board").Role == FanRole.Unknown, "Профиль распространился на другую плату");
        foreach (var name in new[] { "CPU_FAN1", "CPU Fan", "cpu-fan 2" })
            Check(HeaderRoles.Resolve(channel with { Name = name }, "Other", "Other").Role == FanRole.Cpu, "CPU header не распознан");
        foreach (var name in new[] { "SYS_FAN3", "System Fan #4", "CHA_FAN1", "Chassis Fan", "Case Fan" })
            Check(HeaderRoles.Resolve(channel with { Name = name }, "Other", "Other").Role == FanRole.Case, "Case header не распознан");
        foreach (var name in new[] { "CPU_OPT", "CPU Optional", "Fan #1", "Chipset Fan", "VRM", "GPU Fan" })
            Check(HeaderRoles.Resolve(channel with { Name = name }, "Other", "Other").Role == FanRole.Unknown, "У неизвестного выхода угадана роль");
        Check(Known(channel with { Name = "CPU Pump" }).Role == FanRole.Pump, "Помпа перекрыта профилем платы");
        Check(Known(channel with { Origin = HardwareOrigin.Gpu }).Role == FanRole.Unknown, "GPU получил роль CPU");

        var original = new FanBinding { ControlId = channel.Id, RpmId = "/lpc/nct6797d/0/fan/1", Role = FanRole.Case,
            Label = "Ошибочно корпусной", MappingVerified = true, ReturnVerified = true, SpeedVerified = true, StopVerified = true };
        var corrected = BindingSetup.UpdateRole(original, FanRole.Cpu, "CPU_FAN1", Known(channel));
        Check(corrected.Role == FanRole.Cpu && !corrected.StopVerified && corrected.RpmId == original.RpmId, "Исправление CPU унаследовало остановку");
        var caseBinding = BindingSetup.UpdateRole(original, FanRole.Case, "Case", new("SYS_FAN3", FanRole.Case, "BoardProfile"));
        Check(caseBinding.StopVerified, "Повторное сохранение корпуса потеряло проверку остановки");
        foreach (var forbidden in new[] { Known(channel), new HeaderHint("CPU Pump", FanRole.Pump, "BackendName"), new HeaderHint("GPU Fan", FanRole.Unknown, "Excluded") })
            Check(!BindingSetup.UpdateRole(original, FanRole.Case, "Case", forbidden).StopVerified,
                "Ошибочное назначение CPU/помпы/GPU корпусом сохранило разрешение остановки");
        var pair = new ProbeChannelResult(channel, original.RpmId, "matched", true, true, 50, 1000);
        Denied(() => BindingSetup.Create(pair, FanRole.Case, "Case", new("CPU Pump", FanRole.Pump, "BackendName")));
        Denied(() => BindingSetup.Create(pair, FanRole.Case, "Case", new("GPU Fan", FanRole.Unknown, "Excluded")));
        var settings = new Settings { Bindings = [original] };
        settings.Profiles[0].Curves[original.ControlId] = [new(30, 70), new(85, 100)];
        BindingSetup.Apply(settings, [corrected]);
        Check(settings.Profiles[0].Curves[original.ControlId][0].Percent == 70, "Исправление роли уничтожило пользовательскую кривую");

        var cpu = corrected;
        var caseFan = new FanBinding { ControlId = "case-control", RpmId = "case-rpm", Role = FanRole.Case,
            MappingVerified = true, ReturnVerified = true, SpeedVerified = true };
        using var fake = new ReadOnlyFake(cpu.RpmId);
        using var engine = new Engine(fake, new Settings { Bindings = [cpu, caseFan] });
        engine.Tick();
        Check(engine.CaseOffProblem?.Contains("повторный запуск", StringComparison.OrdinalIgnoreCase) == true,
            "Мастер не объяснил отсутствие проверки остановки после успешного pairing");
        cpu.Role = FanRole.Case;
        Check(engine.CaseOffProblem?.Contains("Не определён CPU", StringComparison.Ordinal) == true, "Причина отсутствия CPU скрыта");
    }
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static void Denied(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("Исключённый header получил новую привязку");
    }
    private sealed class ReadOnlyFake(string cpuRpm) : IFanHardware
    {
        public IReadOnlyList<SensorInfo> Read() => [new("/intelcpu/0/temperature/0", "CPU Package", "CPU", "Temperature", 45), new(cpuRpm, "CPU", "Board", "Fan", 1000)];
        public void Set(string id, int percent) => throw new Exception("Readiness test must not write hardware");
        public void Default(string id) => throw new Exception("Readiness test must not write hardware");
        public void Dispose() { }
    }
}
