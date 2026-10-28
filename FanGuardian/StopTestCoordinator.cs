namespace FanGuardian;

internal sealed record StopTestResult(bool Verified, bool Cancelled = false, string? Error = null, string? Note = null);

// This coordinator never issues raw duty commands. The worker owns both the
// bounded stop interval and the subsequent full-speed restart.
internal sealed class StopTestCoordinator
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(500);
    private readonly IFanHardware hardware;
    private readonly Action<string> begin;
    private readonly Action<string> end;
    private readonly Func<string?> phase;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly Action? ensureSafe;

    public StopTestCoordinator(IFanHardware hardware, Action<string> begin, Action<string> end,
        Func<string?> phase, Func<TimeSpan, CancellationToken, Task>? delay = null, Action? ensureSafe = null)
    {
        this.hardware = hardware; this.begin = begin; this.end = end; this.phase = phase;
        this.delay = delay ?? Task.Delay; this.ensureSafe = ensureSafe;
    }

    public async Task<StopTestResult> RunAsync(FanBinding target, IReadOnlyList<FanBinding> bindings, CancellationToken token)
    {
        if (token.IsCancellationRequested) return new(false, true);
        IReadOnlyList<SensorInfo> initial;
        try
        {
            ensureSafe?.Invoke();
            initial = hardware.Read();
            if (!MayStart(target, bindings, initial, hardware.Channels))
                return new(false, Error: "Остановка разрешена только проверенной корпусной группе при работающем CPU-кулере и температуре ниже60°C.");
        }
        catch (Exception ex) { return new(false, Error: ex.Message); }
        var baseline = Rpm(initial, target)!.Value;
        string? error = null;
        try { begin(target.ControlId); }
        catch (Exception ex) { error = "Начало проверки не подтверждено: " + ex.Message; }

        // Cancellation cannot bypass the autonomous restart. Never request
        // Default while the motor is still stopped or merely starting.
        var observation = await WaitForRestartAsync(target, bindings, token);
        error ??= observation.Error;
        if (!observation.Ready)
            return new(false, token.IsCancellationRequested, error ?? "Повторный запуск не подтверждён; проверьте охлаждение.");
        try { end(target.ControlId); }
        catch (Exception ex)
        {
            return new(false, token.IsCancellationRequested, "Возврат исходного режима не подтверждён: " + ex.Message);
        }

        var restored = 0;
        for (var i = 0; i < 24; i++)
        {
            await delay(Interval, CancellationToken.None);
            try
            {
                var sensors = hardware.Read();
                if (!CpuSafe(bindings, sensors)) error ??= "Во время проверки потеряно подтверждение охлаждения CPU.";
                var rpm = Rpm(sensors, target);
                restored = rpm.HasValue && rpm >= target.MinimumRunningRpm &&
                    Math.Abs(rpm.Value - baseline) <= Math.Max(30, baseline * 0.12f) ? restored + 1 : 0;
                if (restored >= 2)
                    return new(error == null && observation.Stopped && !token.IsCancellationRequested,
                        token.IsCancellationRequested, error,
                        observation.Stopped ? null : "Вентилятор не остановился на0%; разрешение остановки не выдано.");
            }
            catch (Exception ex) { error ??= ex.Message; }
        }
        return new(false, token.IsCancellationRequested, error ?? "RPM не вернулись к исходному диапазону; проверьте охлаждение.");
    }

    private async Task<(bool Stopped, bool Ready, string? Error)> WaitForRestartAsync(FanBinding target,
        IReadOnlyList<FanBinding> bindings, CancellationToken token)
    {
        var stoppedCount = 0;
        var restartedCount = 0;
        var stopped = false;
        string? error = null;
        for (var i = 0; i < 48; i++)
        {
            await delay(Interval, CancellationToken.None);
            try
            {
                try { ensureSafe?.Invoke(); } catch (Exception ex) { error ??= ex.Message; }
                var sensors = hardware.Read();
                if (!CpuSafe(bindings, sensors)) error ??= "Во время проверки потеряно подтверждение охлаждения CPU.";
                var currentPhase = phase();
                var rpm = Rpm(sensors, target);
                if (currentPhase == "stopping")
                {
                    stoppedCount = rpm == 0 ? stoppedCount + 1 : 0;
                    if (stoppedCount >= 2) stopped = true;
                }
                else if (currentPhase == "restarting")
                {
                    restartedCount = rpm >= Math.Max(1, target.MinimumRunningRpm) ? restartedCount + 1 : 0;
                    if (restartedCount >= 2) return (stopped, true, error);
                }
                else return (stopped, false, error ?? "Сторож прервал проверку остановки; управление не разрешено.");
            }
            catch (Exception ex) { error ??= ex.Message; }
        }
        return (stopped, false, error ?? "Истёк срок ожидания повторного запуска; управление не разрешено.");
    }

    internal static bool MayStart(FanBinding target, IReadOnlyList<FanBinding> bindings,
        IReadOnlyList<SensorInfo> sensors, IReadOnlyList<ControlChannel> channels)
    {
        var channel = channels.FirstOrDefault(c => c.Id == target.ControlId);
        var header = channel == null ? null : HeaderRoles.Resolve(channel);
        return target.IsVerified && FanRoles.IsCase(target.Role) &&
            header is not null && header.Role is not (FanRole.Cpu or FanRole.Pump) && header.Source != "Excluded" &&
            channel?.AllowsFanWrites == true && channel.Origin == HardwareOrigin.Motherboard &&
            channel.Restore == RestoreStrategy.RestoreOriginal && channel.MinimumPercent == 0 && channel.MaximumPercent == 100 &&
            bindings.Count(b => b.IsVerified && b.ControlId == target.ControlId) == 1 &&
            !bindings.Any(b => b.IsVerified && b.Role == FanRole.Cpu && (b.ControlId == target.ControlId || b.RpmId == target.RpmId)) &&
            CpuSafe(bindings, sensors) && Rpm(sensors, target) >= Math.Max(1, target.MinimumRunningRpm);
    }

    private static bool CpuSafe(IReadOnlyList<FanBinding> bindings, IReadOnlyList<SensorInfo> sensors) =>
        CpuTemperatureReader.Read(sensors) is >= 20 and < 60 &&
        bindings.Any(b => b.IsVerified && b.Role == FanRole.Cpu) &&
        bindings.Where(b => b.IsVerified && b.Role == FanRole.Cpu).All(b => Rpm(sensors, b) >= Math.Max(1, b.MinimumRunningRpm));

    private static float? Rpm(IReadOnlyList<SensorInfo> sensors, FanBinding binding)
    {
        var value = sensors.FirstOrDefault(s => s.Type == "Fan" && s.Id == binding.RpmId)?.Value;
        return value.HasValue && float.IsFinite(value.Value) && value.Value >= 0 ? value : null;
    }
}
