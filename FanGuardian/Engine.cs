using System.Diagnostics;

namespace FanGuardian;

internal static class AppLog
{
    private static readonly object gate = new();
    public static bool Suppressed { get; set; }
    public static string PathName => Path.Combine(Settings.DirectoryPath, "events.log");
    public static void Write(string message)
    {
        if (Suppressed) return;
        try
        {
            lock (gate)
            {
                Directory.CreateDirectory(Settings.DirectoryPath);
                if (File.Exists(PathName) && new FileInfo(PathName).Length > 1_000_000)
                    File.Move(PathName, PathName + ".old", true);
                File.AppendAllText(PathName, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
            }
        }
        catch { /* Logging must never prevent a restore command. */ }
    }
}

internal sealed class Engine : IDisposable
{
    private readonly IFanHardware raw;
    private readonly Settings settings;
    private readonly HashSet<string> captured = new(StringComparer.Ordinal);
    private long? lastValidTemperature;
    private long? safetyStarted;
    private readonly Dictionary<string, int> missingRpmTicks = new(StringComparer.Ordinal);
    private FanMode? pendingMode;
    private long pendingStarted;
    public FanMode Mode { get; private set; } = FanMode.Bios;
    public float? CpuTemperature { get; private set; }
    public IReadOnlyList<SensorInfo> Sensors { get; private set; } = Array.Empty<SensorInfo>();
    public string Status { get; private set; } = "Ожидание показаний";
    public string? ControlBlockReason { get; private set; }
    public Engine(IFanHardware hardware, Settings settings) { raw = hardware; this.settings = settings; }
    public void Tick()
    {
        try
        {
            Sensors = raw.Read();
            CpuTemperature = CpuTemperatureReader.Read(Sensors);
            if (CpuTemperature is > 0 and < 120) lastValidTemperature = Stopwatch.GetTimestamp();
            else CpuTemperature = null;
            var stale = !HasFreshTemperature;
            if (Mode == FanMode.Bios && captured.Count > 0)
            {
                RestoreBios("Повтор попытки возврата");
                if (captured.Count > 0) return;
            }
            if (ControlBlockReason != null)
            {
                Status = BlockedStatus;
                return;
            }
            var temperatureLimit = Mode == FanMode.CaseOff || pendingMode.HasValue ? 65 : 80;
            if (stale || CpuTemperature >= temperatureLimit)
            {
                if (Mode != FanMode.Bios || pendingMode.HasValue) { RestoreBios("Защита: высокая температура или нет свежих показаний"); safetyStarted = Stopwatch.GetTimestamp(); }
                Status = stale ? "Температура недоступна; BIOS" : "Высокая температура; BIOS до ручного выбора режима";
                return;
            }
            if (pendingMode.HasValue)
            {
                if (Stopwatch.GetElapsedTime(pendingStarted) > TimeSpan.FromSeconds(15)) { pendingMode = null; Status = "Корпусные группы не восстановили RPM; BIOS"; return; }
                var caseReady = VerifiedBindings.Where(b => FanRoles.IsCase(b.Role) && b.StopVerified).All(HasRunningRpm);
                if (!caseReady) { Status = "BIOS: ожидание раскрутки корпусных вентиляторов"; return; }
                Mode = pendingMode.Value;
                pendingMode = null;
                AppLog.Write("Корпусные вентиляторы восстановили RPM; режим: " + Mode);
            }
            if (Mode == FanMode.Bios) { Status = IsCoolingDown ? "Режим платы: защитная пауза 5 минут" : "Приложение не задаёт скорость; активен исходный режим платы"; return; }
            if (Diagnostics.Read().OtherFanPrograms.Length > 0) { RestoreBios("Запущена другая программа управления вентиляторами"); Status = "Обнаружена другая программа управления; BIOS"; return; }
            if (IsCoolingDown) { RestoreBios("Защитная пауза"); return; }
            if (!CheckRunningRpm()) { RestoreBios("Защита: низкие или недоступные обороты управляемого вентилятора"); return; }
            var safe = new SafeHardware(raw, settings.Bindings);
            foreach (var b in VerifiedBindings)
            {
                if (Mode == FanMode.CaseOff && (!FanRoles.IsCase(b.Role) || !b.StopVerified))
                {
                    if (captured.Contains(b.ControlId))
                    {
                        safe.Default(b.ControlId);
                        captured.Remove(b.ControlId);
                    }
                    continue;
                }
                var percent = Mode == FanMode.CaseOff ? 0 : CurvePercent(b);
                safe.ValidateSet(b.ControlId, percent);
                // A backend may apply a write before it reports a failure.
                captured.Add(b.ControlId);
                safe.Set(b.ControlId, percent);
            }
            Status = Mode == FanMode.CaseOff ? "Остановлены проверенные корпусные группы; CPU-кулеры остаются под управлением платы" : "Ручные кривые активны";
        }
        catch (Exception ex)
        {
            AppLog.Write("Ошибка датчика или управления: " + ex.Message);
            RestoreBios("Ошибка оборудования");
            Status = ControlBlockReason is null ? "Ошибка оборудования; см. журнал. Проверьте охлаждение."
                : BlockedStatus + " Ошибка чтения оборудования; проверьте охлаждение.";
        }
    }
    private IEnumerable<FanBinding> VerifiedBindings => settings.Bindings.Where(b => b.IsVerified);
    private string BlockedStatus => "Управление заблокировано до перезапуска: " + ControlBlockReason;
    private bool IsCoolingDown => safetyStarted.HasValue && Stopwatch.GetElapsedTime(safetyStarted.Value) < TimeSpan.FromMinutes(5);
    private bool HasFreshTemperature => CpuTemperature.HasValue && lastValidTemperature.HasValue &&
        Stopwatch.GetElapsedTime(lastValidTemperature.Value) <= TimeSpan.FromSeconds(5);
    private bool HasRunningRpm(FanBinding binding) => Sensors.FirstOrDefault(s => s.Type == "Fan" && s.Id == binding.RpmId)?.Value is float rpm &&
        float.IsFinite(rpm) && rpm >= Math.Max(1, binding.MinimumRunningRpm);
    private bool CheckRunningRpm()
    {
        var healthy = true;
        foreach (var binding in VerifiedBindings.Where(b => Mode == FanMode.Manual || b.Role == FanRole.Cpu))
        {
            var ticks = HasRunningRpm(binding) ? 0 : missingRpmTicks.GetValueOrDefault(binding.ControlId) + 1;
            missingRpmTicks[binding.ControlId] = ticks;
            if (ticks >= 2) healthy = false;
        }
        return healthy;
    }
    private int CurvePercent(FanBinding b)
    {
        var profile = settings.Profiles[settings.SelectedProfile];
        var minimum = b.Role == FanRole.Cpu ? Math.Max(30, b.MinimumPercent) : Math.Max(0, b.MinimumPercent);
        var points = profile.Curves.GetValueOrDefault(b.ControlId) ?? Curves.Default(b.Role, settings.SelectedProfile)
            .Select(point => point with { Percent = Math.Max(minimum, point.Percent) }).ToList();
        Curves.Validate(points, minimum);
        return Curves.Evaluate(points, CpuTemperature!.Value);
    }
    public void SelectMode(FanMode requested)
    {
        if (!Enum.IsDefined(requested)) throw new ArgumentOutOfRangeException(nameof(requested));
        if (requested == FanMode.Bios) { RestoreBios("Выбор пользователя"); return; }
        if (ControlBlockReason != null) throw new InvalidOperationException(BlockedStatus);
        if (IsCoolingDown) throw new InvalidOperationException("Защитная пауза 5 минут");
        if (!HasFreshTemperature) throw new InvalidOperationException("Нет свежей температуры CPU");
        if (Diagnostics.Read().OtherFanPrograms.Length > 0)
            throw new InvalidOperationException("Запущена другая программа управления вентиляторами. Закройте её перед включением ручного режима.");
        if (captured.Count > 0 && Mode == FanMode.Bios)
        {
            RestoreBios("Проверка возврата перед сменой режима");
            if (captured.Count > 0) throw new InvalidOperationException("Возврат исходного режима не подтверждён");
        }
        if (!VerifiedBindings.Any()) throw new InvalidOperationException("Нет подтверждённых каналов");
        var bindings = VerifiedBindings.ToArray();
        if (bindings.Select(b => b.ControlId).Distinct(StringComparer.Ordinal).Count() != bindings.Length ||
            bindings.Select(b => b.RpmId).Distinct(StringComparer.Ordinal).Count() != bindings.Length ||
            bindings.Any(b => b.MinimumPercent is < 0 or > 100 || b.MinimumRunningRpm <= 0))
            throw new InvalidOperationException("Привязки дублируются или содержат недопустимые пределы");
        if (VerifiedBindings.Any(b => string.IsNullOrWhiteSpace(b.ControlId) || string.IsNullOrWhiteSpace(b.RpmId)))
            throw new InvalidOperationException("У подтверждённого канала нет управляющего выхода или тахометра");
        if (requested == FanMode.Manual && VerifiedBindings.Any(b => (b.Role == FanRole.Cpu || !b.StopVerified) && !HasRunningRpm(b)))
        {
            RestoreBios("Ручной режим не включён: запуск вентилятора не подтверждён");
            throw new InvalidOperationException("Ручной режим не включён: дождитесь ненулевых рабочих RPM. Запуск остановленной новой группы на минимальной скорости ещё не проверен.");
        }
        if (requested == FanMode.CaseOff && CaseOffProblem is { } problem)
            throw new InvalidOperationException(problem);
        if (Mode == FanMode.CaseOff && requested == FanMode.Manual)
        {
            RestoreBios("Переход от остановки к ручному режиму");
            if (captured.Count > 0) throw new InvalidOperationException("Возврат исходного режима не подтверждён");
            pendingMode = FanMode.Manual;
            pendingStarted = Stopwatch.GetTimestamp();
            Status = "BIOS: ожидание раскрутки корпусных вентиляторов";
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
        missingRpmTicks.Clear();
        if (failures.Count == 0) Status = ControlBlockReason is null
            ? "Приложение не задаёт скорость; активен исходный режим платы" : BlockedStatus;
        AppLog.Write($"Возврат BIOS ({reason}): " + (failures.Count == 0 ? "команды выполнены" : string.Join("; ", failures)));
        if (failures.Count > 0) Status = "Возврат BIOS не подтверждён; проверьте вентиляторы и журнал" +
            (ControlBlockReason is null ? "" : ". " + BlockedStatus);
    }
    public void BlockControl(string reason)
    {
        ControlBlockReason = string.IsNullOrWhiteSpace(reason) ? "Безопасность возврата режима не подтверждена" : reason.Trim();
        RestoreBios("Блокировка управления: " + ControlBlockReason);
    }
    public string? CaseOffProblem
    {
        get
        {
            if (CpuCoolingProblem is { } cpuProblem) return cpuProblem;
            if (!VerifiedBindings.Any(b => FanRoles.IsCase(b.Role))) return "Не назначены корпусные группы. Сохраните их назначение в мастере подключения.";
            if (!VerifiedBindings.Any(b => FanRoles.IsCase(b.Role) && b.StopVerified))
                return "Скорость корпусных групп проверена, но остановка и повторный запуск ещё не проверены. Нужна однократная проверка остановки.";
            return null;
        }
    }
    public string? CpuCoolingProblem
    {
        get
        {
            var cpu = VerifiedBindings.Where(b => b.Role == FanRole.Cpu).ToArray();
            if (cpu.Length == 0) return "Не определён CPU-кулер. В мастере «Подключить новую систему» проверьте назначение CPU_FAN и сохраните группы.";
            var missing = cpu.FirstOrDefault(b => !HasRunningRpm(b));
            if (missing == null) return null;
            var rpm = Sensors.FirstOrDefault(s => s.Type == "Fan" && s.Id == missing.RpmId)?.Value;
            return $"Нет рабочих RPM CPU-кулера «{missing.DisplayLabel}»: {(rpm.HasValue ? rpm.Value.ToString("F0") : "нет показаний")}, нужно не менее {missing.MinimumRunningRpm}. Корпусные вентиляторы не остановлены.";
        }
    }
    public void Dispose() { RestoreBios("Выход"); raw.Dispose(); }
}
