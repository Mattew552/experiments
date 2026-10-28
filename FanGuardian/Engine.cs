namespace FanGuardian;

internal static class AppLog
{
    private static readonly object gate = new();
    public static string PathName => Path.Combine(Settings.DirectoryPath, "events.log");
    public static void Write(string message)
    {
        lock (gate)
        {
            Directory.CreateDirectory(Settings.DirectoryPath);
            if (File.Exists(PathName) && new FileInfo(PathName).Length > 1_000_000)
            {
                File.Move(PathName, PathName + ".old", true);
            }
            File.AppendAllText(PathName, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
    }
}

internal sealed class Engine : IDisposable
{
    private readonly IFanHardware raw;
    private readonly Settings settings;
    private readonly HashSet<string> captured = new(StringComparer.Ordinal);
    private DateTimeOffset lastValidTemperature;
    private DateTimeOffset safetyUntil;
    private int missingRpmTicks;
    private FanMode? pendingMode;
    private DateTimeOffset pendingDeadline;
    public FanMode Mode { get; private set; } = FanMode.Bios;
    public float? CpuTemperature { get; private set; }
    public IReadOnlyList<SensorInfo> Sensors { get; private set; } = Array.Empty<SensorInfo>();
    public string Status { get; private set; } = "Ожидание показаний";
    public Engine(IFanHardware hardware, Settings settings) { raw = hardware; this.settings = settings; }
    public void Tick()
    {
        try
        {
            Sensors = raw.Read();
            CpuTemperature = CpuTemperatureReader.Read(Sensors);
            if (CpuTemperature is > 0 and < 120) lastValidTemperature = DateTimeOffset.UtcNow;
            else CpuTemperature = null;
            var stale = DateTimeOffset.UtcNow - lastValidTemperature > TimeSpan.FromSeconds(5);
            if (Mode == FanMode.Bios && captured.Count > 0) RestoreBios("Повтор попытки возврата");
            var temperatureLimit = Mode == FanMode.CaseOff || pendingMode.HasValue ? 65 : 80;
            if (stale || CpuTemperature >= temperatureLimit)
            {
                if (Mode != FanMode.Bios || pendingMode.HasValue) { RestoreBios("Защита: высокая температура или нет свежих показаний"); safetyUntil = DateTimeOffset.UtcNow.AddMinutes(5); }
                Status = stale ? "Температура недоступна; BIOS" : "Высокая температура; BIOS до ручного выбора режима";
                return;
            }
            if (pendingMode.HasValue)
            {
                if (DateTimeOffset.UtcNow > pendingDeadline) { pendingMode = null; Status = "Передние вентиляторы не восстановили RPM; BIOS"; return; }
                var frontReady = settings.Bindings.Where(b => FanRoles.IsFront(b.Role) && b.StopVerified)
                    .All(b => Sensors.FirstOrDefault(s => s.Id == b.RpmId)?.Value is >= 250);
                if (!frontReady) { Status = "BIOS: ожидание раскрутки передних вентиляторов"; return; }
                Mode = pendingMode.Value;
                pendingMode = null;
                AppLog.Write("Передние вентиляторы восстановили RPM; режим: " + Mode);
            }
            if (Mode == FanMode.Bios) { Status = DateTimeOffset.UtcNow < safetyUntil ? "Охлаждение BIOS: защитная пауза 5 минут" : "Охлаждение BIOS"; return; }
            if (Diagnostics.Read().OtherFanPrograms.Length > 0) { RestoreBios("Запущена другая программа управления вентиляторами"); Status = "Обнаружена другая программа управления; BIOS"; return; }
            if (DateTimeOffset.UtcNow < safetyUntil) { RestoreBios("Защитная пауза"); return; }
            if (Mode == FanMode.CaseOff)
            {
                var cpuBinding = settings.Bindings.FirstOrDefault(b => b.Role == FanRole.Cpu && b.MappingVerified);
                if (cpuBinding != null && (Sensors.FirstOrDefault(s => s.Id == cpuBinding.RpmId)?.Value is null or < 250))
                {
                    if (++missingRpmTicks >= 2) { RestoreBios("Защита: нет RPM CPU при остановке корпуса"); return; }
                }
                else missingRpmTicks = 0;
            }
            var safe = new SafeHardware(raw, settings.Bindings);
            foreach (var b in settings.Bindings.Where(b => b.MappingVerified && b.ReturnVerified && b.SpeedVerified))
            {
                var percent = Mode == FanMode.CaseOff
                    ? (FanRoles.IsFront(b.Role) && b.StopVerified ? 0 : Math.Max(b.MinimumPercent, 30))
                    : CurvePercent(b);
                if (Mode == FanMode.CaseOff && b.Role == FanRole.Cpu) { if (captured.Remove(b.ControlId)) safe.Default(b.ControlId); continue; }
                safe.Set(b.ControlId, percent);
                captured.Add(b.ControlId);
                var rpm = Sensors.FirstOrDefault(s => s.Id == b.RpmId)?.Value;
                if (b.Role == FanRole.Cpu && (rpm is null or < 250))
                {
                    if (++missingRpmTicks >= 2) { RestoreBios("Защита: низкие обороты CPU"); return; }
                }
                else missingRpmTicks = 0;
            }
            Status = Mode == FanMode.CaseOff ? "Остановлены только передние каналы; CPU и задний Molex продолжают работать" : "Ручные кривые активны";
        }
        catch (Exception ex)
        {
            AppLog.Write("Ошибка датчика или управления: " + ex.Message);
            RestoreBios("Ошибка оборудования");
            Status = "Ошибка оборудования; см. журнал. Проверьте охлаждение.";
        }
    }
    private int CurvePercent(FanBinding b)
    {
        var profile = settings.Profiles[settings.SelectedProfile];
        var points = profile.Curves.GetValueOrDefault(b.ControlId) ?? Curves.Default(b.Role, settings.SelectedProfile);
        Curves.Validate(points, b.Role == FanRole.Cpu ? Math.Max(30, b.MinimumPercent) : Math.Max(0, b.MinimumPercent));
        return Curves.Evaluate(points, CpuTemperature!.Value);
    }
    public void SelectMode(FanMode requested)
    {
        if (requested == FanMode.Bios) { RestoreBios("Выбор пользователя"); return; }
        if (DateTimeOffset.UtcNow < safetyUntil) throw new InvalidOperationException("Защитная пауза 5 минут");
        if (CpuTemperature is null || DateTimeOffset.UtcNow - lastValidTemperature > TimeSpan.FromSeconds(5)) throw new InvalidOperationException("Нет свежей температуры CPU");
        if (Diagnostics.Read().OtherFanPrograms.Length > 0)
            throw new InvalidOperationException("Запущена другая программа управления вентиляторами. Закройте её перед включением ручного режима.");
        if (!settings.Bindings.Any(b => b.MappingVerified && b.ReturnVerified && b.SpeedVerified)) throw new InvalidOperationException("Нет подтверждённых каналов");
        if (requested == FanMode.CaseOff &&
            (settings.Bindings.Count(b => b.MappingVerified && b.ReturnVerified && b.SpeedVerified && FanRoles.IsFront(b.Role) && b.StopVerified) != 2 ||
             !settings.Bindings.Any(b => b.Role == FanRole.Cpu && b.MappingVerified && b.ReturnVerified && b.SpeedVerified)))
            throw new InvalidOperationException("Не подтверждены оба передних канала и CPU-кулер");
        if (Mode == FanMode.CaseOff && requested == FanMode.Manual)
        {
            RestoreBios("Переход от остановки к ручному режиму");
            pendingMode = FanMode.Manual;
            pendingDeadline = DateTimeOffset.UtcNow.AddSeconds(15);
            Status = "BIOS: ожидание раскрутки передних вентиляторов";
            AppLog.Write("Ожидание RPM перед ручным режимом");
            return;
        }
        Mode = requested;
        AppLog.Write("Режим: " + requested);
        Tick();
    }
    public void RestoreBios(string reason)
    {
        var toRestore = captured.ToArray();
        var failures = new List<string>();
        foreach (var id in toRestore)
        {
            try { raw.Default(id); captured.Remove(id); }
            catch (Exception ex) { failures.Add(id + ": " + ex.Message); }
        }
        Mode = FanMode.Bios;
        pendingMode = null;
        if (failures.Count == 0) Status = "Охлаждение BIOS";
        AppLog.Write($"Возврат BIOS ({reason}): " + (failures.Count == 0 ? "команды выполнены" : string.Join("; ", failures)));
        if (failures.Count > 0) Status = "Возврат BIOS не подтверждён; проверьте вентиляторы и журнал";
    }
    public void Dispose() { RestoreBios("Выход"); raw.Dispose(); }
}
