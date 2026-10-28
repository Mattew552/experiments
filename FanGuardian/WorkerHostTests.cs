using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace FanGuardian;

internal static class WorkerHostTests
{
    private const string ControlId = "/lpc/nct6797d/0/control/4";
    private const string GpuId = "/lpc/spoofed-gpu/control/0";

    private sealed class FakeHardware : IFanHardware
    {
        private int duty = 50;
        public ConcurrentQueue<string> Writes { get; } = new();
        public volatile bool ThrowOnNextSet;
        public volatile bool ThrowOnNextDefault;
        public bool Disposed { get; private set; }
        public IReadOnlyList<ControlChannel> Channels { get; } =
        [
            new(ControlId, "/lpc/nct6797d/0", "Case fan", HardwareOrigin.Motherboard, RestoreStrategy.RestoreOriginal, true),
            new(GpuId, "gpu-device", "GPU", HardwareOrigin.Gpu, RestoreStrategy.RestoreOriginal, true)
        ];
        public IReadOnlyList<SensorInfo> Read() =>
        [
            new("/amdcpu/0/temperature/2", "Core (Tctl/Tdie)", "AMD Ryzen", "Temperature", 45),
            new(ControlId, "Case fan", "Nuvoton", "Control", duty, "/lpc/nct6797d/0", HardwareOrigin.Motherboard),
            new("/lpc/nct6797d/0/fan/4", "Fan #5", "Nuvoton", "Fan", duty * 20, "/lpc/nct6797d/0", HardwareOrigin.Motherboard),
            new(GpuId, "GPU", "Graphics", "Control", 50, "gpu-device", HardwareOrigin.Gpu)
        ];
        public void Set(string id, int percent)
        {
            if (id != ControlId) throw new Exception("A GPU/unknown output reached the backend");
            Writes.Enqueue("set:" + percent);
            duty = percent;
            if (ThrowOnNextSet)
            {
                ThrowOnNextSet = false;
                throw new IOException("Injected failure after hardware accepted duty");
            }
        }
        public void Default(string id)
        {
            if (id != ControlId) throw new Exception("A GPU/unknown restore reached the backend");
            Writes.Enqueue("default");
            if (ThrowOnNextDefault)
            {
                ThrowOnNextDefault = false;
                throw new IOException("Injected restore failure");
            }
            duty = 50;
        }
        public void Dispose() => Disposed = true;
    }

    private sealed class Session : IDisposable
    {
        private readonly string tempRoot = Path.GetFullPath(Environment.GetEnvironmentVariable("FANGUARDIAN_TEST_ROOT") ?? Path.GetTempPath());
        private readonly string directory;
        private readonly string heartbeat;
        private readonly Task host;
        private long sequence;
        private bool stopped;
        public Session(FakeHardware hardware, Func<Settings> settings)
        {
            Directory.CreateDirectory(tempRoot);
            directory = Path.Combine(tempRoot, "FanGuardian-worker-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            heartbeat = Path.Combine(directory, "heartbeat");
            File.WriteAllText(heartbeat, "starting");
            host = Task.Run(() => WorkerHost.Run(Environment.ProcessId, heartbeat, directory,
                () => hardware, settings, () => false));
            WaitUntil(() => File.Exists(Path.Combine(directory, "ready")), "Worker test did not start");
            if (File.ReadAllText(Path.Combine(directory, "ready")) != "ok") throw new Exception("Worker test startup failed");
        }

        public WorkerReply Send(string operation, string id = "", int percent = 0, bool expectSuccess = true)
        {
            File.WriteAllText(heartbeat, Guid.NewGuid().ToString("N"));
            var number = ++sequence;
            var request = Path.Combine(directory, "request.json");
            WorkerIpcFiles.WriteAtomic(request, JsonSerializer.Serialize(new WorkerRequest(number, operation, id, percent)));
            WorkerReply? reply = null;
            WaitUntil(() =>
            {
                try
                {
                    var path = Path.Combine(directory, "reply.json");
                    if (File.Exists(path)) reply = JsonSerializer.Deserialize<WorkerReply>(WorkerIpcFiles.ReadText(path));
                }
                catch (IOException) { }
                return reply?.Sequence == number;
            }, "Worker test reply timeout");
            if (reply!.Success != expectSuccess)
                throw new Exception($"Worker {operation}: expected success={expectSuccess}, actual={reply.Success}: {reply.Error}");
            return reply;
        }

        public void Shutdown()
        {
            Send("shutdown");
            if (!host.Wait(TimeSpan.FromSeconds(3))) throw new Exception("Worker test did not shut down");
            stopped = true;
        }

        public void Dispose()
        {
            if (!stopped)
            {
                try { Shutdown(); }
                catch { if (!host.IsCompleted) return; }
            }
            // Delete only the unique directory created by this test, after the
            // worker has exited. Never derive recursive deletion from IPC data.
            var resolved = Path.GetFullPath(directory);
            var rootPrefix = tempRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (host.IsCompleted && resolved.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(resolved).StartsWith("FanGuardian-worker-test-", StringComparison.Ordinal))
                Directory.Delete(resolved, recursive: true);
        }
    }

    public static void Run()
    {
        static Settings SettingsForTest() => new()
        {
            Bindings =
            [
                new() { ControlId = ControlId, RpmId = "/lpc/nct6797d/0/fan/4", Role = FanRole.Case,
                    MinimumPercent = 40, MappingVerified = true, ReturnVerified = true, SpeedVerified = true },
                new() { ControlId = GpuId, Role = FanRole.Case, MappingVerified = true, ReturnVerified = true, SpeedVerified = true }
            ]
        };
        var fake = new FakeHardware();
        var loads = 0;
        using (var session = new Session(fake, () => { Interlocked.Increment(ref loads); return SettingsForTest(); }))
        {
            var snapshot = session.Send("read").Snapshot;
            if (snapshot is null || snapshot.Sensors.Length != 4 || snapshot.Channels.Length != 2)
                throw new Exception("Worker lost read snapshot metadata");
            session.Send("set", GpuId, 80, expectSuccess: false);
            session.Send("probe-start", GpuId, 100, expectSuccess: false);
            if (!fake.Writes.IsEmpty) throw new Exception("GPU command reached the fake backend");

            session.Send("set", ControlId, 70);
            session.Send("reload-bindings", expectSuccess: false);
            session.Send("default", ControlId);
            session.Send("probe-start", ControlId, 100);
            session.Send("probe-end", ControlId);
            session.Send("reload-bindings");
            if (loads != 2) throw new Exception("Settings reload happened while a control was touched");

            session.Send("set", ControlId, 75);
            var defaultsBeforeBlock = fake.Writes.Count(w => w == "default");
            fake.ThrowOnNextDefault = true;
            session.Send("block-control", expectSuccess: false);
            WaitUntil(() => fake.Writes.Count(w => w == "default") >= defaultsBeforeBlock + 2,
                "Failed block rollback was not retried");
            if (fake.Writes.Last() != "default") throw new Exception("Session block did not attempt rollback");
            session.Send("set", ControlId, 80, expectSuccess: false);
            session.Send("probe-start", ControlId, 100, expectSuccess: false);
            session.Send("read");
            session.Send("reload-bindings");
            session.Send("set", ControlId, 80, expectSuccess: false);
            session.Send("default", ControlId);
            session.Shutdown();
        }
        if (!fake.Disposed) throw new Exception("Worker did not dispose its backend");

        var failAfterWrite = new FakeHardware { ThrowOnNextSet = true };
        using (var session = new Session(failAfterWrite, SettingsForTest))
        {
            session.Send("set", ControlId, 70, expectSuccess: false);
            WaitUntil(() => failAfterWrite.Writes.Contains("default"), "Failed write was not rolled back");
            session.Send("set", ControlId, 80, expectSuccess: false);
            session.Send("reload-bindings");
            session.Send("set", ControlId, 80, expectSuccess: false);
            session.Shutdown();
        }

        var shutdownFake = new FakeHardware();
        using (var session = new Session(shutdownFake, SettingsForTest))
        {
            session.Send("set", ControlId, 70);
            session.Shutdown();
        }
        if (!shutdownFake.Writes.SequenceEqual(new[] { "set:70", "default" }))
            throw new Exception("Worker shutdown did not restore the touched control");
    }

    private static void WaitUntil(Func<bool> condition, string error)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(3))
        {
            if (condition()) return;
            Thread.Sleep(10);
        }
        throw new Exception(error);
    }
}
