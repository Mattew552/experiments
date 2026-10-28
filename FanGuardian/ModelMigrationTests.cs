namespace FanGuardian;

internal static class ModelMigrationTests
{
    private sealed class Fake : IFanHardware
    {
        public IReadOnlyList<SensorInfo> Sensors { get; set; } = [];
        public IReadOnlyList<ControlChannel> Channels { get; set; } = [];
        public readonly List<string> Writes = [];
        public string? FailSet;
        public string? FailDefaultOnce;
        public IReadOnlyList<SensorInfo> Read() => Sensors;
        public void Set(string id, int value)
        {
            Writes.Add(id + ":" + value);
            if (FailSet == id) throw new InvalidOperationException("Partial write failure");
        }
        public void Default(string id)
        {
            Writes.Add(id + ":default");
            if (FailDefaultOnce == id) { FailDefaultOnce = null; throw new InvalidOperationException("Restore failed once"); }
        }
        public void Dispose() { }
    }

    public static void Run()
    {
        var legacy = SettingsForIdentity(3);
        if (legacy.ApplyIdentity("machine", "board-a", true) != SettingsIdentityStatus.Migrated ||
            legacy.Bindings.Count != 1 || legacy.Bindings[0].Label != "Моё название" || legacy.HardwareFingerprint != "board-a")
            throw new Exception("Проверенная legacy-привязка или имя потеряны при миграции");
        if (legacy.ApplyIdentity("machine", "board-a", false) != SettingsIdentityStatus.Current || legacy.Bindings.Count != 1)
            throw new Exception("Текущая аппаратная привязка ошибочно отключена");
        foreach (var scenario in new (string Machine, string Board, bool Allow)[] { ("other", "board-a", true), ("machine", "board-b", true), ("machine", "", true) })
        {
            var settings = SettingsForIdentity(4);
            settings.HardwareFingerprint = "board-a";
            if (settings.ApplyIdentity(scenario.Machine, scenario.Board, scenario.Allow) != SettingsIdentityStatus.Rejected || settings.Bindings.Count != 0)
                throw new Exception("Привязки перенеслись на другое или неизвестное оборудование");
        }
        var unknownLegacy = SettingsForIdentity(3);
        if (unknownLegacy.ApplyIdentity("machine", "board-a", false) != SettingsIdentityStatus.Rejected || unknownLegacy.Bindings.Count != 0)
            throw new Exception("Непроверенный legacy ПК автоматически получил разрешения");
        if ((int)FanRole.Cpu != 0 || (int)FanRole.FrontTop != 1 || (int)FanRole.FrontPair != 2 ||
            !FanRoles.IsCase(FanRole.Case) || FanRoles.IsControllable(FanRole.Pump) || FanRoles.IsControllable(FanRole.Unknown))
            throw new Exception("Числовые роли legacy или фильтр управляемых групп изменены");

        var bindings = new[]
        {
            Binding(0, FanRole.Cpu), Binding(1, FanRole.Case, stop: true), Binding(2, FanRole.Case),
            Binding(3, FanRole.Case, stop: true), Binding(4, FanRole.Case), Binding(5, FanRole.Pump), Binding(6, FanRole.Unknown)
        };
        bindings[4].MinimumPercent = 80;
        var hardware = Hardware(bindings);
        using (var engine = new Engine(hardware, new Settings { Bindings = bindings.ToList() }))
        {
            engine.Tick();
            engine.SelectMode(FanMode.Manual);
            if (hardware.Writes.Any(w => w.StartsWith(Id(5), StringComparison.Ordinal) || w.StartsWith(Id(6), StringComparison.Ordinal)))
                throw new Exception("Помпа или неизвестная группа получили обычную команду");
            if (Enumerable.Range(1, 4).Any(index => !hardware.Writes.Any(w => w.StartsWith(Id(index) + ":", StringComparison.Ordinal))) ||
                !hardware.Writes.Contains(Id(4) + ":80"))
                throw new Exception("Ручной режим ограничил число групп или не учёл проверенный минимум");
            engine.SelectMode(FanMode.CaseOff);
            if (!hardware.Writes.Contains(Id(1) + ":0") || !hardware.Writes.Contains(Id(3) + ":0") ||
                hardware.Writes.Contains(Id(0) + ":0") || hardware.Writes.Contains(Id(2) + ":0") || hardware.Writes.Contains(Id(4) + ":0") ||
                !hardware.Writes.Contains(Id(2) + ":default") || !hardware.Writes.Contains(Id(4) + ":default"))
                throw new Exception("Режим остановки затронул непроверенную группу");
            var start = hardware.Writes.Count;
            hardware.Sensors = hardware.Sensors.Select(s => s.Type == "Temperature" ? s with { Value = 0 } : s).ToArray();
            engine.Tick();
            if (engine.Mode != FanMode.Bios || hardware.Writes.Skip(start).Any(w => !w.EndsWith(":default", StringComparison.Ordinal)))
                throw new Exception("При 0°C продолжились команды остановки");
        }

        hardware = Hardware(bindings);
        using (var engine = new Engine(hardware, new Settings { Bindings = bindings.ToList() }))
        {
            engine.Tick(); engine.SelectMode(FanMode.Manual);
            hardware.Sensors = hardware.Sensors.Select(s => s.Id == Rpm(0) ? s with { Value = 0 } : s).ToArray();
            engine.Tick(); engine.Tick();
            if (engine.Mode != FanMode.Bios) throw new Exception("Корпусные группы сбросили счётчик потери RPM CPU");
        }

        hardware = Hardware(bindings);
        using (var engine = new Engine(hardware, new Settings { Bindings = bindings.ToList() }))
        {
            engine.Tick(); engine.SelectMode(FanMode.Manual);
            hardware.Sensors = hardware.Sensors.Select(s => s.Id == Rpm(2) ? s with { Value = 0 } : s).ToArray();
            engine.Tick(); engine.Tick();
            if (engine.Mode != FanMode.Bios) throw new Exception("Остановка корпусного вентилятора в ручном режиме не вернула управление плате");
        }

        hardware = Hardware(bindings);
        hardware.Sensors = hardware.Sensors.Select(s => s.Id == Rpm(2) ? s with { Value = 0 } : s).ToArray();
        using (var engine = new Engine(hardware, new Settings { Bindings = bindings.ToList() }))
        {
            engine.Tick();
            ExpectDenied(() => engine.SelectMode(FanMode.Manual));
            if (engine.Mode != FanMode.Bios || hardware.Writes.Count != 0)
                throw new Exception("Новой остановленной группе отправлена непроверенная команда запуска");
        }

        hardware = Hardware(bindings);
        using (var engine = new Engine(hardware, new Settings { Bindings = bindings.ToList() }))
        {
            engine.Tick(); engine.SelectMode(FanMode.CaseOff);
            hardware.Sensors = hardware.Sensors.Select(s => s.Id == Rpm(1) || s.Id == Rpm(3) ? s with { Value = 0 } : s).ToArray();
            engine.SelectMode(FanMode.Manual);
            engine.Tick();
            if (engine.Mode != FanMode.Bios) throw new Exception("После остановки корпусных групп не дождались раскрутки");
            hardware.Sensors = hardware.Sensors.Select(s => s.Type == "Fan" ? s with { Value = 1000 } : s).ToArray();
            engine.Tick();
            if (engine.Mode != FanMode.Manual) throw new Exception("Проверенный переход после остановки перестал включать ручной режим");
        }

        hardware = Hardware(bindings);
        using (var engine = new Engine(hardware, new Settings { Bindings = bindings.ToList() }))
        {
            engine.Tick(); engine.SelectMode(FanMode.Manual);
            hardware.FailDefaultOnce = Id(1);
            engine.BlockControl("Не подтверждён физический возврат RPM");
            if (engine.ControlBlockReason is null || engine.Mode != FanMode.Bios ||
                !engine.Status.Contains("Возврат BIOS не подтверждён", StringComparison.Ordinal))
                throw new Exception("Блокировка скрыла неуспешный возврат");
            engine.Tick();
            var writes = hardware.Writes.Count;
            engine.Tick();
            ExpectDenied(() => engine.SelectMode(FanMode.Manual));
            ExpectDenied(() => engine.SelectMode(FanMode.CaseOff));
            engine.SelectMode(FanMode.Bios);
            if (hardware.Writes.Count != writes || engine.ControlBlockReason is null ||
                !engine.Status.Contains("Управление заблокировано", StringComparison.Ordinal))
                throw new Exception("Повторный тик или выбор режима сняли блокировку текущей сессии");
        }

        hardware = Hardware(bindings);
        hardware.FailSet = Id(1);
        hardware.FailDefaultOnce = Id(1);
        using (var engine = new Engine(hardware, new Settings { Bindings = bindings.ToList() }))
        {
            engine.Tick(); engine.SelectMode(FanMode.Manual);
            var attempts = hardware.Writes.Count(w => w == Id(1) + ":default");
            engine.Tick();
            if (attempts != 1 || hardware.Writes.Count(w => w == Id(1) + ":default") != 2)
                throw new Exception("Неудачная запись/возврат потеряли канал из повторного восстановления");
        }
    }

    private static void ExpectDenied(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("Небезопасный режим не был отклонён");
    }

    private static Settings SettingsForIdentity(int schema) => new()
    {
        SchemaVersion = schema, MachineFingerprint = "machine",
        Bindings = [new FanBinding { Label = "Моё название", Role = FanRole.FrontPair }]
    };
    private static string Id(int index) => $"/lpc/test/control/{index}";
    private static string Rpm(int index) => $"/lpc/test/fan/{index}";
    private static FanBinding Binding(int index, FanRole role, bool stop = false) => new()
    {
        ControlId = Id(index), RpmId = Rpm(index), Role = role, MinimumPercent = 40,
        MappingVerified = true, ReturnVerified = true, SpeedVerified = true, StopVerified = stop
    };
    private static Fake Hardware(IEnumerable<FanBinding> bindings) => new()
    {
        Sensors = new[] { new SensorInfo("/amdcpu/0/temperature/2", "Core (Tctl/Tdie)", "AMD Ryzen", "Temperature", 45) }
            .Concat(bindings.Select(b => new SensorInfo(b.RpmId, b.DisplayLabel, "Test chip", "Fan", 1000))).ToArray(),
        Channels = bindings.Select(b => new ControlChannel(b.ControlId, "/lpc/test", b.DisplayLabel,
            HardwareOrigin.Motherboard, RestoreStrategy.RestoreOriginal, true)).ToArray()
    };
}
