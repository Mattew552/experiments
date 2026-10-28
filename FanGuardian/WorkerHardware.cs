using System.Diagnostics;
using System.Text.Json;

namespace FanGuardian;

internal sealed record WorkerRequest(long Sequence, string Operation, string ControlId, int Percent);
internal sealed record WorkerReply(long Sequence, bool Success, string Error, HardwareSnapshot? Snapshot = null);

internal static class WorkerIpcFiles
{
    public static string ReadText(string path)
    {
        // Atomic replacement requires FILE_SHARE_DELETE on every reader on
        // Windows, including the UI's polling of the previous reply.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static void WriteAtomic(string path, string text)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, text);
        var timer = Stopwatch.StartNew();
        while (true)
        {
            try { File.Move(temporary, path, overwrite: true); return; }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) &&
                                       timer.Elapsed < TimeSpan.FromMilliseconds(100) && File.Exists(temporary))
            {
                // Short-lived readers from an older client or file scanner can
                // still hold the destination. Retry without changing access.
                Thread.Sleep(10);
            }
        }
    }
}

// A single worker owns driver reads, writes and the backend's original mode snapshot.
internal sealed class WorkerHardware : IFanHardware
{
    private readonly string directory;
    private readonly object gate = new();
    private readonly Process worker;
    private long sequence;
    private bool disposed;
    public IReadOnlyList<ControlChannel> Channels { get; private set; } = Array.Empty<ControlChannel>();
    public string? StopTestPhase { get; private set; }

    public WorkerHardware(string heartbeat)
    {
        directory = Path.Combine(Settings.DirectoryPath, "worker-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(heartbeat, DateTimeOffset.UtcNow.ToString("O"));
        worker = Program.StartWorker(Environment.ProcessId, heartbeat, directory);
        try
        {
            var ready = Path.Combine(directory, "ready");
            var timer = Stopwatch.StartNew();
            while (!File.Exists(ready) && timer.Elapsed < TimeSpan.FromSeconds(10) && !worker.HasExited) Thread.Sleep(100);
            if (!File.Exists(ready)) throw new InvalidOperationException("Сторож управления не запустился; запись запрещена");
            var status = File.ReadAllText(ready);
            if (status != "ok") throw new InvalidOperationException("Сторож управления: " + status);
            Read();
        }
        catch { worker.Dispose(); throw; }
    }

    public IReadOnlyList<SensorInfo> Read()
    {
        var snapshot = Send("read", "", 0).Snapshot ?? throw new InvalidDataException("Сторож не прислал показания");
        Channels = snapshot.Channels;
        StopTestPhase = snapshot.StopTestPhase;
        return snapshot.Sensors;
    }
    public void Set(string controlId, int percent) => Send("set", controlId, percent);
    public void Default(string controlId) => Send("default", controlId, 0);
    public void ProbeStart(string controlId) => Send("probe-start", controlId, 100);
    public void ProbeEnd(string controlId) => Send("probe-end", controlId, 0);
    public void ReloadBindings() => Send("reload-bindings", "", 0);
    public void BlockControl() => Send("block-control", "", 0);
    public void BeginStopTest(string controlId) => Send("stop-test-start", controlId, 0);
    public void EndStopTest(string controlId) => Send("stop-test-end", controlId, 0);

    private WorkerReply Send(string operation, string id, int percent)
    {
        lock (gate)
        {
            if (disposed) throw new ObjectDisposedException(nameof(WorkerHardware));
            if (worker.HasExited) throw new InvalidOperationException("Сторож управления завершился; запись запрещена");
            var number = ++sequence;
            var requestPath = Path.Combine(directory, "request.json");
            WorkerIpcFiles.WriteAtomic(requestPath, JsonSerializer.Serialize(new WorkerRequest(number, operation, id, percent)));
            var replyPath = Path.Combine(directory, "reply.json");
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(5))
            {
                if (File.Exists(replyPath))
                {
                    WorkerReply? reply = null;
                    try { reply = JsonSerializer.Deserialize<WorkerReply>(WorkerIpcFiles.ReadText(replyPath)); }
                    catch (IOException) { }
                    if (reply?.Sequence == number)
                    {
                        if (!reply.Success) throw new InvalidOperationException("Сторож: " + reply.Error);
                        return reply;
                    }
                }
                if (worker.HasExited) throw new InvalidOperationException("Сторож управления завершился; проверьте охлаждение");
                Thread.Sleep(50);
            }
            throw new TimeoutException("Нет ответа сторожа; возврат управления плате не подтверждён");
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        try { Send("shutdown", "", 0); } catch (Exception ex) { AppLog.Write("Завершение сторожа: " + ex.Message); }
        try { if (!worker.WaitForExit(5000)) AppLog.Write("Сторож не завершился за 5 секунд"); }
        catch (Exception ex) { AppLog.Write("Ожидание сторожа: " + ex.Message); }
        worker.Dispose();
        disposed = true;
    }
}

internal static class WorkerHost
{
    public static void Run(int parentPid, string heartbeat, string directory,
        Func<IFanHardware>? createHardware = null, Func<Settings>? loadSettings = null,
        Func<bool>? otherFanProgramRunning = null)
    {
        createHardware ??= () => new LhmHardware();
        loadSettings ??= Settings.Load;
        otherFanProgramRunning ??= () => Diagnostics.Read().OtherFanPrograms.Length > 0;
        var ready = Path.Combine(directory, "ready");
        var touched = new HashSet<string>(StringComparer.Ordinal);
        var pendingRestore = new HashSet<string>(StringComparer.Ordinal);
        var pendingRestart = new HashSet<string>(StringComparer.Ordinal);
        string? activeProbe = null;
        string? activeStop = null;
        string? stopPhase = null;
        TimeSpan stopDeadline = default;
        TimeSpan nextStopSafetyCheck = default;
        var controlBlocked = false;
        var clock = Stopwatch.StartNew();
        TimeSpan probeDeadline = default;
        TimeSpan nextProbeSafetyCheck = default;
        TimeSpan nextRestoreAttempt = default;
        var lastHeartbeatStamp = File.Exists(heartbeat) ? File.GetLastWriteTimeUtc(heartbeat) : DateTime.MinValue;
        var lastHeartbeatAt = clock.Elapsed;
        try
        {
            var settings = loadSettings();
            using var hardware = createHardware();
            var safe = new SafeHardware(hardware, settings.Bindings, ownsInner: false);
            bool Running(IReadOnlyList<SensorInfo> sensors, FanBinding binding) =>
                sensors.FirstOrDefault(s => s.Type == "Fan" && s.Id == binding.RpmId)?.Value is float rpm &&
                float.IsFinite(rpm) && rpm >= Math.Max(1, binding.MinimumRunningRpm);
            bool StopCoolingSafe(IReadOnlyList<SensorInfo> sensors) =>
                CpuTemperatureReader.Read(sensors) is >= 20 and < 60 &&
                settings.Bindings.Any(b => b.IsVerified && b.Role == FanRole.Cpu) &&
                settings.Bindings.Where(b => b.IsVerified && b.Role == FanRole.Cpu).All(b => Running(sensors, b)) &&
                !otherFanProgramRunning();
            void RecoverStopped(string id)
            {
                var binding = settings.Bindings.FirstOrDefault(b => b.ControlId == id && b.IsVerified && FanRoles.IsCase(b.Role))
                    ?? throw new InvalidOperationException("Корпусная группа для восстановления не найдена");
                var restarted = false;
                try
                {
                    hardware.Set(id, 100);
                    var recovery = Stopwatch.StartNew();
                    do
                    {
                        if (Running(hardware.Read(), binding)) { restarted = true; break; }
                        Thread.Sleep(100);
                    } while (recovery.Elapsed < TimeSpan.FromSeconds(4));
                }
                finally { hardware.Default(id); }
                if (!restarted) throw new InvalidOperationException("Повторный запуск корпусного вентилятора не подтверждён");
            }
            hardware.Read();
            lastHeartbeatAt = clock.Elapsed;
            lastHeartbeatStamp = File.Exists(heartbeat) ? File.GetLastWriteTimeUtc(heartbeat) : DateTime.MinValue;
            File.WriteAllText(ready, "ok");
            try
            {
                long lastSequence = 0;
                void StopProbe(string reason)
                {
                    if (activeProbe == null) return;
                    var id = activeProbe;
                    activeProbe = null;
                    controlBlocked = true;
                    try
                    {
                        hardware.Default(id);
                        touched.Remove(id);
                        pendingRestore.Remove(id);
                        AppLog.Write("Сторож: проба прервана; управление заблокировано (" + reason + ") " + id);
                    }
                    catch (Exception ex)
                    {
                        pendingRestore.Add(id);
                        AppLog.Write("Сторож: ошибка возврата после пробы " + ex);
                    }
                }
                while (true)
                {
                    if (pendingRestore.Count > 0 && clock.Elapsed >= nextRestoreAttempt)
                    {
                        nextRestoreAttempt = clock.Elapsed.Add(TimeSpan.FromSeconds(1));
                        foreach (var id in pendingRestore.ToArray())
                        {
                            try
                            {
                                if (pendingRestart.Contains(id)) RecoverStopped(id); else hardware.Default(id);
                                touched.Remove(id); pendingRestore.Remove(id); pendingRestart.Remove(id);
                            }
                            catch (Exception ex) { AppLog.Write("Сторож: повтор возврата не удался " + id + " " + ex.Message); }
                        }
                    }
                    // The zero-speed phase is owned by the worker, not by UI
                    // timing. It always advances to a bounded full-speed restart.
                    if (activeStop != null && stopPhase == "stopping" && clock.Elapsed >= stopDeadline)
                    {
                        try
                        {
                            hardware.Set(activeStop, 100);
                            stopPhase = "restarting";
                            stopDeadline = clock.Elapsed.Add(TimeSpan.FromSeconds(15));
                        }
                        catch
                        {
                            controlBlocked = true;
                            pendingRestore.Add(activeStop);
                            pendingRestart.Add(activeStop);
                            activeStop = null; stopPhase = null;
                        }
                    }
                    if (activeStop != null && (clock.Elapsed >= stopDeadline || clock.Elapsed >= nextStopSafetyCheck))
                    {
                        nextStopSafetyCheck = clock.Elapsed.Add(TimeSpan.FromSeconds(1));
                        if (clock.Elapsed >= stopDeadline || !StopCoolingSafe(hardware.Read()))
                        {
                            var id = activeStop;
                            controlBlocked = true;
                            activeStop = null; stopPhase = null;
                            pendingRestore.Add(id); pendingRestart.Add(id);
                            try { RecoverStopped(id); touched.Remove(id); pendingRestore.Remove(id); pendingRestart.Remove(id); }
                            catch (Exception ex) { AppLog.Write("Сторож: ошибка завершения проверки остановки " + ex); }
                        }
                    }
                    // An expired lease restores first; it must not start another
                    // potentially slow driver read before attempting rollback.
                    if (activeProbe != null && clock.Elapsed >= probeDeadline)
                        StopProbe("истёк срок пробы");
                    else if (activeProbe != null && clock.Elapsed >= nextProbeSafetyCheck)
                    {
                        nextProbeSafetyCheck = clock.Elapsed.Add(TimeSpan.FromSeconds(1));
                        var temperature = CpuTemperatureReader.Read(hardware.Read());
                        if (clock.Elapsed >= probeDeadline || temperature is not (>= 20 and < 65) ||
                            otherFanProgramRunning())
                            StopProbe("температура, конкурирующая программа или срок пробы");
                    }
                    bool alive;
                    try { using var parent = Process.GetProcessById(parentPid); alive = !parent.HasExited; } catch { alive = false; }
                    var heartbeatStamp = File.Exists(heartbeat) ? File.GetLastWriteTimeUtc(heartbeat) : DateTime.MinValue;
                    if (heartbeatStamp != lastHeartbeatStamp)
                    {
                        lastHeartbeatStamp = heartbeatStamp;
                        lastHeartbeatAt = clock.Elapsed;
                    }
                    if (!alive || clock.Elapsed - lastHeartbeatAt > TimeSpan.FromSeconds(8))
                    {
                        AppLog.Write("Сторож: потеря связи с окном; возврат исходного режима");
                        break;
                    }
                    var requestPath = Path.Combine(directory, "request.json");
                    if (File.Exists(requestPath))
                    {
                        WorkerRequest? request = null;
                        try { request = JsonSerializer.Deserialize<WorkerRequest>(WorkerIpcFiles.ReadText(requestPath)); }
                        catch (IOException) { }
                        if (request != null && request.Sequence > lastSequence)
                        {
                            lastSequence = request.Sequence;
                            WorkerReply reply;
                            try
                            {
                                HardwareSnapshot? snapshot = null;
                                switch (request.Operation)
                                {
                                    case "read":
                                        snapshot = new HardwareSnapshot(hardware.Read().ToArray(), hardware.Channels.ToArray(), stopPhase);
                                        break;
                                    case "set":
                                    {
                                        if (controlBlocked) throw new InvalidOperationException("Управление заблокировано до перезапуска приложения");
                                        if (activeProbe != null || activeStop != null || pendingRestore.Count > 0)
                                            throw new InvalidOperationException("Проба или возврат режима ещё не завершены");
                                        var temperature = CpuTemperatureReader.Read(hardware.Read());
                                        var limit = request.Percent == 0 ? 65 : 80;
                                        if (temperature is not (> 0 and < 120) || temperature >= limit || otherFanProgramRunning())
                                            throw new InvalidOperationException("Нет безопасных показаний для управления");
                                        safe.ValidateSet(request.ControlId, request.Percent);
                                        touched.Add(request.ControlId);
                                        try { safe.Set(request.ControlId, request.Percent); }
                                        catch { controlBlocked = true; pendingRestore.Add(request.ControlId); throw; }
                                        break;
                                    }
                                    case "default":
                                        if (activeProbe != null || activeStop != null) throw new InvalidOperationException("Во время пробы другая запись запрещена");
                                        try
                                        {
                                            if (touched.Contains(request.ControlId)) hardware.Default(request.ControlId);
                                            else safe.Default(request.ControlId);
                                        }
                                        catch
                                        {
                                            controlBlocked = true;
                                            if (touched.Contains(request.ControlId)) pendingRestore.Add(request.ControlId);
                                            throw;
                                        }
                                        touched.Remove(request.ControlId); pendingRestore.Remove(request.ControlId); break;
                                    case "probe-start":
                                    {
                                        if (controlBlocked) throw new InvalidOperationException("Управление заблокировано до перезапуска приложения");
                                        if (activeProbe != null || activeStop != null || touched.Count != 0 || otherFanProgramRunning())
                                            throw new InvalidOperationException("Проба канала запрещена до возврата всех каналов");
                                        var sensors = hardware.Read();
                                        var channel = hardware.Channels.FirstOrDefault(c => c.Id == request.ControlId);
                                        if (channel == null || !FanProbe.MayStart(sensors, channel, request.Percent))
                                            throw new InvalidOperationException("Нет безопасных показаний или возможностей для пробы");
                                        touched.Add(request.ControlId);
                                        try { hardware.Set(request.ControlId, 100); }
                                        catch { controlBlocked = true; pendingRestore.Add(request.ControlId); throw; }
                                        activeProbe = request.ControlId;
                                        probeDeadline = clock.Elapsed.Add(TimeSpan.FromSeconds(20));
                                        nextProbeSafetyCheck = clock.Elapsed.Add(TimeSpan.FromSeconds(1));
                                        break;
                                    }
                                    case "stop-test-start":
                                    {
                                        if (controlBlocked || activeProbe != null || activeStop != null || touched.Count != 0)
                                            throw new InvalidOperationException("Проверка остановки недоступна до возврата всех каналов");
                                        var sensors = hardware.Read();
                                        var binding = settings.Bindings.FirstOrDefault(b => b.ControlId == request.ControlId && b.IsVerified && FanRoles.IsCase(b.Role));
                                        if (request.Percent != 0 || binding == null || otherFanProgramRunning() ||
                                            !StopTestCoordinator.MayStart(binding, settings.Bindings, sensors, hardware.Channels))
                                            throw new InvalidOperationException("Для остановки нужны подтверждённая корпусная группа, работающий CPU-кулер и температура ниже60°C");
                                        touched.Add(request.ControlId);
                                        pendingRestart.Add(request.ControlId);
                                        activeStop = request.ControlId;
                                        stopPhase = "stopping";
                                        stopDeadline = clock.Elapsed.Add(TimeSpan.FromSeconds(5));
                                        nextStopSafetyCheck = clock.Elapsed.Add(TimeSpan.FromSeconds(1));
                                        try { hardware.Set(request.ControlId, 0); }
                                        catch
                                        {
                                            controlBlocked = true;
                                            pendingRestore.Add(request.ControlId);
                                            activeStop = null; stopPhase = null;
                                            throw;
                                        }
                                        break;
                                    }
                                    case "stop-test-end":
                                    {
                                        if (activeStop != request.ControlId || stopPhase != "restarting")
                                            throw new InvalidOperationException("Дождитесь фазы повторного запуска корпусного вентилятора");
                                        var binding = settings.Bindings.First(b => b.ControlId == request.ControlId);
                                        if (!Running(hardware.Read(), binding))
                                            throw new InvalidOperationException("Повторный запуск корпусного вентилятора ещё не подтверждён");
                                        try { hardware.Default(request.ControlId); }
                                        catch
                                        {
                                            controlBlocked = true;
                                            activeStop = null; stopPhase = null;
                                            pendingRestore.Add(request.ControlId);
                                            throw;
                                        }
                                        touched.Remove(request.ControlId); pendingRestart.Remove(request.ControlId);
                                        activeStop = null; stopPhase = null;
                                        break;
                                    }
                                    case "probe-end":
                                        if (activeProbe != request.ControlId) throw new InvalidOperationException("Проба прервана сторожем или не активна");
                                        try { hardware.Default(request.ControlId); }
                                        catch
                                        {
                                            controlBlocked = true;
                                            activeProbe = null;
                                            pendingRestore.Add(request.ControlId);
                                            throw;
                                        }
                                        touched.Remove(request.ControlId);
                                        activeProbe = null;
                                        break;
                                    case "block-control":
                                    {
                                        // Blocking is irreversible for this worker session,
                                        // including after a successful settings reload.
                                        controlBlocked = true;
                                        activeProbe = null;
                                        activeStop = null; stopPhase = null;
                                        var failures = new List<string>();
                                        foreach (var id in touched.ToArray())
                                        {
                                            try
                                            {
                                                if (pendingRestart.Contains(id)) RecoverStopped(id); else hardware.Default(id);
                                                touched.Remove(id); pendingRestore.Remove(id); pendingRestart.Remove(id);
                                            }
                                            catch (Exception ex)
                                            {
                                                pendingRestore.Add(id);
                                                failures.Add(id + ": " + ex.Message);
                                            }
                                        }
                                        AppLog.Write("Сторож: управление заблокировано до перезапуска");
                                        if (failures.Count > 0)
                                            throw new InvalidOperationException("Возврат не подтверждён: " + string.Join("; ", failures));
                                        break;
                                    }
                                    case "reload-bindings":
                                        if (activeProbe != null || activeStop != null || touched.Count != 0)
                                            throw new InvalidOperationException("Сначала верните все каналы в исходный режим");
                                        settings = loadSettings();
                                        safe = new SafeHardware(hardware, settings.Bindings, ownsInner: false);
                                        break;
                                    case "shutdown": break;
                                    default: throw new InvalidOperationException("Неизвестная команда");
                                }
                                reply = new(request.Sequence, true, "", snapshot);
                            }
                            catch (Exception ex)
                            {
                                if (request.Operation == "read" && touched.Count > 0)
                                {
                                    controlBlocked = true;
                                    activeProbe = null;
                                    activeStop = null; stopPhase = null;
                                    pendingRestore.UnionWith(touched);
                                }
                                reply = new(request.Sequence, false, ex.Message);
                                AppLog.Write("Сторож: ошибка команды: " + ex);
                            }
                            var replyPath = Path.Combine(directory, "reply.json");
                            WorkerIpcFiles.WriteAtomic(replyPath, JsonSerializer.Serialize(reply));
                            if (request.Operation == "shutdown") break;
                        }
                    }
                    Thread.Sleep(50);
                }
            }
            finally
            {
                foreach (var id in touched)
                {
                    try
                    {
                        if (pendingRestart.Contains(id)) RecoverStopped(id); else hardware.Default(id);
                        AppLog.Write("Сторож: восстановлен исходный режим " + id);
                    }
                    catch (Exception ex) { AppLog.Write("Сторож: ОШИБКА восстановления " + id + " " + ex); }
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("Сторож: аварийное завершение " + ex);
            if (!File.Exists(ready)) File.WriteAllText(ready, ex.Message);
        }
    }
}
