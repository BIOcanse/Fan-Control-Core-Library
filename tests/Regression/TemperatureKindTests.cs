using System.Collections.Concurrent;
using System.Text.Json;
using FanControlCore;
using FanControlCore.Discovery;
using FanControlCore.Policy;

internal static class TemperatureKindTests
{
    private static readonly FanCurvePoint[] Curve = [new(0, 0), new(100, 100)];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception("Temperature kinds: " + message);
    }
    private static void Until(Func<bool> ready, string message)
        => Check(SpinWait.SpinUntil(ready, 3000), message);
    private static JsonElement Send(FanControlSession session, string request)
    {
        using var json = JsonDocument.Parse(request);
        return session.Handle(json.RootElement);
    }
    private static JsonElement Record(FanControlSession session, int fan)
        => Send(session, JsonSerializer.Serialize(new { op = "setcurve", fan, execution = "software", curve = Curve }, Json));
    private static void Kinds(IEnumerable<string> actual, params string[] expected)
        => Check(actual.SequenceEqual(expected), "expected kinds [" + string.Join(",", expected) + "]");
    private static void Receipt(JsonElement receipt, params string[] expected)
    {
        Check(receipt.TryGetProperty("softwareCurveRunNeeded", out _), "receipt retains run-needed fact");
        Kinds(receipt.GetProperty("softwareCurveTemperatureKinds").EnumerateArray().Select(item => item.GetString()!), expected);
    }

    internal static void Run()
    {
        TypedAndJson();
        PlanFacts();
        OrderedDelivery();
        DynamicTemperatures();
        BlindAndRetry();
        Console.WriteLine("Temperature kinds: typed/JSON batches, plan facts, ordered notifications, dynamic names, kept blind plans and retry passed.");
    }

    private static void TypedAndJson()
    {
        using var typed = new FanCore(new Backend());
        using var jsonCore = new FanCore(new Backend());
        using var session = new FanControlSession(typed);
        using var jsonSession = new FanControlSession(jsonCore);
        var now = DateTime.UtcNow.Ticks;
        var old = now - TimeSpan.FromSeconds(10).Ticks;
        var future = now + TimeSpan.FromHours(1).Ticks;
        session.PushTemperatures([
            new("CPU-package", 62, now), new("gpu0", 73, now), new("old", 45, old),
            new("zero", 10, 0), new("negative", 10, -1), new("future", 10, future),
            new("infinite", double.PositiveInfinity, now), new("nan", double.NaN, now), new("missing", 10, 0)]);
        var request = $$"""{"op":"temp","samples":{"CPU-package":62,"gpu0":73,"old":45,"zero":10,"negative":10,"future":10,"infinite":1e999,"nan":-1e999,"missing":10},"observedAtUtcTicks":{"cpu-PACKAGE":{{now}},"gpu0":{{now}},"old":{{old}},"zero":0,"negative":-1,"future":{{future}},"infinite":{{now}},"nan":{{now}} } }""";
        Check(Send(jsonSession, request).GetProperty("ok").GetBoolean(), "JSON temperature push succeeds");
        Check(typed.Temperatures.Names.SequenceEqual(jsonCore.Temperatures.Names), "typed and JSON store the same fresh names");
        foreach (var name in new[] { "cpu-package", "gpu0", "old", "zero", "negative", "future", "infinite", "nan", "missing" })
            Check(typed.Temperatures.Read(name) == jsonCore.Temperatures.Read(name), "typed/JSON value parity: " + name);
        Check(typed.Temperatures.Read("CPU-package") == 62 && typed.Temperatures.Read("gpu0") == 73,
            "valid values survive case-insensitive timestamp matching");
        Check(typed.Temperatures.Names.Count == 2, "non-finite, invalid timestamps and stale observations are unavailable");
        typed.PushTemperatures([new("cpu-later", 80, now)]);
        Check(typed.Temperatures.Read("cpu-package") is null && typed.Temperatures.Read("cpu-later") == 80,
            "core typed entry replaces the entire batch");
        session.PushTemperatures([]);
        Check(!typed.Temperatures.IsFresh && typed.Temperatures.Names.Count == 0, "empty batch clears temperatures");
        Send(jsonSession, """{"op":"temp","samples":{"cpu":59}}""");
        Check(jsonCore.Temperatures.Read("cpu") == 59, "JSON omitted timestamps retain arrival-time meaning");
    }

    private static void PlanFacts()
    {
        using var core = new FanCore(new Backend());
        using var session = new FanControlSession(core);
        var changes = new List<string>();
        Action<IReadOnlyList<string>> observer = kinds => changes.Add(string.Join(",", kinds));
        session.SoftwareCurveTemperatureKindsChanged += observer;
        Kinds(core.SoftwareCurveTemperatureKinds);
        Kinds(session.SoftwareCurveTemperatureKinds);
        Check(core.NeedsTemperatures && Send(session, """{"op":"describe"}""").GetProperty("needsTemperatures").GetBoolean(),
            "capability stays true with no plan");
        Check(Record(session, 0).GetProperty("ok").GetBoolean(), "CPU plan records with no pushed temperature");
        Receipt(Send(session, """{"op":"curve","fan":0}"""), FanTemperatureKinds.Cpu);
        Check(changes.SequenceEqual(new[] { "cpu" }), "first CPU plan emits one event");
        Check(Record(session, 0).GetProperty("ok").GetBoolean(), "replace plan without temperature");
        var rejected = Send(session, """{"op":"setcurve","fan":0,"execution":"software","curve":[]}""");
        Check(!rejected.GetProperty("ok").GetBoolean(), "empty curve is still rejected");
        Receipt(rejected, FanTemperatureKinds.Cpu);
        Check(Record(session, 3).GetProperty("ok").GetBoolean(), "another CPU plan records");
        Check(changes.Count == 1, "replacement and duplicate kind do not emit events");
        Receipt(Record(session, 2), FanTemperatureKinds.Cpu, FanTemperatureKinds.Any);
        Receipt(Record(session, 1), FanTemperatureKinds.Cpu, FanTemperatureKinds.Gpu, FanTemperatureKinds.Any);
        Kinds(session.SoftwareCurveTemperatureKinds, "cpu", "gpu", "any");
        Receipt(Send(session, """{"op":"auto","fan":0}"""), "cpu", "gpu", "any");
        Receipt(Send(session, """{"op":"set","fan":3,"percent":40}"""), "gpu", "any");
        Receipt(Send(session, """{"op":"boost","fan":1}"""), "any");
        core.RestoreAll();
        core.RestoreAll();
        Kinds(session.SoftwareCurveTemperatureKinds);
        Check(changes.SequenceEqual(new[] { "cpu", "cpu,any", "cpu,gpu,any", "gpu,any", "any", "" }),
            "auto, set, boost and RestoreAll shrink distinct kinds in order with no duplicate events");
        foreach (var op in new[] { "curve", "setcurve", "auto", "set", "boost" })
        {
            var failed = Send(session, JsonSerializer.Serialize(new { op })); // missing fan
            Check(!failed.GetProperty("ok").GetBoolean(), "missing fan is rejected for " + op);
            Receipt(failed);
        }
        Check(changes.Count == 6, "failed requests change no facts");
        session.SoftwareCurveTemperatureKindsChanged -= observer;
        Record(session, 0);
        Check(changes.Count == 6, "unsubscribed observer is not called");
    }

    private static void OrderedDelivery()
    {
        using var core = new FanCore(new Backend());
        using var session = new FanControlSession(core);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var changes = new ConcurrentQueue<string>();
        var errors = new ConcurrentQueue<string>();
        core.SoftwareCurveTemperatureKindsChanged += _ => throw new Exception("fixture observer failure");
        core.SoftwareCurveTemperatureKindsChanged += kinds =>
        {
            if (!Task.Run(() => core.SoftwareCurveTemperatureKinds).Wait(1000)) errors.Enqueue("observer holds engine gate");
            changes.Enqueue(string.Join(",", kinds));
            if (kinds.Count == 1)
            {
                entered.Set();
                if (!release.Wait(3000)) errors.Enqueue("observer was not released");
            }
        };
        var first = Task.Run(() => core.TryWriteCurve(0, Curve, "software", out _));
        try
        {
            Check(entered.Wait(3000), "first kinds notification enters");
            var next = Task.Run(() => core.TrySetAutomatic(0, out _));
            Check(next.Wait(1000) && next.Result, "concurrent mutation completes while notification is blocked");
        }
        finally { release.Set(); }
        Check(first.Wait(3000) && first.Result, "first recording completes");
        Check(errors.IsEmpty && changes.SequenceEqual(new[] { "cpu", "" }), "events arrive outside gate in change order despite throwing observer");
        Check(core.Diagnostics.Any(message => message.Contains("fixture observer failure")), "observer failure is diagnosed");

        // A reentrant mutation must queue behind every observer of the current snapshot.
        var reentrant = new List<string>();
        Action<IReadOnlyList<string>> remove = kinds => { if (kinds.Count > 0) core.RestoreAll(); };
        core.SoftwareCurveTemperatureKindsChanged += remove;
        core.SoftwareCurveTemperatureKindsChanged += kinds => reentrant.Add(string.Join(",", kinds));
        Record(session, 1);
        Check(reentrant.SequenceEqual(new[] { "gpu", "" }), "reentrant changes preserve snapshot delivery order");
    }

    private static void DynamicTemperatures()
    {
        foreach (var fan in new[] { 0, 1, 2 })
        {
            var backend = new Backend();
            using var core = new FanCore(backend);
            using var session = new FanControlSession(core);
            Check(Record(session, fan).GetProperty("ok").GetBoolean(), "record before any names exist");
            using var run = new CurveRun(session.RunSoftwareCurveAsync);
            // Supply only another kind (or GPU-only for any); it cannot drive this fan.
            session.PushTemperatures([new(fan == 1 ? "cpu-other" : "gpu-other", 99, DateTime.UtcNow.Ticks)]);
            Thread.Sleep(250);
            Check(backend.Writes.IsEmpty, "missing needed kind does not drive, including any without CPU");
            session.PushTemperatures([
                new("CPU-brand-new", 45, DateTime.UtcNow.Ticks), new("gpu-brand-new", 75, DateTime.UtcNow.Ticks),
                new("board", 85, DateTime.UtcNow.Ticks)]);
            var expected = fan switch { 0 => 45, 1 => 75, _ => 85 };
            Until(() => backend.Writes.Contains((fan, expected)), "plan drives from new names of its kind; any takes hottest including board");
            // Replacing all source names must keep the plan useful without re-recording it.
            session.PushTemperatures([
                new("cpu-replaced", 100, DateTime.UtcNow.Ticks), new("GPU-replaced", 100, DateTime.UtcNow.Ticks)]);
            Until(() => backend.Writes.Contains((fan, 100)), "plan reads replacement names after recording");
        }
    }

    /// <summary>
    /// Review B-M1: a blind timeout hands a driven fan back but keeps the user's plan and its kind, so the host keeps
    /// pushing and the plan resumes by itself; a fan that was never driven is not touched at all.
    /// </summary>
    private static void BlindAndRetry()
    {
        var backend = new Backend { FailRelease = true };
        using var core = new FanCore(backend);
        using var session = new FanControlSession(core);
        core.CurveEngine.BlindTimeout = TimeSpan.FromMilliseconds(500);
        var changes = new ConcurrentQueue<string>();
        session.SoftwareCurveTemperatureKindsChanged += kinds => changes.Enqueue(string.Join(",", kinds));
        Record(session, 0);
        using var run = new CurveRun(session.RunSoftwareCurveAsync);
        Thread.Sleep(300);
        Check(backend.Writes.IsEmpty && backend.Releases == 0 && core.SoftwarePlanRecorded(0), "blind from first tick waits without driving or premature release");
        Until(() => core.SoftwareCurveSuspension(0) == FanCurveSuspension.Blind, "initial missing kind reaches the blind timeout and suspends the plan");
        Thread.Sleep(200);
        Check(backend.Writes.IsEmpty && backend.Releases == 0, "a never-driven fan is not touched by the blind hand-back");
        Check(core.SoftwarePlanRecorded(0) && session.SoftwareCurveRunNeeded
            && session.SoftwareCurveTemperatureKinds.SequenceEqual(new[] { "cpu" }), "plan and its kind are kept, so the host keeps pushing");
        Check(changes.SequenceEqual(new[] { "cpu" }), "suspension emits no kinds change");

        session.PushTemperatures([new("cpu", 40, DateTime.UtcNow.Ticks)]);
        Until(() => backend.Writes.Contains((0, 40)) && core.SoftwareCurveSuspension(0) is null, "temperature returns: plan resumes by itself");
        session.PushTemperatures([]);
        Until(() => core.SoftwareCurveSuspension(0) == FanCurveSuspension.Blind && backend.Releases > 0,
            "driven fan goes blind: handed back, plan kept");
        Check(core.ExecutionOf(0) is null && core.SoftwarePlanRecorded(0), "receipt: recorded, not executing");

        var failed = Send(session, """{"op":"auto","fan":0}""");
        Check(!failed.GetProperty("ok").GetBoolean() && failed.GetProperty("errorKind").GetString() == "Other",
            "pending release returns an unsuccessful receipt with the backend's (untyped) kind");
        Receipt(failed);
        Check(!core.SoftwarePlanRecorded(0) && session.SoftwareCurveRunNeeded && session.SoftwareCurveTemperatureKinds.Count == 0,
            "only a hand-back retry remains: run needed with empty kinds");
        Check(changes.SequenceEqual(new[] { "cpu", "" }), "explicit removal emits exactly the two kinds changes");
        backend.FailRelease = false;
        Until(() => !core.SoftwareCurveRunNeeded, "retry succeeds without any temperature push");
        Check(changes.Count == 2, "retry-only completion does not emit a kinds change");
    }

    private sealed class Backend : IFanBackend
    {
        public string Name => "temperature-kind-fixture";
        public ConcurrentQueue<(int Fan, int Percent)> Writes { get; } = new();
        public int Releases;
        public volatile bool FailRelease;
        public bool TryOpen(out string? reason) { reason = null; return true; }
        public IReadOnlyList<FanDescription> Describe() => [
            new(0, "CPU", true, true, FanRoles.Cpu, SupportsSoftwareCurve: true),
            new(1, "GPU", true, true, FanRoles.Gpu, SupportsSoftwareCurve: true),
            new(2, "shared", true, true, FanRoles.Unknown, SupportsSoftwareCurve: true),
            new(3, "CPU second", true, true, FanRoles.Cpu, SupportsSoftwareCurve: true)];
        public IReadOnlyList<FanState> Read() => [];
        public bool TryDriveDuty(int fan, double percent, out string? reason)
        { Writes.Enqueue((fan, (int)percent)); reason = null; return true; }
        public bool TrySetDuty(int fan, double percent, out string? reason) { reason = null; return true; }
        public bool TrySetFullSpeed(int fan, out string? reason) { reason = null; return true; }
        public bool TrySetAutomatic(int fan, out string? reason)
        { Interlocked.Increment(ref Releases); reason = FailRelease ? "fixture failure" : null; return !FailRelease; }
        public void Dispose() { }
    }
}
