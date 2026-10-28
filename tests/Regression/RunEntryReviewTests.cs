using System.Collections.Concurrent;
using System.Text.Json;
using FanControlCore;
using FanControlCore.Discovery;
using FanControlCore.Policy;

internal static class RunEntryReviewTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception("Run-entry review: " + message); }
    private static void Until(Func<bool> ready, string message) => Check(SpinWait.SpinUntil(ready, 3000), message);
    private static void Record(FanCore core)
    {
        core.Temperatures.Push(new Dictionary<string, double> { ["cpu"] = 60 });
        Check(core.TryWriteCurve(0, [new(60, 35)], "software", out _), "record plan");
    }
    private static JsonElement Send(FanControlSession session, string request)
    {
        using var json = JsonDocument.Parse(request);
        return session.Handle(json.RootElement);
    }
    private static void Needed(JsonElement receipt, bool expected)
        => Check(receipt.GetProperty("softwareCurveRunNeeded").GetBoolean() == expected, "receipt carries current run-needed fact");

    internal static void RunNeeded()
    {
        var backend = new Backend();
        using var core = new FanCore(backend);
        using var session = new FanControlSession(core);
        Record(core);
        Check(core.SoftwareCurveRunNeeded && session.SoftwareCurveRunNeeded, "recorded plan needs a host run");
        using (var run = new CurveRun(session.RunSoftwareCurveAsync))
        {
            Until(() => backend.Drives > 0, "fixture drives");
            backend.FailRelease = true;
        }
        var changes = new ConcurrentQueue<bool>();
        session.SoftwareCurveRunNeededChanged += changes.Enqueue;
        Needed(Send(session, """{"op":"auto","fan":0}"""), true);
        Check(!core.SoftwarePlanRecorded(0) && core.SoftwareCurveRunNeeded, "failed auto with no run keeps retry needed after removing plan");
        Check(changes.IsEmpty, "true remains true without another event");
        backend.FailRelease = false;
        using (var retry = new CurveRun(core.RunSoftwareCurveAsync))
        {
            Until(() => !core.SoftwareCurveRunNeeded && changes.Count == 1, "retry clears need and emits exactly one event");
            Check(changes.ToArray().SequenceEqual(new[] { false }) && !backend.Manual, "retry reports false after firmware hand-back");
            Thread.Sleep(100);
            Check(!retry.Task.IsCompleted, "run stays active after needed turns false until host stops it");
        }
        session.SoftwareCurveRunNeededChanged -= changes.Enqueue;
        Check(!session.SoftwareCurveRunNeeded, "session query tracks retry completion");

        changes.Clear();
        Record(core);
        using (var run = new CurveRun(core.RunSoftwareCurveAsync))
        {
            Until(() => backend.Manual, "last-plan fixture drives");
            session.SoftwareCurveRunNeededChanged += changes.Enqueue;
            Needed(Send(session, """{"op":"auto","fan":0}"""), false);
            Check(changes.ToArray().SequenceEqual(new[] { false }), "removing last plan during run emits exactly one event");
            Check(!run.Task.IsCompleted, "last-plan removal does not stop the run");
            session.SoftwareCurveRunNeededChanged -= changes.Enqueue;
        }

        // Hold the first notification while another caller makes the next change.
        using var engine = new SoftwareCurveEngine(new Backend());
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var ordered = new ConcurrentQueue<bool>();
        engine.SoftwareCurveRunNeededChanged += value =>
        {
            // A second thread must be able to read while the observer is running.
            Check(Task.Run(() => engine.SoftwareCurveRunNeeded).Wait(1000), "event executes outside engine gate");
            ordered.Enqueue(value);
            if (value) { entered.Set(); Check(release.Wait(3000), "release event observer"); }
        };
        var recording = Task.Run(() => engine.TryRecordPlan(new(0, [new(60, 35)], [new("cpu", () => 60)]), out _));
        try
        {
            Check(entered.Wait(3000), "first event entered");
            var removing = Task.Run(() => engine.TryStop(0, out _));
            Check(removing.Wait(1000) && removing.Result, "next change is possible while observer is held");
        }
        finally { release.Set(); }
        Check(recording.Wait(3000) && recording.Result, "recording notification finishes");
        Check(ordered.ToArray().SequenceEqual(new[] { true, false }), "quick concurrent changes publish in order");

        foreach (var op in new[] { "curve", "setcurve", "set", "boost", "auto" })
        {
            Record(core);
            var request = op switch
            {
                "setcurve" => """{"op":"setcurve","fan":0,"execution":"software","curve":[{"temperatureCelsius":60,"percent":35}]}""",
                "set" => """{"op":"set","fan":0,"percent":70}""",
                _ => JsonSerializer.Serialize(new { op, fan = 0 })
            };
            Needed(Send(session, request), op is "curve" or "setcurve");
        }
        Console.WriteLine("Run-needed fact, retry, change-only ordered notifications outside gate, receipts and host-owned stop passed.");
    }

    internal static void PlanRemoval()
    {
        var backend = new Backend();
        using (var engine = new SoftwareCurveEngine(backend))
        {
            Check(engine.TryRecordPlan(new(0, [new(60, 35)], [new("cpu", () => 60)]), out _), "record undriven plan");
            Check(engine.TryStop(0, out _), "remove undriven plan");
            Check(backend.Releases == 0, "removing undriven plan must not release hardware");
        }
        foreach (var op in new[] { "auto", "set", "boost", "firmware", "restore" })
        {
            backend = new Backend();
            using var core = new FanCore(backend);
            core.Open();
            Record(core);
            Check(op switch
            {
                "auto" => core.TrySetAutomatic(0, out _),
                "set" => core.TrySetDuty(0, 70, out _),
                "boost" => core.TrySetFullSpeed(0, out _),
                "firmware" => core.TryWriteCurve(0, [new(60, 35)], "firmware", out _),
                _ => Restore(core)
            }, "replace/remove undriven plan");
            Check(backend.Releases == (op == "auto" ? 1 : 0), "plan-only removal adds no native release before caller operation");
            Check(!core.SoftwarePlanRecorded(0), "plan removed");
        }
        Console.WriteLine("Undriven plan removal performs no extra hardware release.");
    }

    private static bool Restore(FanCore core) { core.RestoreAll(); return true; }
    private sealed class Backend : IFanBackend
    {
        public string Name => "review-fixture";
        public volatile bool FailRelease, Manual;
        public int Drives, Releases;
        public bool TryOpen(out string? reason) { reason = null; return true; }
        public IReadOnlyList<FanDescription> Describe() => [new(0, "CPU", true, true, FanRoles.Cpu, FirmwareCurve: "xy", SupportsSoftwareCurve: true)];
        public IReadOnlyList<FanState> Read() => [];
        public bool TryDriveDuty(int fan, double percent, out string? reason) { Manual = true; Interlocked.Increment(ref Drives); reason = null; return true; }
        public bool TryWriteCurve(int fan, IReadOnlyList<FanCurvePoint> curve, out string? reason) { reason = null; return true; }
        public bool TrySetDuty(int fan, double percent, out string? reason) { reason = null; return true; }
        public bool TrySetFullSpeed(int fan, out string? reason) { reason = null; return true; }
        public bool TrySetAutomatic(int fan, out string? reason)
        {
            Interlocked.Increment(ref Releases);
            reason = FailRelease ? "injected release failure" : null;
            if (FailRelease) return false;
            Manual = false; return true;
        }
        public void Dispose() { }
    }
}
