using System.Diagnostics;
using System.Text.Json;

namespace FanGuardian;

internal sealed record WorkerRequest(long Sequence, string Operation, string ControlId, int Percent);
internal sealed record WorkerReply(long Sequence, bool Success, string Error);

// The worker is the sole owner of fan writes. It retains LHM's original register
// snapshot across commands, including when the UI process terminates unexpectedly.
internal sealed class WorkerHardware : IFanHardware
{
    private readonly LhmHardware reader = new();
    private readonly string directory;
    private readonly object gate = new();
    private readonly Process worker;
    private long sequence;
    private bool disposed;

    public WorkerHardware(string heartbeat)
    {
        directory = Path.Combine(Settings.DirectoryPath, "worker-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(heartbeat, DateTimeOffset.UtcNow.ToString("O"));
            worker = Program.StartWorker(Environment.ProcessId, heartbeat, directory);
            var ready = Path.Combine(directory, "ready");
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!File.Exists(ready) && DateTime.UtcNow < deadline) Thread.Sleep(100);
            if (!File.Exists(ready)) throw new InvalidOperationException("Сторож управления не запустился; запись запрещена");
            var status = File.ReadAllText(ready);
            if (status != "ok") throw new InvalidOperationException("Сторож управления: " + status);
        }
        catch { reader.Dispose(); throw; }
    }

    public IReadOnlyList<SensorInfo> Read() => reader.Read();
    public void Set(string controlId, int percent) => Send("set", controlId, percent);
    public void Default(string controlId) => Send("default", controlId, 0);

    private void Send(string operation, string id, int percent)
    {
        lock (gate)
        {
            if (disposed) throw new ObjectDisposedException(nameof(WorkerHardware));
            var number = ++sequence;
            var request = new WorkerRequest(number, operation, id, percent);
            var requestPath = Path.Combine(directory, "request.json");
            var tempPath = requestPath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(request));
            File.Move(tempPath, requestPath, true);
            var replyPath = Path.Combine(directory, "reply.json");
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(replyPath))
                {
                    WorkerReply? reply = null;
                    try { reply = JsonSerializer.Deserialize<WorkerReply>(File.ReadAllText(replyPath)); }
                    catch (IOException) { }
                    if (reply?.Sequence == number)
                    {
                        if (!reply.Success) throw new InvalidOperationException("Сторож: " + reply.Error);
                        return;
                    }
                }
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
        reader.Dispose();
    }
}

internal static class WorkerHost
{
    public static void Run(int parentPid, string heartbeat, string directory)
    {
        var ready = Path.Combine(directory, "ready");
        var touched = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var settings = Settings.Load();
            var hardware = new LhmHardware();
            using var safe = new SafeHardware(hardware, settings.Bindings);
            safe.Read();
            File.WriteAllText(ready, "ok");
            try
            {
                long lastSequence = 0;
                while (true)
                {
                    bool alive;
                    try { alive = !Process.GetProcessById(parentPid).HasExited; } catch { alive = false; }
                    var stale = !File.Exists(heartbeat) || DateTime.UtcNow - File.GetLastWriteTimeUtc(heartbeat) > TimeSpan.FromSeconds(8);
                    if (!alive || stale)
                    {
                        AppLog.Write("Сторож: потеря связи с окном; возврат исходного режима");
                        break;
                    }
                    var requestPath = Path.Combine(directory, "request.json");
                    if (File.Exists(requestPath))
                    {
                        WorkerRequest? request = null;
                        try { request = JsonSerializer.Deserialize<WorkerRequest>(File.ReadAllText(requestPath)); }
                        catch (IOException) { }
                        if (request != null && request.Sequence > lastSequence)
                        {
                            lastSequence = request.Sequence;
                            WorkerReply reply;
                            try
                            {
                                switch (request.Operation)
                                {
                                    case "set": touched.Add(request.ControlId); safe.Set(request.ControlId, request.Percent); break;
                                    case "default": safe.Default(request.ControlId); touched.Remove(request.ControlId); break;
                                    case "shutdown": break;
                                    default: throw new InvalidOperationException("Неизвестная команда");
                                }
                                reply = new(request.Sequence, true, "");
                            }
                            catch (Exception ex) { reply = new(request.Sequence, false, ex.Message); AppLog.Write("Сторож: ошибка команды: " + ex); }
                            var replyPath = Path.Combine(directory, "reply.json");
                            var tempPath = replyPath + ".tmp";
                            File.WriteAllText(tempPath, JsonSerializer.Serialize(reply));
                            File.Move(tempPath, replyPath, true);
                            if (request.Operation == "shutdown") break;
                        }
                    }
                    Thread.Sleep(100);
                }
            }
            finally
            {
                foreach (var id in touched)
                {
                    try { safe.Default(id); AppLog.Write("Сторож: восстановлен исходный режим " + id); }
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
