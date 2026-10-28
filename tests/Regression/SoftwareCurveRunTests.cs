using System.Collections.Concurrent;
using System.Text.Json;
using FanControlCore;
using FanControlCore.Discovery;
using FanControlCore.Policy;

internal sealed class CurveRun : IDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly bool allowFault;
    public Task Task { get; }
    public CurveRun(Func<CancellationToken, Task> run, bool allowFault = false)
    {
        this.allowFault = allowFault;
        Task = run(stop.Token);
    }
    public void Dispose()
    {
        stop.Cancel();
        if (!SpinWait.SpinUntil(() => Task.IsCompleted, 3000)) throw new Exception("Curve run did not stop");
        if (allowFault) _ = Task.Exception;
        else Task.GetAwaiter().GetResult();
        stop.Dispose();
    }
}

internal static class SoftwareCurveRunTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Until(Func<bool> ready, string message) => Check(SpinWait.SpinUntil(ready, 3000), message);
    private const string Record = """{"op":"setcurve","fan":0,"execution":"software","curve":[{"temperatureCelsius":60,"percent":35}]}""";
    private static JsonElement Send(FanControlSession session, string request)
    {
        using var json = JsonDocument.Parse(request);
        return session.Handle(json.RootElement);
    }
    private static void Receipt(JsonElement receipt, bool recorded, string? execution)
    {
        Check(receipt.GetProperty("ok").GetBoolean(), "curve receipt succeeds");
        Check(receipt.GetProperty("softwarePlanRecorded").GetBoolean() == recorded, "receipt identifies recorded plan");
        Check(receipt.GetProperty("execution").GetString() == execution, "receipt reports actual execution");
        if (recorded) Check(receipt.GetProperty("curve")[0].GetProperty("percent").GetDouble() == 35, "receipt retains plan points");
    }

    internal static void Run()
    {
        {
            var backend = new RunBackend { Available = false };
            using var core = new FanCore(backend);
            using var session = new FanControlSession(core);
            using var run = new CurveRun(session.RunSoftwareCurveAsync);
            Check(!run.Task.IsCompleted && core.ActiveBackendName is null, "run waits while discovery is unavailable");
            backend.Available = true;
            Check(Send(session, """{"op":"describe","refresh":true}""").GetProperty("available").GetBoolean(), "explicit redetection finds backend");
            Send(session, """{"op":"temp","samples":{"cpu":60}}""");
            Send(session, Record);
            Until(() => !backend.Writes.IsEmpty, "existing run serves plans after redetection");
        }
        {
            var backend = new RunBackend();
            using var core = new FanCore(backend);
            using var session = new FanControlSession(core);
            Send(session, """{"op":"temp","samples":{"cpu":60}}""");
            Receipt(Send(session, Record), true, null);
            Thread.Sleep(250);
            Check(backend.Writes.IsEmpty && backend.Releases == 0, "recording drives and releases nothing");
            Check(core.ExecutionOf(0) is null, "recording does not execute");
            using (var run = new CurveRun(session.RunSoftwareCurveAsync))
            {
                Until(() => !backend.Writes.IsEmpty, "run executes recorded plan");
                Receipt(Send(session, """{"op":"curve","fan":0}"""), true, "software");
                try
                {
                    core.RunSoftwareCurveAsync(CancellationToken.None).GetAwaiter().GetResult();
                    throw new Exception("Concurrent run was accepted");
                }
                catch (InvalidOperationException error) { Check(error.Message.Contains("already active"), "clear concurrent-run failure across entry points"); }
            }
            Check(!backend.Manual && backend.Releases > 0, "stop hands fan back before returning");
            Receipt(Send(session, """{"op":"curve","fan":0}"""), true, null);
            var count = backend.Writes.Count;
            Thread.Sleep(250);
            Check(backend.Writes.Count == count, "stopped run performs no more writes");
            using (var run = new CurveRun(session.RunSoftwareCurveAsync))
                Until(() => backend.Writes.Count > count, "new run resumes retained plan");
            core.RestoreAll();
            Check(!core.SoftwarePlanRecorded(0), "RestoreAll removes stopped plan");
        }

        foreach (var action in new[] { "auto", "set", "boost", "restore" })
        {
            var backend = new RunBackend();
            using var core = new FanCore(backend);
            core.Open();
            core.PushTemperatures([new("cpu", 60, DateTime.UtcNow.Ticks)]);
            using var run = new CurveRun(core.RunSoftwareCurveAsync);
            Check(core.TryWriteCurve(0, [new(60, 35)], "software", out _), "plan wakes waiting run");
            Until(() => !backend.Writes.IsEmpty, "run drives before manual action");
            Check(action switch
            {
                "set" => core.TrySetDuty(0, 70, out _),
                "boost" => core.TrySetFullSpeed(0, out _),
                "auto" => core.TrySetAutomatic(0, out _),
                _ => Restore(core)
            }, "manual action succeeds");
            Check(!core.SoftwarePlanRecorded(0) && backend.Releases > 0, "manual action removes and releases curve");
            Check(action switch { "set" => backend.Manual && backend.Duty == 70, "boost" => backend.Manual && backend.Duty == 100, _ => !backend.Manual }, "manual action keeps its meaning");
            var count = backend.Writes.Count;
            Thread.Sleep(250);
            Check(backend.Writes.Count == count, "removed plan cannot write again");
        }

        {
            var backend = new RunBackend();
            var core = new FanCore(backend);
            using var session = new FanControlSession(core);
            Send(session, """{"op":"temp","samples":{"cpu":60}}""");
            Send(session, Record);
            var run = session.RunSoftwareCurveAsync(CancellationToken.None);
            Until(() => !backend.Writes.IsEmpty, "dispose fixture drives");
            session.Dispose();
            Check(run.IsCompletedSuccessfully && !backend.Manual, "dispose ends run and hands back before returning");
            Check(!core.SoftwarePlanRecorded(0), "dispose removes plans");
        }

        {
            var root = Path.Combine(Path.GetTempPath(), "fancore-run-journal-" + Guid.NewGuid().ToString("N"));
            var journal = Path.Combine(root, "temporary-takeovers.json");
            try
            {
                var backend = new RunBackend { BeforeDrive = () =>
                {
                    using var saved = JsonDocument.Parse(File.ReadAllText(journal));
                    Check(saved.RootElement.GetProperty("Fans").GetArrayLength() == 1, "takeover is durable before first drive");
                } };
                using var core = new FanCore(backend, new(StateDirectory: root, MachineIdentity: () => "run-test"));
                core.Open();
                core.PushTemperatures([new("cpu", 60, DateTime.UtcNow.Ticks)]);
                Check(core.TryWriteCurve(0, [new(60, 35)], "software", out _), "record durable-host plan");
                Check(!File.Exists(journal), "recording does not claim temporary hardware ownership");
                using var run = new CurveRun(core.RunSoftwareCurveAsync);
                Until(() => !backend.Writes.IsEmpty, "durable preparation precedes software drive");
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    foreach (var file in Directory.EnumerateFiles(root)) File.Delete(file);
                    Directory.Delete(root);
                }
            }
        }

        {
            var backend = new RunBackend();
            using var engine = new SoftwareCurveEngine(backend, TimeSpan.FromMilliseconds(20));
            var iterations = 0;
            engine.BeforeIteration = () => Interlocked.Increment(ref iterations);
            using var run = new CurveRun(engine.RunAsync);
            Thread.Sleep(100);
            Check(iterations == 0, "empty run waits without ticking");
            Check(engine.TryRecordPlan(new(0, [new(60, 20)], [new("cpu", () => 60)], PercentPerSecond: 50), out _), "record initial speed");
            Until(() => backend.Writes.TryPeek(out _), "initial speed applied");
            Check(engine.TryRecordPlan(new(0, [new(60, 80)], [new("cpu", () => 60)], PercentPerSecond: 50), out _), "replace running plan");
            Until(() => backend.Writes.Count >= 2, "replacement drives");
            Check(backend.Writes.ToArray()[1] == 21, "replacement preserves applied speed and acceleration");
            Check(engine.TryStop(0, out _), "remove final plan");
            var before = iterations;
            Thread.Sleep(100);
            Check(iterations <= before + 1, "run returns to idle without polling");
        }

        {
            var backend = new RunBackend { RequiresReassert = true };
            using var engine = new SoftwareCurveEngine(backend, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(30));
            Check(engine.TryRecordPlan(new(0, [new(60, 35)], [new("cpu", () => 60)]), out _), "record reassert plan");
            using var run = new CurveRun(engine.RunAsync);
            Until(() => backend.Writes.Count >= 2, "host-owned run reasserts unchanged duty");
            Check(backend.Writes.All(value => value == 35), "reassert keeps target");
        }

        {
            var backend = new RunBackend();
            using var core = new FanCore(backend);
            core.Open();
            using var input = new MessageInput();
            using var output = new StringWriter();
            var server = Task.Run(() => StdioHost.Run(core, input, output));
            input.Push("""{"op":"temp","notification":true,"samples":{"cpu":60}}""");
            input.Push(Record);
            Until(() => !backend.Writes.IsEmpty, "stdio host owns execution of setcurve");
            input.Push("""{"op":"read"}""");
            input.End();
            Check(server.Wait(3000), "stdio host exits");
            using var receipt = JsonDocument.Parse(output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)[1]);
            Check(receipt.RootElement.GetProperty("fans")[0].GetProperty("dutyPercent").GetDouble() == 35, "stdio read sees computed speed");
            Check(!backend.Manual && !core.SoftwarePlanRecorded(0), "stdio host stops run and restores on EOF");
        }
        Console.WriteLine("Software curve run entry: record, idle, resume, replacement, concurrent rejection, manual removal, dispose and stdio passed.");
    }

    private static bool Restore(FanCore core) { core.RestoreAll(); return true; }

    private sealed class RunBackend : IFanBackend
    {
        public string Name => "run-fixture";
        public ConcurrentQueue<double> Writes { get; } = new();
        public volatile bool Manual;
        public double Duty;
        public int Releases;
        public bool RequiresReassert;
        public bool Available = true;
        public Action? BeforeDrive;
        public bool TryOpen(out string? reason) { reason = Available ? null : "fixture unavailable"; return Available; }
        public IReadOnlyList<FanDescription> Describe() => [new(0, "CPU", true, true, FanRoles.Cpu, SupportsSoftwareCurve: true, RequiresPeriodicReassert: RequiresReassert)];
        public IReadOnlyList<FanState> Read() => [new(0, "CPU", null, Duty, null, true, true, !Manual)];
        public bool TryDriveDuty(int fan, double percent, out string? reason) { BeforeDrive?.Invoke(); TrySetDuty(fan, percent, out reason); Writes.Enqueue(percent); return true; }
        public bool TrySetDuty(int fan, double percent, out string? reason) { Duty = percent; Manual = true; reason = null; return true; }
        public bool TrySetFullSpeed(int fan, out string? reason) => TrySetDuty(fan, 100, out reason);
        public bool TrySetAutomatic(int fan, out string? reason) { Manual = false; Interlocked.Increment(ref Releases); reason = null; return true; }
        public void Dispose() { }
    }
}
