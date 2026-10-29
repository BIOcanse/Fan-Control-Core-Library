using System.Collections.Concurrent;
using System.Text.Json;
using FanControlCore;
using FanControlCore.Discovery;
using FanControlCore.Policy;

/// <summary>Review B-M1, B-M3, B-M5, B-L2, the engine side of B-H1 and B-H2, with fake backends only.</summary>
internal static class ReviewFixTests
{
    private static readonly FanCurvePoint[] Curve = [new(0, 0), new(100, 100)];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static void Check(bool condition, string message) { if (!condition) throw new Exception("Review fixes: " + message); }
    private static void Until(Func<bool> ready, string message, int milliseconds = 4000)
        => Check(SpinWait.SpinUntil(ready, milliseconds), message);
    private static JsonElement Send(FanCore core, string request)
        => JsonSerializer.SerializeToElement(FanCommandServer.Handle(core, request), Json);
    private static JsonElement Send(FanCore core, object request) => Send(core, JsonSerializer.Serialize(request, Json));
    private static string? Kind(JsonElement reply)
        => reply.TryGetProperty("errorKind", out var kind) && kind.ValueKind == JsonValueKind.String ? kind.GetString() : null;

    internal static void Run()
    {
        FailedPlanIsKeptAndSuspended();
        FailedPlanWithStuckHandBack();
        SlowHardwareDoesNotBlockQueries();
        ReleaseRetriesBackOff();
        SharedTakeoverReleaseForcesRewrite();
        MachineModeChannelWithoutHolderCounting();
        MachineModeGroupThroughFanCore();
        MachineModeDutyRange();
        GroupFirmwareSwitchIsOneBatch();
        HandBackThrowDoesNotStopGroup();
        ErrorKinds();
        if (OperatingSystem.IsWindows())
        {
            SharedLibreHardwareMonitorComputer();
            SuperIoRouteIsLast();
        }
        Console.WriteLine("Review fixes: kept and suspended plans, a failed plan whose hand-back keeps failing, queries during slow hardware, release backoff, forced rewrite only with holder counting, machine-mode channel and group, a throwing hand-back that does not stop the group, error kinds, host-only LHM computer and route order passed.");
    }

    /// <summary>
    /// A failed plan whose hand-back keeps failing: the run is still needed (the fan is not back in firmware yet), no
    /// temperatures are needed (the plan waits for a new recording), and the retries back off.
    /// </summary>
    private static void FailedPlanWithStuckHandBack()
    {
        var backend = new Backend { FailDrive = true, FailRelease = true };
        using var engine = new SoftwareCurveEngine(backend, TimeSpan.FromMilliseconds(10));
        Check(engine.TryRecordPlan(new(0, Curve, [new("cpu", () => 50)]), out _), "plan");
        using var run = new CurveRun(engine.RunAsync);
        Until(() => engine.Suspension(0) == FanCurveSuspension.Failed && backend.Releases > 0, "failed and hand-back attempted");
        var first = backend.Releases;
        Thread.Sleep(700);
        var attempts = backend.Releases - first;
        Check(attempts is >= 3 and <= 10, $"stuck hand-back retries back off (saw {attempts} in 700 ms at a 10 ms tick)");
        Check(engine.SoftwareCurveRunNeeded && engine.SoftwareCurveTemperatureKinds.Count == 0 && engine.HasRuntime(0),
            "run still needed for the pending hand-back; no temperatures needed");
        backend.FailRelease = false;
        Until(() => !engine.HasRuntime(0) && !engine.SoftwareCurveRunNeeded, "once the hand-back succeeds nothing more is needed");
    }

    /// <summary>
    /// A covers-all channel without holder counting (Acer semantics: one machine mode, any release returns the whole
    /// machine to automatic). An explicit stop of one fan forces no rewrite of the other (dev.6 behaviour; through FanCore
    /// the machine-mode group makes a per-fan stop impossible). A blind fan hands the whole takeover back.
    /// </summary>
    private static void MachineModeChannelWithoutHolderCounting()
    {
        var backend = new MachineModeBackend();
        using var engine = new SoftwareCurveEngine(backend, TimeSpan.FromMilliseconds(10)) { BlindTimeout = TimeSpan.FromMilliseconds(100) };
        var gpu = 1;
        Check(engine.TryRecordPlan(new(0, Curve, [new("cpu", () => 40)]), out _)
            && engine.TryRecordPlan(new(1, Curve, [new("gpu", () => Volatile.Read(ref gpu) != 0 ? 60 : null)]), out _), "two plans");
        using var run = new CurveRun(engine.RunAsync);
        Until(() => backend.Drives[0] > 0 && backend.Drives[1] > 0 && backend.Custom, "both driven");
        Thread.Sleep(100);
        var before = backend.Drives[0];
        Check(engine.TryStop(1, out _), "stop fan 1");
        Until(() => backend.Releases[1] > 0, "fan 1 released");
        Thread.Sleep(200);
        Check(backend.Drives[0] == before, "no forced rewrite without holder counting");

        Check(engine.TryRecordPlan(new(1, Curve, [new("gpu", () => Volatile.Read(ref gpu) != 0 ? 60 : null)]), out _), "fan 1 again");
        Until(() => engine.IsRunning(1) && backend.Custom, "both running again");
        Volatile.Write(ref gpu, 0);
        Until(() => engine.Suspension(1) == FanCurveSuspension.Blind && engine.Suspension(0) == FanCurveSuspension.SharedHandBack
            && !engine.HasRuntime(0) && !engine.HasRuntime(1) && !backend.Custom, "blind fan 1 hands the whole machine back");
        Volatile.Write(ref gpu, 1);
        Until(() => engine.IsRunning(0) && engine.IsRunning(1) && backend.Custom, "both resume once the temperature returns");
    }

    /// <summary>
    /// Round-3 M1: Acer takes 20 to 100 % only. A fixed 0 % is refused before any plan is recorded (the channel cannot
    /// stop a fan); a value below 20 % is raised to 20 % in the recorded plan; a curve target below the floor holds
    /// the floor instead of stopping. No drive is ever rejected, so nothing fails into a whole-group hand-back.
    /// Round-4 M-A: a curve point above 100 % is refused the same way, in the group switch, in a per-fan software
    /// curve inside the software group, and when recorded on the engine directly.
    /// </summary>
    private static void MachineModeDutyRange()
    {
        var backend = new MachineModeBackend();
        using var core = new FanCore(backend);
        Check(core.Open(), "duty-range fixture opens");
        var zero = Send(core, new { op = "group", group = "machine", control = "software",
            fans = new object[] { new { fan = 0, percent = 0 }, new { fan = 1, percent = 50 } } });
        Check(Kind(zero) == "Unsupported" && zero.GetProperty("controlGroup").GetString() == "machine"
            && !core.SoftwarePlanRecorded(0) && !core.SoftwarePlanRecorded(1), "0 % refused before any plan is recorded");
        Check(Kind(Send(core, new { op = "group", group = "machine", control = "software",
            fans = new object[] { new { fan = 0, percent = 140 }, new { fan = 1, percent = 50 } } })) == "InvalidRequest", "above 100 % refused");
        var over = Send(core, new { op = "group", group = "machine", control = "software",
            fans = new object[] { new { fan = 0, percent = 40 }, new { fan = 1, curve = OverRange } } });
        Check(Kind(over) == "InvalidRequest" && over.GetProperty("controlGroup").GetString() == "machine"
            && !core.SoftwarePlanRecorded(0) && !core.SoftwarePlanRecorded(1) && core.GroupControl("machine") == FanGroupControl.Firmware,
            "M-A: a curve point above 100 % is refused before any plan is recorded");
        core.PushTemperatures([new("cpu", 5, DateTime.UtcNow.Ticks), new("gpu", 5, DateTime.UtcNow.Ticks)]);
        var low = Send(core, new { op = "group", group = "machine", control = "software",
            fans = new object[] { new { fan = 0, percent = 10 }, new { fan = 1, curve = Curve } } });
        Check(low.GetProperty("ok").GetBoolean() && core.ReadCurve(0)!.Single().Percent == 20, "10 % is recorded as the 20 % floor");
        using (var temperatures = new Timer(_ => core.PushTemperatures([new("cpu", 5, DateTime.UtcNow.Ticks), new("gpu", 5, DateTime.UtcNow.Ticks)]),
            null, 0, 100))
        using (var run = new CurveRun(core.RunSoftwareCurveAsync))
        {
            Until(() => backend.Last[0] == 20 && backend.Last[1] == 20 && backend.Custom, "both at the floor; the 5 % curve target holds 20 %");
            Thread.Sleep(300);
            Check(backend.Rejections == 0 && core.SoftwareCurveSuspension(0) is null && core.SoftwareCurveSuspension(1) is null,
                "no drive was rejected and nothing failed");
            Check(Kind(Send(core, new { op = "set", fan = 1, percent = 0 })) == "Unsupported" && core.ReadCurve(1)!.Count == 2,
                "a per-fan 0 % inside the group is refused too and keeps the curve");
            Check(Kind(Send(core, new { op = "setcurve", fan = 1, execution = "software", curve = OverRange })) == "InvalidRequest"
                && core.ReadCurve(1)!.SequenceEqual(Curve), "M-A: a per-fan software curve above 100 % is refused and keeps the curve");
            Check(backend.Rejections == 0, "still no drive was rejected");
            Check(Send(core, new { op = "group", group = "machine", control = "firmware" }).GetProperty("ok").GetBoolean(), "cleanup");
        }
        using var engine = new SoftwareCurveEngine(new MachineModeBackend());
        Check(!engine.TryRecordPlan(new(1, OverRange, [new("gpu", () => 85)]), out _) && !engine.HasPlan(1),
            "M-A: the engine refuses to record a point above 100 % for direct users");
    }

    /// <summary>Round-4 M-A: at 85 °C this curve asks for 120 %, which Acer refuses on every write.</summary>
    private static readonly FanCurvePoint[] OverRange = [new(40, 50), new(80, 120)];

    /// <summary>
    /// Round-3 H2: switching a group to firmware removes every member's plan and marks every runtime as being handed
    /// back in one state change, before any release. A barrier holds the switch right after that change while run ticks
    /// keep going with new targets: no member is driven again (on Acer one drive would switch the whole machine back to
    /// custom). Then every member is released and the machine stays automatic.
    /// </summary>
    private static void GroupFirmwareSwitchIsOneBatch()
    {
        var backend = new MachineModeBackend();
        using var core = new FanCore(backend);
        Check(core.Open(), "batch fixture opens");
        var celsius = 40.0;
        using var temperatures = new Timer(_ =>
        {
            var now = DateTime.UtcNow.Ticks;
            var value = Volatile.Read(ref celsius);
            core.PushTemperatures([new("cpu", value, now), new("gpu", value + 20, now)]);
        }, null, 0, 50);
        Thread.Sleep(150);
        Check(Send(core, new { op = "group", group = "machine", control = "software",
            fans = new object[] { new { fan = 0, curve = Curve }, new { fan = 1, curve = Curve } } }).GetProperty("ok").GetBoolean(), "software");
        using var marked = new ManualResetEventSlim();
        using var proceed = new ManualResetEventSlim();
        core.CurveEngine.AfterBatchStopMarked = () => { marked.Set(); proceed.Wait(5000); };
        using (var run = new CurveRun(core.RunSoftwareCurveAsync))
        {
            Until(() => backend.Last[0] == 40 && backend.Last[1] == 60 && backend.Custom, "both driven");
            var firmware = Task.Run(() => Send(core, new { op = "group", group = "machine", control = "firmware" }));
            try
            {
                Check(marked.Wait(3000), "the switch reached the point after its one state change");
                Check(!core.SoftwarePlanRecorded(0) && !core.SoftwarePlanRecorded(1), "both plans are gone at once");
                Thread.Sleep(50); // a drive already past its check before the change may still complete
                var drives = backend.Drives.ToArray();
                Volatile.Write(ref celsius, 70); // new targets: any tick that could drive a member would write now
                Thread.Sleep(400);
                Check(backend.Drives.SequenceEqual(drives), "no run tick drove any member after the batch stop began");
            }
            finally { proceed.Set(); }
            var reply = firmware.Result;
            Check(reply.GetProperty("ok").GetBoolean()
                && reply.GetProperty("handedBack").EnumerateArray().Select(entry => entry.GetInt32()).SequenceEqual([0, 1]),
                "both members handed back and listed");
            Until(() => !backend.Custom && !core.CurveEngine.HasRuntime(0) && !core.CurveEngine.HasRuntime(1), "machine automatic");
            var settled = backend.Drives.ToArray();
            Thread.Sleep(300);
            Check(!backend.Custom && backend.Drives.SequenceEqual(settled), "and it stays automatic");
        }
        core.CurveEngine.AfterBatchStopMarked = null;
    }

    /// <summary>
    /// Round-5 T-B: a member's hand-back that throws is caught in that hand-back, so a switch to firmware goes on with
    /// the next member. On a channel without holder counting the next member is under firmware at once and listed; the
    /// fault is typed Other and the faulting member stays pending for the next switch.
    /// </summary>
    private static void HandBackThrowDoesNotStopGroup()
    {
        var backend = new MachineModeBackend();
        using var core = new FanCore(backend);
        Check(core.Open(), "throwing-release fixture opens");
        Check(Send(core, new { op = "group", group = "machine", control = "software",
            fans = new object[] { new { fan = 0, percent = 40 }, new { fan = 1, percent = 50 } } }).GetProperty("ok").GetBoolean(), "software");
        backend.ThrowAutomaticFor = 0;
        var faulted = Send(core, new { op = "group", group = "machine", control = "firmware" });
        Check(Kind(faulted) == "Other" && faulted.GetProperty("controlGroup").GetString() == "machine"
            && faulted.GetProperty("error").GetString()!.Contains("fixture transport fault"),
            "fan 0's release throws: a typed failure that names the group");
        Check(backend.Releases[1] == 1
            && faulted.GetProperty("handedBack").EnumerateArray().Select(entry => entry.GetInt32()).SequenceEqual([1]),
            "fan 1 is still handed back and listed");
        backend.ThrowAutomaticFor = -1;
        var retried = Send(core, new { op = "group", group = "machine", control = "firmware" });
        Check(retried.GetProperty("ok").GetBoolean() && backend.Releases[0] == 1
            && retried.GetProperty("handedBack").EnumerateArray().Select(entry => entry.GetInt32()).SequenceEqual([0]),
            "the next switch hands back the pending member");
    }

    /// <summary>
    /// The machine-mode group through FanCore and JSON: a group whose fixed duty is itself the machine mode
    /// (firmwareFixedDuty false) refuses a per-fan set under firmware; a group switch drives both in software.
    /// </summary>
    private static void MachineModeGroupThroughFanCore()
    {
        var backend = new MachineModeBackend();
        using var core = new FanCore(backend);
        Check(core.Open(), "machine-mode fixture opens");
        var group = Send(core, new { op = "describe" }).GetProperty("controlGroups")[0];
        Check(group.GetProperty("id").GetString() == "machine" && group.GetProperty("reason").GetString() == "AcerMachineMode"
            && !group.GetProperty("firmwareFixedDuty").GetBoolean(), "group described");
        var set = Send(core, new { op = "set", fan = 0, percent = 40 });
        Check(Kind(set) == "ControlGroupSwitchRequired" && set.GetProperty("controlGroup").GetString() == "machine"
            && backend.Sets == 0, "a per-fan fixed duty under firmware is refused and names the group");
        var applied = Send(core, new { op = "group", group = "machine", control = "software",
            fans = new object[] { new { fan = 0, percent = 40 }, new { fan = 1, percent = 50 } } });
        Check(applied.GetProperty("ok").GetBoolean(), "group to software");
        using (var run = new CurveRun(core.RunSoftwareCurveAsync))
        {
            Until(() => backend.Last[0] == 40 && backend.Last[1] == 50 && backend.Custom, "both fixed duties driven by the run");
            Check(backend.Sets == 0, "a fixed duty inside a software group is driven, not set as a firmware mode");
            Check(Send(core, new { op = "group", group = "machine", control = "firmware" }).GetProperty("ok").GetBoolean(), "back to firmware");
            Until(() => !backend.Custom && !core.CurveEngine.HasRuntime(0) && !core.CurveEngine.HasRuntime(1), "machine automatic");
        }
    }

    /// <summary>B-M1: five rejected writes hand the fan back but keep the plan; recording it again resumes.</summary>
    private static void FailedPlanIsKeptAndSuspended()
    {
        var backend = new Backend { FailDrive = true };
        using var core = new FanCore(backend);
        Check(core.Open(), "fixture opens");
        core.PushTemperatures([new("cpu", 50, DateTime.UtcNow.Ticks)]);
        Check(core.TryWriteCurve(0, Curve, "software", out _), "plan records");
        var needed = new ConcurrentQueue<bool>();
        core.SoftwareCurveRunNeededChanged += needed.Enqueue;
        using var run = new CurveRun(core.RunSoftwareCurveAsync);
        Until(() => core.SoftwareCurveSuspension(0) == FanCurveSuspension.Failed && backend.Releases > 0,
            "five failed writes suspend the plan and hand the fan back");
        Check(backend.Drives == 5, "no write after the fifth failure");
        Check(core.SoftwarePlanRecorded(0) && core.ReadCurve(0)!.SequenceEqual(Curve), "the user's plan is kept");
        var receipt = Send(core, new { op = "curve", fan = 0 });
        Check(receipt.GetProperty("softwareCurveSuspension").GetString() == "Failed"
            && receipt.GetProperty("execution").ValueKind == JsonValueKind.Null, "curve receipt reports the typed suspension");
        Until(() => !core.SoftwareCurveRunNeeded && core.SoftwareCurveTemperatureKinds.Count == 0,
            "a failed plan needs neither a run nor temperatures until it is recorded again");
        Thread.Sleep(300);
        Check(backend.Drives == 5, "suspended plan is not retried on its own");
        backend.FailDrive = false;
        core.PushTemperatures([new("cpu", 50, DateTime.UtcNow.Ticks)]);
        Check(core.TryWriteCurve(0, Curve, "software", out _), "record again");
        Check(core.SoftwareCurveSuspension(0) is null && core.SoftwareCurveRunNeeded, "recording clears the suspension");
        Until(() => backend.Drives > 5 && core.ExecutionOf(0) == FanCurveExecutions.Software, "plan resumes");
        Check(needed.ToArray().SequenceEqual([false, true]), "run-needed follows the suspension in order");
    }

    /// <summary>B-M3: a drive blocked in the backend leaves queries, receipts and plan recording responsive.</summary>
    private static void SlowHardwareDoesNotBlockQueries()
    {
        using var block = new ManualResetEventSlim();
        var backend = new Backend { BlockDrive = block };
        using var core = new FanCore(backend);
        Check(core.Open(), "slow fixture opens");
        core.PushTemperatures([new("cpu", 50, DateTime.UtcNow.Ticks)]);
        Check(core.TryWriteCurve(0, Curve, "software", out _), "plan records");
        var run = new CurveRun(core.RunSoftwareCurveAsync);
        try
        {
            Until(() => backend.Blocked == 1 && core.InFlightOperation?.Contains("curve tick") == true, "drive is stuck in hardware");
            var queries = Task.Run(() =>
            {
                var curve = Send(core, new { op = "curve", fan = 0 });
                var describe = Send(core, new { op = "describe" });
                var record = Send(core, new { op = "setcurve", fan = 1, execution = "software", curve = Curve });
                return curve.GetProperty("ok").GetBoolean() && describe.GetProperty("ok").GetBoolean()
                    && record.GetProperty("softwarePlanRecorded").GetBoolean()
                    && core.SoftwareCurveRunNeeded && core.Diagnostics is not null && core.SoftwarePlanRecorded(0);
            });
            Check(queries.Wait(1000) && queries.Result, "curve, describe, setcurve and facts answer while the tick waits on hardware");
        }
        finally { block.Set(); }
        run.Dispose();
        Check(backend.Releases > 0, "stopped run still hands back after the slow drive finished");
    }

    /// <summary>B-L2: a failing hand-back is retried with a doubling delay, not every tick.</summary>
    private static void ReleaseRetriesBackOff()
    {
        var backend = new Backend { FailRelease = true };
        using var engine = new SoftwareCurveEngine(backend, TimeSpan.FromMilliseconds(10)) { BlindTimeout = TimeSpan.FromMilliseconds(50) };
        var available = 1;
        Check(engine.TryRecordPlan(new(0, Curve, [new("cpu", () => Volatile.Read(ref available) != 0 ? 50 : null)]), out _), "plan");
        using var run = new CurveRun(engine.RunAsync);
        Until(() => backend.Drives > 0, "driven");
        Volatile.Write(ref available, 0);
        Until(() => backend.Releases > 0, "blind hand-back attempted");
        var first = backend.Releases;
        Thread.Sleep(700);
        var attempts = backend.Releases - first;
        Check(attempts is >= 3 and <= 10, $"backoff limits retries to a few in 700 ms at a 10 ms tick (saw {attempts})");
        Check(engine.Suspension(0) == FanCurveSuspension.Blind && engine.HasPlan(0), "blind plan kept while the hand-back retries");
        backend.FailRelease = false;
        Until(() => !engine.HasRuntime(0), "retry eventually succeeds");
    }

    /// <summary>
    /// B-H1 engine side: on a covers-all channel that counts holders, a release may change the shared mode, so the
    /// other running fans are rewritten on the next tick even when their target is unchanged.
    /// </summary>
    private static void SharedTakeoverReleaseForcesRewrite()
    {
        var backend = new SharedFlagBackend();
        using var engine = new SoftwareCurveEngine(backend, TimeSpan.FromMilliseconds(10));
        Check(engine.TryRecordPlan(new(0, Curve, [new("cpu", () => 40)]), out _)
            && engine.TryRecordPlan(new(1, Curve, [new("gpu", () => 60)]), out _), "two plans");
        using var run = new CurveRun(engine.RunAsync);
        Until(() => backend.Drives[0] > 0 && backend.Drives[1] > 0 && backend.Shared, "both driven");
        Thread.Sleep(100);
        var before = backend.Drives[0];
        Check(engine.TryStop(1, out _), "stop fan 1");
        Until(() => backend.Drives[0] > before && backend.Shared, "fan 0 rewritten with its unchanged target after the shared release");
        Thread.Sleep(100);
        var settled = backend.Drives[0];
        Thread.Sleep(100);
        Check(backend.Drives[0] == settled, "one forced rewrite, then deduplication resumes");
    }

    /// <summary>B-M5: every rejected command carries a typed errorKind next to its text.</summary>
    private static void ErrorKinds()
    {
        var backend = new Backend();
        using var core = new FanCore(backend);
        Check(core.Open(), "kinds fixture opens");
        Check(Kind(Send(core, "{not json")) == "InvalidRequest", "malformed JSON");
        Check(Kind(Send(core, """{"op":"spin"}""")) == "InvalidRequest", "unknown operation");
        Check(Kind(Send(core, """{"op":"set","percent":40}""")) == "InvalidRequest", "missing fan");
        Check(Kind(Send(core, """{"op":"set","fan":"zero","percent":40}""")) == "InvalidRequest", "mistyped fan");
        Check(Kind(Send(core, """{"op":"setcurve","fan":0,"execution":"software","curve":[]}""")) == "InvalidRequest", "empty curve");
        Check(Kind(Send(core, """{"op":"setcurve","fan":0,"execution":"software","curve":[{"temperatureCelsius":40,"percent":-5}]}"""))
            == "InvalidRequest", "a software curve point below 0 %");

        // Optional pre-existing fix: the stdio server answers a non-object line or a non-string op and keeps serving.
        using (var input = new StringReader("[1]\n7\n{\"notification\":true,\"op\":5}\n{\"id\":4,\"op\":\"describe\"}\n"))
        using (var output = new StringWriter())
        {
            FanCommandServer.Run(core, input, output);
            var replies = output.ToString().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
            Check(replies.Length == 4 && replies.Take(3).All(reply => Kind(reply) == "InvalidRequest")
                && replies[3].GetProperty("ok").GetBoolean() && replies[3].GetProperty("id").GetInt64() == 4,
                "stdio: three malformed lines answered as InvalidRequest, then describe still served");
        }
        Check(Send(core, new { op = "describe" }).GetProperty("controlGroups").GetArrayLength() == 0,
            "fans without a shared control switch are in no group");
        Check(Kind(Send(core, new { op = "group", group = "any", control = "software" })) == "InvalidRequest", "no such group");
        Check(Kind(Send(core, new { op = "set", fan = 3, percent = 40 })) == "NoSuchFan", "unknown fan");
        Check(Kind(Send(core, new { op = "set", fan = 1, percent = 40 })) == "Unsupported", "no duty on this fan");
        Check(Kind(Send(core, new { op = "setcurve", fan = 0, execution = "firmware", curve = Curve })) == "Unsupported", "no firmware curve");
        backend.FailSet = true;
        var other = Send(core, new { op = "set", fan = 0, percent = 40 });
        Check(Kind(other) == "Other" && other.GetProperty("error").GetString() == "fixture set failure",
            "untyped backend failure stays Other with its text");
        backend.FailSet = false;

        core.PushTemperatures([new("cpu", 50, DateTime.UtcNow.Ticks)]);
        Check(core.TryWriteCurve(0, Curve, "software", out _), "plan");
        using (var run = new CurveRun(core.RunSoftwareCurveAsync))
        {
            Until(() => backend.Drives > 0, "driven");
            backend.FailRelease = true;
        }
        Check(Kind(Send(core, new { op = "setcurve", fan = 0, execution = "software", curve = Curve })) == "HandBackPending",
            "pending hand-back");
        backend.FailRelease = false;

        using (var unavailable = new FanCore(new Backend { Available = false }))
        {
            unavailable.Open();
            Check(Kind(Send(unavailable, new { op = "boost", fan = 0 })) == "Unavailable", "no channel");
        }

        var blockedRoot = Path.Combine(Path.GetTempPath(), "fcc-kinds-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blockedRoot, "a file where the state directory should be");
        try
        {
            using var noState = new FanCore(new Backend(), new FanCoreOptions(blockedRoot, MachineIdentity: () => "kinds"));
            Check(noState.Open(), "unwritable-state fixture opens");
            Check(Kind(Send(noState, new { op = "set", fan = 0, percent = 40 })) == "StateUnavailable", "journal cannot be saved");
        }
        finally { File.Delete(blockedRoot); }
    }

    /// <summary>
    /// B-H2: with the host's open LibreHardwareMonitor Computer, the Super I/O route never opens, closes or updates a
    /// Computer while discovering, and only looks at the motherboard tree.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void SharedLibreHardwareMonitorComputer()
    {
        var cpu = new LibreHardwareMonitor.Hardware.FakeHardware("Cpu", [], [new("Control", 0, "CPU control", new LibreHardwareMonitor.Hardware.FakeControl())]);
        var laptop = new LibreHardwareMonitor.Hardware.Computer { IsMotherboardEnabled = true };
        laptop.Hardware.Add(new LibreHardwareMonitor.Hardware.FakeHardware("Motherboard", [], []));
        laptop.Hardware.Add(cpu);
        using (var backend = new FanControlCore.Protocols.DesktopSuperIoFanBackend(laptop))
        {
            Check(!backend.TryOpen(out var reason) && reason!.Contains("主板风扇接头"), "laptop: no writable header");
            Check(backend.Describe().Count == 0, "other hardware groups are not collected");
        }
        Check(laptop.Opens == 0 && laptop.Closes == 0 && cpu.Updates == 0, "shared computer never opened, closed or updated");

        var control = new LibreHardwareMonitor.Hardware.FakeControl();
        var superIo = new LibreHardwareMonitor.Hardware.FakeHardware("SuperIO", [],
            [new("Fan", 0, "Fan #1", null) { Value = 900 }, new("Control", 0, "Fan Control #1", control)]);
        var desktop = new LibreHardwareMonitor.Hardware.Computer { IsMotherboardEnabled = true };
        desktop.Hardware.Add(new LibreHardwareMonitor.Hardware.FakeHardware("Motherboard", [superIo], []));
        var route = FanChannelRoute.For(FanPlatform.Windows, new FanCoreOptions(LibreHardwareMonitorComputer: desktop))
            .Single(entry => entry.Category == FanChannelCategory.SuperIo);
        using (var backend = route.Create())
        {
            Check(backend.TryOpen(out _), "desktop header found through the route option");
            Check(superIo.Updates == 0, "discovery reads the host tree without updating it");
            Check(backend.Describe().Single().Name == "Fan Control #1", "header collected from the IList hardware tree");
            Check(backend.TrySetDuty(0, 45, out _) && control.Software == 45, "write reaches the shared control");
            Check(backend.Read().Single().Rpm == 900 && superIo.Updates == 0,
                "read takes the host-updated values without updating the host's tree from another thread");
        }
        Check(desktop.Opens == 0 && desktop.Closes == 0, "disposal leaves the host's computer open");

        var disabled = new LibreHardwareMonitor.Hardware.Computer { IsMotherboardEnabled = false };
        using (var backend = new FanControlCore.Protocols.DesktopSuperIoFanBackend(disabled))
            Check(!backend.TryOpen(out var reason) && reason!.Contains("没有启用主板") && disabled.Opens == 0, "motherboard group not enabled by us");
        using (var backend = new FanControlCore.Protocols.DesktopSuperIoFanBackend(new object()))
            Check(!backend.TryOpen(out var reason) && reason!.Contains("Computer"), "wrong object rejected");
        // H3: no host Computer, no Super I/O route. The library never opens one of its own.
        using (var backend = new FanControlCore.Protocols.DesktopSuperIoFanBackend(null))
            Check(!backend.TryOpen(out var kind, out var reason) && kind == FanOpenFailureKind.NoSupportedController
                && reason!.Contains("LibreHardwareMonitor Computer"), "without a host Computer the route is unavailable");
    }

    /// <summary>H3: notebook EC routes (Uniwill raw EC, vendor firmware such as Acer) are tried before the generic Super I/O route.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void SuperIoRouteIsLast()
    {
        var routes = FanChannelRoute.For(FanPlatform.Windows, new FanCoreOptions()).Select(route => route.Category).ToArray();
        var superIo = Array.IndexOf(routes, FanChannelCategory.SuperIo);
        Check(superIo == routes.Length - 1, "Super I/O is the last route");
        Check(Array.LastIndexOf(routes, FanChannelCategory.RawEc) is >= 0 and var rawEc && rawEc < superIo
            && Array.LastIndexOf(routes, FanChannelCategory.VendorFirmware) < superIo, "raw EC and vendor firmware come first");
    }

    private sealed class Backend : IFanBackend
    {
        public string Name => "review-fix-fixture";
        public int Drives, Releases, Blocked;
        public volatile bool FailDrive, FailRelease, FailSet;
        public bool Available = true;
        public ManualResetEventSlim? BlockDrive;
        public bool TryOpen(out string? reason) { reason = Available ? null : "fixture unavailable"; return Available; }
        public IReadOnlyList<FanDescription> Describe() =>
        [
            new(0, "CPU", true, true, FanRoles.Cpu, SupportsSoftwareCurve: true),
            new(1, "GPU", true, false, FanRoles.Gpu, SupportsSoftwareCurve: true),
            new(2, "Other", true, true, FanRoles.Unknown, SupportsSoftwareCurve: true)
        ];
        public IReadOnlyList<FanState> Read() => [];
        public bool TryDriveDuty(int fan, double percent, out string? reason)
        {
            if (BlockDrive is { } block) { Interlocked.Increment(ref Blocked); block.Wait(TimeSpan.FromSeconds(10)); }
            Interlocked.Increment(ref Drives);
            reason = FailDrive ? "fixture drive failure" : null;
            return !FailDrive;
        }
        public bool TrySetDuty(int fan, double percent, out string? reason) { reason = FailSet ? "fixture set failure" : null; return !FailSet; }
        public bool TrySetFullSpeed(int fan, out string? reason) { reason = null; return true; }
        public bool TrySetAutomatic(int fan, out string? reason)
        {
            Interlocked.Increment(ref Releases);
            reason = FailRelease ? "fixture release failure" : null;
            return !FailRelease;
        }
        public void Dispose() { }
    }

    /// <summary>A covers-all channel that counts holders (Uniwill semantics): the shared mode clears with its last holder.</summary>
    private sealed class SharedFlagBackend : IFanBackend
    {
        public string Name => "shared-flag-fixture";
        public readonly int[] Drives = new int[2];
        private readonly HashSet<int> holders = [];
        public bool Shared { get { lock (holders) return holders.Count > 0; } }
        public bool SharedTakeoverCountsHolders => true;
        public IReadOnlyCollection<int> SharedTakeoverHolders { get { lock (holders) return holders.ToArray(); } }
        public bool TryOpen(out string? reason) { reason = null; return true; }
        public IReadOnlyList<FanDescription> Describe() =>
        [
            new(0, "A", true, true, FanRoles.Cpu, SupportsSoftwareCurve: true, SoftwareTakeoverCoversAllFans: true),
            new(1, "B", true, true, FanRoles.Gpu, SupportsSoftwareCurve: true, SoftwareTakeoverCoversAllFans: true)
        ];
        public IReadOnlyList<FanState> Read() => [];
        public bool TryDriveDuty(int fan, double percent, out string? reason)
        { lock (holders) holders.Add(fan); Interlocked.Increment(ref Drives[fan]); reason = null; return true; }
        public bool TrySetDuty(int fan, double percent, out string? reason) { reason = null; return true; }
        public bool TrySetFullSpeed(int fan, out string? reason) { reason = null; return true; }
        public bool TrySetAutomatic(int fan, out string? reason) { lock (holders) holders.Remove(fan); reason = null; return true; }
        public void Dispose() { }
    }

    /// <summary>
    /// Acer semantics: one machine-wide custom mode, no holder counting, any release returns the whole machine to
    /// automatic; one control group whose fixed duty is itself the machine mode.
    /// </summary>
    private sealed class MachineModeBackend : IFanBackend
    {
        public string Name => "machine-mode-fixture";
        public readonly int[] Drives = new int[2];
        public readonly int[] Releases = new int[2];
        public readonly double[] Last = new double[2];
        public int Sets, Rejections;
        private int custom;
        public bool Custom => Volatile.Read(ref custom) != 0;
        public IReadOnlyList<FanControlGroup> ControlGroups =>
            [new("machine", [0, 1], FanControlGroupReason.AcerMachineMode, FirmwareFixedDuty: false)];
        public bool TryOpen(out string? reason) { reason = null; return true; }
        // Acer's custom duty takes 20 to 100 only (AcerWmiFanBackend.TrySetDuty), so it cannot stop a fan.
        public IReadOnlyList<FanDescription> Describe() =>
        [
            new(0, "CPU", true, true, FanRoles.Cpu, SupportsSoftwareCurve: true, SoftwareTakeoverCoversAllFans: true,
                MinimumOnPercent: 20, CanStop: false),
            new(1, "GPU", true, true, FanRoles.Gpu, SupportsSoftwareCurve: true, SoftwareTakeoverCoversAllFans: true,
                MinimumOnPercent: 20, CanStop: false)
        ];
        public IReadOnlyList<FanState> Read() => [];
        public bool TryDriveDuty(int fan, double percent, out string? reason)
        {
            if (percent is < 20 or > 100)
            {
                Interlocked.Increment(ref Rejections);
                reason = "duty must be 20 to 100";
                return false;
            }
            Volatile.Write(ref custom, 1);
            Volatile.Write(ref Last[fan], percent);
            Interlocked.Increment(ref Drives[fan]);
            reason = null;
            return true;
        }
        public bool TrySetDuty(int fan, double percent, out string? reason) { Interlocked.Increment(ref Sets); Volatile.Write(ref custom, 1); reason = null; return true; }
        public bool TrySetFullSpeed(int fan, out string? reason) { Interlocked.Increment(ref Sets); Volatile.Write(ref custom, 1); reason = null; return true; }
        /// <summary>A fan whose release throws, like a transport fault mid-call; -1 for none.</summary>
        public int ThrowAutomaticFor = -1;
        public bool TrySetAutomatic(int fan, out string? reason)
        {
            if (fan == Volatile.Read(ref ThrowAutomaticFor)) throw new InvalidOperationException("fixture transport fault");
            Volatile.Write(ref custom, 0); Interlocked.Increment(ref Releases[fan]); reason = null; return true;
        }
        public void Dispose() { }
    }
}

namespace LibreHardwareMonitor.Hardware
{
    /// <summary>Reflection stand-in for LibreHardwareMonitor's Computer: counts lifecycle calls, owns an IList tree.</summary>
    internal sealed class Computer
    {
        public bool IsMotherboardEnabled { get; set; }
        public IList<object> Hardware { get; } = new List<object>();
        public int Opens, Closes;
        public void Open() => Opens++;
        public void Close() => Closes++;
    }

    internal sealed class FakeHardware(string type, FakeHardware[] children, FakeSensor[] sensors)
    {
        public string HardwareType => type;
        public FakeHardware[] SubHardware => children;
        public FakeSensor[] Sensors => sensors;
        public int Updates;
        public void Update() => Updates++;
    }

    internal sealed class FakeSensor(string type, int index, string name, FakeControl? control)
    {
        public string SensorType => type;
        public int Index => index;
        public string Name => name;
        public FakeControl? Control => control;
        public float? Value { get; set; }
    }

    internal sealed class FakeControl
    {
        public float? Software;
        public string ControlMode => Software is null ? "Default" : "Software";
        public float MinSoftwareValue => 0;
        public float MaxSoftwareValue => 100;
        public FakeSensor? Sensor => null;
        public void SetSoftware(float value) => Software = value;
        public void SetDefault() => Software = null;
    }
}
