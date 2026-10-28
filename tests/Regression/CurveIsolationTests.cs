using FanControlCore;
using FanControlCore.Policy;
using System.Collections.Concurrent;

internal static class CurveIsolationTests
{
    private static readonly FanCurvePoint[] Curve = [new(30, 40), new(90, 90)];
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Until(Func<bool> ready, string message) => Check(SpinWait.SpinUntil(ready, 3000), message);

    public static void Run()
    {
        foreach (var fault in new[] { "tick", "release", "loop" })
        {
            using var backend = new FaultBackend();
            using var engine = new SoftwareCurveEngine(backend, TimeSpan.FromMilliseconds(5));
            var notices = new ConcurrentQueue<string>();
            engine.DiagnosticSink = notices.Enqueue;
            Check(engine.TryRecordPlan(new(0, Curve, [new("cpu", () => 60)]), out _), "starts fan zero");
            Check(engine.TryRecordPlan(new(1, Curve, [new("cpu", () => 60)]), out _), "starts fan one");
            using var run = new CurveRun(engine.RunAsync, allowFault: fault == "loop");
            Until(() => backend.Writes[0] > 0 && backend.Writes[1] > 0, "both fans under software control");
            if (fault == "loop")
            {
                engine.BeforeIteration = () =>
                {
                    engine.BeforeIteration = null;
                    throw new ApplicationException("injected loop body");
                };
            }
            else
            {
                backend.ThrowRelease = fault == "release" ? 1 : 0;
                backend.ThrowTick = true;
                // Change the temperature target so the next tick writes.
                Check(engine.TryRecordPlan(new(0, Curve, [new("cpu", () => 85)], PercentPerSecond: 10000), out _), "replace target");
            }
            Until(() => !engine.HasRuntime(0), "fault hands affected fan back");
            if (fault == "loop") Until(() => !engine.HasRuntime(1), "loop fault hands every fan back");
            else Check(engine.IsRunning(1), "unaffected fan stays served");
            if (fault == "loop")
            {
                Until(() => run.Task.IsCompleted, "faulted run completes");
                Check(run.Task.IsFaulted, "loop fault is visible to its host");
                Check(engine.HasPlan(0) && engine.HasPlan(1), "fault retains plans");
            }
            Check(!backend.Manual[0], "firmware owns affected fan");
            Until(() => notices.Any(message => message.Contains(fault == "loop" ? "loop body" : "injected tick")), "failure notified");
            Check(engine.Diagnostics.Any(message => message.Contains("injected")), "failure visible in diagnostics");
            if (fault == "release")
            {
                Until(() => notices.Any(message => message.Contains("injected release")), "release failure notified");
                Check(notices.Count(message => message.Contains("injected release")) == 1, "release exception recorded once");
                Check(backend.Releases[0] >= 2, "release failure retries on existing timer");
            }
            backend.ThrowTick = false;
            Check(engine.TryRecordPlan(new(0, Curve, [new("cpu", () => 60)]), out _), "next explicit request works");
            if (fault == "loop")
            {
                using var resumed = new CurveRun(engine.RunAsync);
                Until(() => backend.Manual[0], "new run drives again");
            }
            else Until(() => backend.Manual[0], "new curve drives again");
        }

        using (var backend = new FaultBackend { ThrowTick = true, ThrowRelease = int.MaxValue })
        using (var engine = new SoftwareCurveEngine(backend, TimeSpan.FromMilliseconds(5)))
        {
            engine.DiagnosticSink = _ => throw new ApplicationException("broken observer");
            Check(engine.TryRecordPlan(new(0, Curve, [new("cpu", () => 60)]), out _), "pending release fixture");
            using var run = new CurveRun(engine.RunAsync);
            Until(() => engine.Diagnostics.Any(message => message.Contains("broken observer")), "observer exception contained and visible");
            Check(!engine.TryRecordPlan(new(0, Curve, [new("cpu", () => 60)]), out var reason)
                && reason!.Contains("交还"), "pending release rejects takeover with reason");
            Check(engine.Diagnostics.Count(message => message.Contains("injected release")) == 1, "repeated release failures not duplicated");
            backend.ThrowRelease = 0;
            Until(() => !engine.HasRuntime(0), "restoration completes after backend recovers");
        }
        using (var backend = new FaultBackend { ThrowTick = true })
        using (var engine = new SoftwareCurveEngine(backend, TimeSpan.FromMilliseconds(5)))
        using (var releaseObserver = new ManualResetEventSlim())
        {
            engine.DiagnosticSink = _ => releaseObserver.Wait();
            try
            {
                Check(engine.TryRecordPlan(new(0, Curve, [new("cpu", () => 60)]), out _), "blocked observer fixture starts");
                using var run = new CurveRun(engine.RunAsync);
                Until(() => !engine.HasRuntime(0), "blocked observer cannot delay firmware release");
            }
            finally { releaseObserver.Set(); }
        }
        Console.WriteLine("Curve isolation regression checks passed (tick, release, loop, observer, retry).");
    }

    private sealed class FaultBackend : IFanBackend
    {
        public string Name => "curve-isolation-fixture";
        public int[] Writes = new int[2], Releases = new int[2];
        public bool[] Manual = new bool[2];
        public volatile bool ThrowTick;
        public int ThrowRelease;
        public bool TryOpen(out string? reason) { reason = null; return true; }
        public IReadOnlyList<FanDescription> Describe() => [new(0, "CPU", true, true), new(1, "GPU", true, true)];
        public IReadOnlyList<FanState> Read() => [];
        public bool TrySetDuty(int index, double percent, out string? reason)
        {
            Manual[index] = true;
            Interlocked.Increment(ref Writes[index]);
            if (index == 0 && ThrowTick) throw new ApplicationException("injected tick");
            reason = null; return true;
        }
        public bool TryDriveDuty(int index, double percent, out string? reason) => TrySetDuty(index, percent, out reason);
        public bool TrySetFullSpeed(int index, out string? reason) => TrySetDuty(index, 100, out reason);
        public bool TrySetAutomatic(int index, out string? reason)
        {
            Interlocked.Increment(ref Releases[index]);
            if (index == 0 && Interlocked.Decrement(ref ThrowRelease) >= 0) throw new ApplicationException("injected release");
            Manual[index] = false; reason = null; return true;
        }
        public void Dispose() { }
    }
}
