namespace FanGuardian;

internal sealed record ProbeProgress(string Message, int Completed, int Total);
internal sealed record ProbeChannelResult(ControlChannel Control, string? RpmId, string Description,
    bool AssociationVerified, bool RestoreVerified, int BaselinePercent = 0, float BaselineRpm = 0);
internal sealed record ProbeRunResult(IReadOnlyList<ProbeChannelResult> Results, bool Cancelled = false, string? Error = null);

// The worker owns the hardware and enforces its independent probe lease. This
// coordinator only samples that same owner and requests bounded high pulses.
internal sealed class ProbeCoordinator
{
    private const int WindowSize = 5;
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(500);
    private readonly IFanHardware hardware;
    private readonly Action<string> start;
    private readonly Action<string> end;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly Action? ensureSafe;

    public ProbeCoordinator(IFanHardware hardware, Action<string> start, Action<string> end,
        Func<TimeSpan, CancellationToken, Task>? delay = null, Action? ensureSafe = null)
    {
        this.hardware = hardware;
        this.start = start;
        this.end = end;
        this.delay = delay ?? Task.Delay;
        this.ensureSafe = ensureSafe;
    }

    public async Task<ProbeRunResult> RunAsync(IProgress<ProbeProgress>? progress, CancellationToken token)
    {
        var results = new List<ProbeChannelResult>();
        try
        {
            var initial = ReadSafe();
            if (!initial.Any(s => FanProbe.IsTachometer(s) && s.Value > 0))
                return new(results, Error: "Нет активного тахометра; охлаждение не подтверждено. Команды не отправлены.");
            var channels = hardware.Channels.Where(c => c.AllowsFanWrites)
                .OrderBy(c => c.DeviceId, StringComparer.Ordinal).ThenBy(c => c.Id, StringComparer.Ordinal).ToArray();
            if (channels.Length == 0) return new(results, Error: "Нет каналов с проверенным способом восстановления.");
            for (var index = 0; index < channels.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                var channel = channels[index];
                progress?.Report(new($"{channel.Name}: исходные показания", index, channels.Length));
                var current = ReadSafe();
                if (!FanProbe.MayStart(current, channel, 100))
                {
                    results.Add(new(channel, null, "Проба недоступна: нет достоверного duty ниже 95% или подходящих возможностей.", false, false));
                    continue;
                }
                var baseline = await ObserveAsync(channel, WindowSize, 24, token);
                if (baseline is null)
                {
                    results.Add(new(channel, null, "Исходные RPM нестабильны или датчики пропадают; канал не проверялся.", false, false));
                    continue;
                }
                var duty = baseline.Sensors.FirstOrDefault(s => s.Id == channel.Id)?.Value;
                if (duty is not (>= 0 and < 95))
                {
                    results.Add(new(channel, null, "Исходный duty изменился; проба пропущена.", false, false));
                    continue;
                }
                var first = await PulseAsync(channel, baseline, progress, index, channels.Length, token);
                if (first.Response.Count != 1)
                {
                    results.Add(new(channel, null, first.Response.Count == 0
                        ? "Повторяемый рост RPM не обнаружен; наличие вентилятора не определено."
                        : "Изменились несколько тахометров; требуется проверка общей группы.", false, true, (int)Math.Ceiling(duty.Value)));
                    continue;
                }
                var secondBaseline = first.Restored;
                var second = await PulseAsync(channel, secondBaseline, progress, index, channels.Length, token);
                var rpmId = first.Response[0];
                var matched = second.Response.Count == 1 && second.Response[0] == rpmId;
                results.Add(new(channel, matched ? rpmId : null, matched
                    ? "Связь повторена двумя пробами, возврат RPM подтверждён. Назначение вентилятора нужно указать."
                    : "Вторая проба не повторила соответствие; канал остаётся неподтверждённым.",
                    matched, true, (int)Math.Ceiling(duty.Value), baseline.Values.GetValueOrDefault(rpmId)?.Median ?? 0));
            }
            // Distinct outputs affecting the same tachometer can be a shared
            // control domain. Do not authorize either without a device profile.
            var duplicateRpmIds = results.Where(r => r.AssociationVerified && r.RpmId != null)
                .GroupBy(r => r.RpmId, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
            for (var i = 0; i < results.Count; i++)
                if (duplicateRpmIds.Contains(results[i].RpmId)) results[i] = results[i] with
                {
                    AssociationVerified = false,
                    Description = "На этот тахометр влияют несколько выходов; независимое управление не подтверждено."
                };
            progress?.Report(new("Проверка завершена", channels.Length, channels.Length));
            return new(results);
        }
        catch (OperationCanceledException) { return new(results, Cancelled: true); }
        catch (Exception ex) { return new(results, Error: ex.Message); }
    }

    private async Task<(IReadOnlyList<string> Response, Observation Restored)> PulseAsync(ControlChannel channel,
        Observation baseline, IProgress<ProbeProgress>? progress, int completed, int total, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var current = ReadSafe();
        if (!FanProbe.MayStart(current, channel, 100)) throw new InvalidOperationException("Условия безопасной пробы изменились.");
        progress?.Report(new($"{channel.Name}: увеличение до 100%", completed, total));
        Observation? high = null;
        Exception? failure = null;
        var started = false;
        try
        {
            // Assume a failed IPC start may still have reached the worker, so
            // always ask the owner to restore after attempting the command.
            started = true;
            start(channel.Id);
            high = await ObserveAsync(channel, 10, 28, token, maximumElapsed: TimeSpan.FromSeconds(15));
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            if (started)
            {
                try { end(channel.Id); }
                catch (Exception ex) { throw new InvalidOperationException("Возврат исходного режима не подтверждён. Проверка остановлена; проверьте охлаждение.", ex); }
            }
        }

        progress?.Report(new($"{channel.Name}: проверка возврата RPM", completed, total));
        // Cancellation must not bypass verification of the restoration already
        // requested from the worker. This phase is bounded by sample count.
        var restored = await ObserveAsync(channel, WindowSize, 30, CancellationToken.None, value => Returned(baseline, high, value));
        if (restored is null)
            throw new InvalidOperationException("RPM не вернулись к исходному диапазону. Дальнейшие пробы остановлены.");
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        if (high is null || Math.Abs(high.CpuTemperature - baseline.CpuTemperature) > 3) return (Array.Empty<string>(), restored);
        var response = high.Values.Where(pair => baseline.Values.TryGetValue(pair.Key, out var original) &&
                pair.Value.Median - original.Median >= ResponseThreshold(original.Median, original.Range, pair.Value.Range))
            .Select(pair => pair.Key).ToArray();
        return (response, restored);
    }

    private async Task<Observation?> ObserveAsync(ControlChannel channel, int minimumSamples, int maximumSamples, CancellationToken token,
        Func<Observation, bool>? accept = null, TimeSpan? maximumElapsed = null)
    {
        var samples = new Queue<IReadOnlyList<SensorInfo>>();
        var started = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < maximumSamples; i++)
        {
            token.ThrowIfCancellationRequested();
            if (maximumElapsed.HasValue && started.Elapsed >= maximumElapsed.Value) return null;
            await delay(SampleInterval, token);
            if (maximumElapsed.HasValue && started.Elapsed >= maximumElapsed.Value) return null;
            var snapshot = ReadSafe();
            if (maximumElapsed.HasValue && started.Elapsed >= maximumElapsed.Value) return null;
            samples.Enqueue(snapshot);
            while (samples.Count > WindowSize) samples.Dequeue();
            if (i + 1 < minimumSamples || samples.Count < WindowSize) continue;
            var observation = Summarize(channel, samples.ToArray());
            if (observation != null && observation.Values.Values.All(v => v.Range <= Math.Max(20, v.Median * 0.04f)) &&
                (accept is null || accept(observation)))
                return observation;
        }
        return null;
    }

    private IReadOnlyList<SensorInfo> ReadSafe()
    {
        ensureSafe?.Invoke();
        var sensors = hardware.Read();
        if (CpuTemperatureReader.Read(sensors) is not (>= 20 and < 65))
            throw new InvalidOperationException("Проба остановлена: температура CPU недоступна или достигла 65 °C.");
        return sensors;
    }

    private static Observation? Summarize(ControlChannel channel, IReadOnlyList<IReadOnlyList<SensorInfo>> samples)
    {
        var dictionaries = samples.Select(sample => sample.Where(s => FanProbe.IsTachometer(s) &&
                s.Origin == channel.Origin && FanProbe.DeviceId(s) == channel.DeviceId)
            .GroupBy(s => s.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Value!.Value, StringComparer.Ordinal)).ToArray();
        if (dictionaries.Length == 0 || dictionaries[0].Count == 0) return null;
        // A disappearing sensor invalidates the window; it is not a zero.
        if (dictionaries.Any(d => d.Count != dictionaries[0].Count || dictionaries[0].Keys.Any(k => !d.ContainsKey(k)))) return null;
        var values = dictionaries[0].Keys.ToDictionary(id => id, id =>
        {
            var sorted = dictionaries.Select(d => d[id]).Order().ToArray();
            return new SampleValue(sorted[sorted.Length / 2], sorted[^1] - sorted[0]);
        }, StringComparer.Ordinal);
        var temperatures = samples.Select(s => CpuTemperatureReader.Read(s)!.Value).Order().ToArray();
        return new Observation(samples[^1], values, temperatures[temperatures.Length / 2]);
    }

    private static float ResponseThreshold(float baseline, float baselineNoise, float highNoise) =>
        Math.Max(10, Math.Max(baseline * 0.08f, Math.Max(baselineNoise, highNoise) * 4));

    internal static bool ReturnedRpm(float baseline, float high, float restored, float baselineNoise = 0, float highNoise = 0)
    {
        var tolerance = Math.Max(30, Math.Max(baseline * 0.12f, baselineNoise * 3));
        var delta = high - baseline;
        // A response must visibly reverse. A broad baseline tolerance alone
        // would incorrectly call a weak response "restored" while still high.
        if (delta >= ResponseThreshold(baseline, baselineNoise, highNoise))
            tolerance = Math.Min(tolerance, delta * 0.35f);
        return Math.Abs(restored - baseline) <= tolerance;
    }

    private static bool Returned(Observation baseline, Observation? high, Observation restored) => baseline.Values.All(pair =>
        restored.Values.TryGetValue(pair.Key, out var value) && ReturnedRpm(pair.Value.Median,
            high?.Values.GetValueOrDefault(pair.Key)?.Median ?? pair.Value.Median,
            value.Median, pair.Value.Range, high?.Values.GetValueOrDefault(pair.Key)?.Range ?? 0));

    private sealed record SampleValue(float Median, float Range);
    private sealed record Observation(IReadOnlyList<SensorInfo> Sensors, IReadOnlyDictionary<string, SampleValue> Values, float CpuTemperature);
}
