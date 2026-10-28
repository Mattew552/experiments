using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace FanGuardian;

internal static class StopTestTests
{
    private static FanBinding Binding(string id, FanRole role) => new()
    {
        ControlId = id, RpmId = id + "/rpm", Role = role,
        MappingVerified = true, ReturnVerified = true, SpeedVerified = true,
        MinimumPercent = 40, MinimumRunningRpm = 250
    };

    public static void Run()
    {
        RunAsync().GetAwaiter().GetResult();
        WorkerIntegration();
    }

    private static async Task RunAsync()
    {
        var cpu = Binding("cpu", FanRole.Cpu);
        var caseFan = Binding("case", FanRole.Case);
        using (var fake = new Fake(cpu, caseFan))
        {
            var result = await Coordinator(fake).RunAsync(caseFan, [cpu, caseFan], CancellationToken.None);
            Check(result.Verified && result.Error == null && fake.Starts == 1 && fake.Ends == 1 && fake.Phase == null,
                "Корпусная остановка/повторный запуск не подтверждены");
        }
        using (var fake = new Fake(cpu, caseFan))
        {
            var result = await Coordinator(fake).RunAsync(cpu, [cpu, caseFan], CancellationToken.None);
            Check(!result.Verified && result.Error != null && fake.Starts == 0, "CPU отправлен в пробу остановки");
            result = await Coordinator(fake).RunAsync(caseFan, [caseFan], CancellationToken.None);
            Check(!result.Verified && result.Error != null && fake.Starts == 0, "Остановка разрешена без CPU тахометра");
            var shared = CloneRpm(caseFan, cpu.RpmId);
            Check(!StopTestCoordinator.MayStart(shared, [cpu, shared], fake.Read(), fake.Channels),
                "Корпусная группа использовала CPU тахометр");
        }
        using (var fake = new Fake(cpu, caseFan) { FailRestart = true })
        {
            var result = await Coordinator(fake).RunAsync(caseFan, [cpu, caseFan], CancellationToken.None);
            Check(!result.Verified && result.Error != null && fake.Ends == 0 && fake.RestartAttempted,
                "Нераскрутившийся вентилятор получил разрешение остановки");
        }
        using (var cancel = new CancellationTokenSource())
        using (var fake = new Fake(cpu, caseFan))
        {
            var coordinator = new StopTestCoordinator(fake, fake.Begin, fake.End, () => fake.Phase,
                (_, _) => { fake.Advance(); if (fake.Phase == "stopping") cancel.Cancel(); return Task.CompletedTask; });
            var result = await coordinator.RunAsync(caseFan, [cpu, caseFan], cancel.Token);
            Check(!result.Verified && result.Cancelled && result.Error == null && fake.Ends == 1 && fake.Phase == null,
                "Отмена обошла повторный запуск и восстановление");
        }
        using (var fake = new Fake(cpu, caseFan) { CannotStop = true })
        {
            var result = await Coordinator(fake).RunAsync(caseFan, [cpu, caseFan], CancellationToken.None);
            Check(!result.Verified && result.Error == null && result.Note != null && fake.Ends == 1,
                "Неподдержанная остановка ошибочно выдала разрешение или ошибку восстановления");
        }
        var gpu = new ControlChannel("case", "gpu-device", "GPU", HardwareOrigin.Gpu, RestoreStrategy.RestoreOriginal, true);
        using (var fake = new Fake(cpu, caseFan))
            Check(!StopTestCoordinator.MayStart(caseFan, [cpu, caseFan], fake.Read(), [gpu]), "GPU разрешён для остановки");
    }

    private static FanBinding CloneRpm(FanBinding value, string rpm) => new()
    {
        ControlId = value.ControlId, RpmId = rpm, Role = value.Role,
        MappingVerified = true, ReturnVerified = true, SpeedVerified = true,
        MinimumPercent = value.MinimumPercent, MinimumRunningRpm = value.MinimumRunningRpm
    };

    private static StopTestCoordinator Coordinator(Fake fake) => new(fake, fake.Begin, fake.End, () => fake.Phase,
        (_, _) => { fake.Advance(); return Task.CompletedTask; });

    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    private static void WorkerIntegration()
    {
        var cpu = Binding("cpu", FanRole.Cpu);
        var caseFan = Binding("case", FanRole.Case);
        using var session = new WorkerSession(cpu, caseFan);
        session.Send("stop-test-start", cpu.ControlId, success: false);
        session.Send("stop-test-start", "gpu", success: false);
        Check(session.Hardware.Writes.IsEmpty, "Отказ CPU/GPU отправил команду аппаратуре");
        session.Send("stop-test-start", caseFan.ControlId);
        Check(session.Send("read").Snapshot?.StopTestPhase == "stopping", "Worker не начал ограниченную остановку");
        session.Send("set", caseFan.ControlId, 70, success: false);
        session.Send("default", caseFan.ControlId, success: false);
        session.Send("stop-test-end", caseFan.ControlId, success: false);
        var wait = Stopwatch.StartNew();
        while (session.Send("read").Snapshot?.StopTestPhase != "restarting")
        {
            if (wait.Elapsed > TimeSpan.FromSeconds(7)) throw new Exception("Worker не запустил вентилятор по собственному таймеру");
            Thread.Sleep(100);
        }
        session.Send("stop-test-end", caseFan.ControlId);
        Check(session.Hardware.Writes.SequenceEqual(["set:0", "set:100", "default"]), "Неверная последовательность stop/restart/default");
        session.Send("stop-test-start", caseFan.ControlId);
        session.Shutdown();
        Check(session.Hardware.Writes.TakeLast(3).SequenceEqual(["set:0", "set:100", "default"]),
            "Shutdown остановленной группы пропустил запуск100% перед Default");
    }

    private sealed class WorkerFake(FanBinding cpu, FanBinding caseFan) : IFanHardware
    {
        private int duty = 50;
        public ConcurrentQueue<string> Writes { get; } = new();
        public IReadOnlyList<ControlChannel> Channels =>
        [
            new(caseFan.ControlId, "board", "Case", HardwareOrigin.Motherboard, RestoreStrategy.RestoreOriginal, true),
            new(cpu.ControlId, "board", "CPU", HardwareOrigin.Motherboard, RestoreStrategy.RestoreOriginal, true),
            new("gpu", "gpu-device", "GPU", HardwareOrigin.Gpu, RestoreStrategy.RestoreOriginal, true)
        ];
        public IReadOnlyList<SensorInfo> Read() =>
        [
            new("/intelcpu/0/temperature/0", "CPU Package", "CPU", "Temperature", 42),
            new(cpu.RpmId, "CPU", "Board", "Fan", 1000),
            new(caseFan.RpmId, "Case", "Board", "Fan", duty * 20)
        ];
        public void Set(string id, int percent)
        {
            if (id != caseFan.ControlId) throw new Exception("CPU/GPU reached raw stop backend");
            duty = percent; Writes.Enqueue("set:" + percent);
        }
        public void Default(string id)
        {
            if (id != caseFan.ControlId) throw new Exception("CPU/GPU reached raw stop restore");
            duty = 50; Writes.Enqueue("default");
        }
        public void Dispose() { }
    }

    private sealed class WorkerSession : IDisposable
    {
        private readonly string root = Path.GetFullPath(Environment.GetEnvironmentVariable("FANGUARDIAN_TEST_ROOT") ?? Path.GetTempPath());
        private readonly string directory;
        private readonly string heartbeat;
        private readonly Task host;
        private long sequence;
        private bool stopped;
        public WorkerFake Hardware { get; }

        public WorkerSession(FanBinding cpu, FanBinding caseFan)
        {
            Directory.CreateDirectory(root);
            directory = Path.Combine(root, "FanGuardian-stop-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            heartbeat = Path.Combine(directory, "heartbeat");
            File.WriteAllText(heartbeat, "start");
            Hardware = new WorkerFake(cpu, caseFan);
            host = Task.Run(() => WorkerHost.Run(Environment.ProcessId, heartbeat, directory,
                () => Hardware, () => new Settings { Bindings = [cpu, caseFan] }, () => false));
            Wait(() => File.Exists(Path.Combine(directory, "ready")));
        }

        public WorkerReply Send(string operation, string id = "", int percent = 0, bool success = true)
        {
            File.WriteAllText(heartbeat, Guid.NewGuid().ToString("N"));
            var number = ++sequence;
            WorkerIpcFiles.WriteAtomic(Path.Combine(directory, "request.json"), JsonSerializer.Serialize(new WorkerRequest(number, operation, id, percent)));
            WorkerReply? reply = null;
            Wait(() =>
            {
                var path = Path.Combine(directory, "reply.json");
                if (File.Exists(path)) reply = JsonSerializer.Deserialize<WorkerReply>(WorkerIpcFiles.ReadText(path));
                return reply?.Sequence == number;
            });
            if (reply!.Success != success) throw new Exception($"Stop worker {operation}: {reply.Error}");
            return reply;
        }

        public void Shutdown()
        {
            Send("shutdown");
            if (!host.Wait(TimeSpan.FromSeconds(5))) throw new Exception("Stop worker shutdown timeout");
            stopped = true;
        }
        public void Dispose()
        {
            if (!stopped) { try { Shutdown(); } catch { if (!host.IsCompleted) return; } }
            var resolved = Path.GetFullPath(directory);
            if (host.IsCompleted && resolved.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(resolved).StartsWith("FanGuardian-stop-test-", StringComparison.Ordinal))
                Directory.Delete(resolved, recursive: true);
        }
        private static void Wait(Func<bool> condition)
        {
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(5))
            {
                if (condition()) return;
                Thread.Sleep(10);
            }
            throw new Exception("Stop worker IPC timeout");
        }
    }

    private sealed class Fake(FanBinding cpu, FanBinding caseFan) : IFanHardware
    {
        public string? Phase { get; private set; }
        public int Starts { get; private set; }
        public int Ends { get; private set; }
        public bool RestartAttempted { get; private set; }
        public bool FailRestart { get; init; }
        public bool CannotStop { get; init; }
        private int ticks;
        public IReadOnlyList<ControlChannel> Channels =>
            [new(caseFan.ControlId, "board", "Case", HardwareOrigin.Motherboard, RestoreStrategy.RestoreOriginal, true)];
        public IReadOnlyList<SensorInfo> Read() =>
        [
            new("/intelcpu/0/temperature/0", "CPU Package", "CPU", "Temperature", 42),
            new(cpu.RpmId, "CPU", "Board", "Fan", 1000),
            new(caseFan.RpmId, "Case", "Board", "Fan", Phase == "stopping" ? (CannotStop ? 800 : 0) :
                Phase == "restarting" ? (FailRestart ? 0 : 1400) : 800)
        ];
        public void Advance()
        {
            if (Phase == null) return;
            ticks++;
            if (ticks >= 5) { Phase = "restarting"; RestartAttempted = true; }
            if (ticks >= 40 && FailRestart) Phase = null;
        }
        public void Begin(string id)
        {
            if (id != caseFan.ControlId) throw new Exception("Non-case output reached stop backend");
            Starts++; ticks = 0; Phase = "stopping";
        }
        public void End(string id)
        {
            if (id != caseFan.ControlId || Phase != "restarting" || FailRestart)
                throw new Exception("Default requested before successful restart");
            Ends++; Phase = null;
        }
        public void Set(string id, int percent) => throw new Exception("Raw stop writes forbidden in coordinator");
        public void Default(string id) => throw new Exception("Raw default forbidden in coordinator");
        public void Dispose() { }
    }
}
