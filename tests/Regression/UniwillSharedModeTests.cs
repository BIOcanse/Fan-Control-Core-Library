using System.Runtime.Versioning;
using System.Text.Json;
using FanControlCore;
using FanControlCore.Discovery;
using FanControlCore.Policy;
using FanControlCore.Profiles;
using FanControlCore.Protocols;
using FanControlCore.Transport;

/// <summary>
/// The Uniwill control group and the machine-wide full-fan-mode bit (0x0751 / 0x40), with a thread-safe fake EC that
/// models the bit, both duty registers and both firmware tables. No hardware is touched.
///
/// Both fans form one control group: software versus firmware control switches for the whole group in one call; a
/// per-fan call that needs the other mode is refused with a typed error naming the group; a safety hand-back returns
/// the whole group. Backend-level holder counting stays as defense for direct backend and engine use.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class UniwillSharedModeTests
{
    private const string Device = "6f1c4a52-1d0e-4d77-9f0b-5b3f1a2c9e01";
    private const string Machine = "uniwill-shared-mode-test";
    private const string Group = "uniwill-shared-mode";
    private static readonly FanCurvePoint[] Curve = [new(0, 0), new(100, 100)];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception("Uniwill shared mode: " + message); }
    private static void Until(Func<bool> ready, string message, int milliseconds = 4000)
        => Check(SpinWait.SpinUntil(ready, milliseconds), message);

    internal static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "fcc-uniwill-shared-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            GroupDescribe(Path.Combine(root, "describe"));
            PerFanCallsNameTheGroup(Path.Combine(root, "per-fan"));
            GroupSwitchIsAtomic(Path.Combine(root, "atomic"));
            BlindHandBackReturnsWholeGroup(Path.Combine(root, "blind"));
            FailedDriveReturnsWholeGroup(Path.Combine(root, "failed"));
            StaggeredRecoveryWaitsForWholeGroup(Path.Combine(root, "staggered"));
            PendingHandBackBlocksTakeover(Path.Combine(root, "pending"));
            FailedFirmwareSwitchListsOnlyFirmwareFans(Path.Combine(root, "firmware-pending"));
            PendingMemberKeepsLaterFixedSpeed(Path.Combine(root, "pending-fixed"));
            HandBackFaultsKeepGroupResult(Path.Combine(root, "faults"));
            EngineHeldRefusalEndsWholeTakeover(Path.Combine(root, "held"));
            FixedAndBoostInsideSoftwareGroup(Path.Combine(root, "fixed"));
            GroupSwitchHandsBackFixedTables(Path.Combine(root, "tables"));
            TruthfulReadsAndFloor(Path.Combine(root, "reads"));
            EngineDeadZoneUsesFloor();
            TypedBackendFailures(Path.Combine(root, "kinds"));
            StableRecoveryIdentity(Path.Combine(root, "identity"));
            EngineRefusesOverFixedTable(Path.Combine(root, "engine"));
            FailedClearKeepsHolder(Path.Combine(root, "clear"));
            ReadDoesNotWaitForTableWrites(Path.Combine(root, "read"));
            ThrowingBoostKeepsHolder(Path.Combine(root, "throw"));
        }
        finally { DeleteFlat(root); }
        Console.WriteLine("Uniwill shared mode: control group describe, per-fan refusals naming the group, atomic group switch, whole-group safety hand-back, pending last release, handedBack only under firmware, a pending member's later fixed speed kept, typed hand-back faults, held refusal ending the takeover, fixed and boost inside the group, table hand-back on switch, reads, floor, typed failures, stable recovery identity and backend holder counting passed.");
    }

    private static FanCoreOptions Options(string directory) => new(directory, MachineIdentity: () => Machine);

    private static FanCore Core(SharedModeHardware hardware, string directory)
    {
        var core = new FanCore(new UniwillWmiFanBackend(hardware, directory, () => Device), Options(directory));
        Check(core.Open(), "fixture opens");
        return core;
    }

    private static JsonElement Send(FanCore core, string request) => JsonSerializer.SerializeToElement(FanCommandServer.Handle(core, request), Json);

    private static JsonElement Send(FanCore core, object request) => Send(core, JsonSerializer.Serialize(request, Json));

    private static bool Ok(JsonElement reply) => reply.GetProperty("ok").GetBoolean();

    private static string? Kind(JsonElement reply)
        => reply.TryGetProperty("errorKind", out var kind) && kind.ValueKind == JsonValueKind.String ? kind.GetString() : null;

    private static string? NamedGroup(JsonElement reply)
        => reply.TryGetProperty("controlGroup", out var group) && group.ValueKind == JsonValueKind.String ? group.GetString() : null;

    private static string? Control(JsonElement reply) => reply.GetProperty("controlGroups")[0].GetProperty("control").GetString();

    private static JsonElement Switch(FanCore core, string control, params object[] fans)
        => Send(core, new { op = "group", group = Group, control, fans });

    private static bool? Automatic(JsonElement reply, int fan)
    {
        foreach (var entry in reply.GetProperty("fans").EnumerateArray())
            if (entry.GetProperty("index").GetInt32() == fan)
            {
                var automatic = entry.GetProperty("automaticControl");
                return automatic.ValueKind == JsonValueKind.Null ? null : automatic.GetBoolean();
            }
        throw new Exception("fan missing from reply");
    }

    /// <summary>Pushes host temperatures every 100 ms; a null value is a missing sensor.</summary>
    private sealed class Pusher : IDisposable
    {
        private readonly Timer timer;
        private double? cpu, gpu;
        internal Pusher(FanCore core, double? cpu, double? gpu)
        {
            (this.cpu, this.gpu) = (cpu, gpu);
            timer = new Timer(_ =>
            {
                var now = DateTime.UtcNow.Ticks;
                var samples = new List<FanTemperatureSample>();
                lock (this)
                {
                    if (this.cpu is { } c) samples.Add(new("cpu", c, now));
                    if (this.gpu is { } g) samples.Add(new("gpu", g, now));
                }
                core.PushTemperatures(samples);
            }, null, 0, 100);
        }
        internal double? Cpu { set { lock (this) cpu = value; } }
        internal double? Gpu { set { lock (this) gpu = value; } }
        public void Dispose()
        {
            using var done = new ManualResetEvent(false);
            timer.Dispose(done);
            done.WaitOne(2000);
        }
    }

    /// <summary>Describe lists the group with its members, typed reason, whether a fixed duty stays firmware-executed, and its mode.</summary>
    private static void GroupDescribe(string directory)
    {
        var hardware = new SharedModeHardware();
        using var core = Core(hardware, directory);
        var groups = Send(core, new { op = "describe" }).GetProperty("controlGroups");
        Check(groups.GetArrayLength() == 1, "one control group");
        var group = groups[0];
        Check(group.GetProperty("id").GetString() == Group
            && group.GetProperty("fans").EnumerateArray().Select(fan => fan.GetInt32()).SequenceEqual([0, 1])
            && group.GetProperty("reason").GetString() == "UniwillSharedModeBit"
            && group.GetProperty("firmwareFixedDuty").GetBoolean()
            && group.GetProperty("control").GetString() == "Firmware", "group shape: id, fans, typed reason, firmwareFixedDuty, control");
        Check(core.ControlGroupOf(0)?.Id == Group && core.ControlGroupOf(1)?.Id == Group, "both fans belong to it");
        Check(core.GroupControl(Group) == FanGroupControl.Firmware && core.GroupControl("nope") is null, "a new session starts under firmware");
        Check(hardware.TotalWrites == 0, "describe writes nothing");
    }

    /// <summary>
    /// Per-fan calls that need the other control mode are refused with ControlGroupSwitchRequired naming the group and
    /// change nothing. Inside a software group each fan keeps its own curve or fixed speed.
    /// </summary>
    private static void PerFanCallsNameTheGroup(string directory)
    {
        var hardware = new SharedModeHardware();
        using var core = Core(hardware, directory);
        using var temperatures = new Pusher(core, 40, 60);

        var boost = Send(core, new { op = "boost", fan = 0 });
        Check(Kind(boost) == "ControlGroupSwitchRequired" && NamedGroup(boost) == Group
            && boost.GetProperty("error").GetString()!.Contains(Group), "boost under firmware is refused and names the group");
        var software = Send(core, new { op = "setcurve", fan = 1, execution = "software", curve = Curve });
        Check(Kind(software) == "ControlGroupSwitchRequired" && NamedGroup(software) == Group, "a per-fan software curve is refused");
        Check(!core.TrySetFullSpeed(1, out var legacyReason) && legacyReason!.Contains(Group), "the old untyped overload refuses too");
        Check(hardware.TotalWrites == 0 && !core.SoftwarePlanRecorded(0) && !core.SoftwarePlanRecorded(1), "refusals changed nothing");
        Check(Ok(Send(core, new { op = "set", fan = 0, percent = 50 })), "a fixed duty under firmware stays a firmware table here");
        Check(Ok(Send(core, new { op = "auto", fan = 0 })), "and per-fan auto hands that table back");

        Check(Ok(Switch(core, "software", new { fan = 0, curve = Curve }, new { fan = 1, curve = Curve })), "group to software");
        var auto = Send(core, new { op = "auto", fan = 0 });
        Check(Kind(auto) == "ControlGroupSwitchRequired" && NamedGroup(auto) == Group, "per-fan auto in a software group is refused");
        Check(Kind(Send(core, new { op = "auto", fan = 1, resetFirmwareCurve = true })) == "ControlGroupSwitchRequired", "a curve reset too");
        var tableWrites = hardware.WritesIn(UniwillFanTable.CpuEndTemperature, 0x60);
        var firmware = Send(core, new { op = "setcurve", fan = 1, execution = "firmware", curve = Curve });
        Check(Kind(firmware) == "ControlGroupSwitchRequired" && NamedGroup(firmware) == Group, "a firmware curve is refused");
        Check(hardware.WritesIn(UniwillFanTable.CpuEndTemperature, 0x60) == tableWrites, "no table write");
        Check(core.SoftwarePlanRecorded(0) && core.SoftwarePlanRecorded(1), "both plans are untouched");
        var own = Send(core, new { op = "setcurve", fan = 1, execution = "software", curve = new FanCurvePoint[] { new(0, 30), new(100, 90) } });
        Check(Ok(own) && core.ReadCurve(1)![0].Percent == 30, "each fan can still change its own curve");

        var back = Switch(core, "firmware");
        Check(Ok(back) && Control(back) == "Firmware", "group back to firmware");
        Check(!core.SoftwarePlanRecorded(0) && !core.SoftwarePlanRecorded(1), "returning to firmware ends every member's plan");
        Check(Kind(Send(core, new { op = "group", group = "nope", control = "software" })) == "InvalidRequest", "unknown group");
        var manual = Send(core, new { op = "group", group = Group, control = "manual" });
        Check(Kind(manual) == "InvalidRequest" && NamedGroup(manual) == Group, "unknown control; the reply still names the group");
        Check(manual.TryGetProperty("handedBack", out var none) && none.ValueKind == JsonValueKind.Array && none.GetArrayLength() == 0,
            "round-5 L-4: a rejection before the switch ran lists an empty handedBack");
        Check(Kind(Send(core, new { op = "group", control = "firmware" })) == "InvalidRequest", "missing group");
    }

    /// <summary>
    /// A group switch validates every assignment first and then records all plans as one change. Recording touches no
    /// hardware; the run entry takes the whole group over in one tick.
    /// </summary>
    private static void GroupSwitchIsAtomic(string directory)
    {
        var hardware = new SharedModeHardware();
        using var core = Core(hardware, directory);
        using var temperatures = new Pusher(core, 50, 60);
        var bad = new (string Why, object[] Fans)[]
        {
            ("only one member", [new { fan = 0, percent = 40 }]),
            ("no members", []),
            ("a member twice", [new { fan = 0, percent = 40 }, new { fan = 0, percent = 50 }]),
            ("a fan outside the group", [new { fan = 0, percent = 40 }, new { fan = 2, percent = 50 }]),
            ("both percent and curve", [new { fan = 0, percent = 40 }, new { fan = 1, percent = 40, curve = Curve }]),
            ("neither percent nor curve", [new { fan = 0, percent = 40 }, new { fan = 1 }]),
            ("an empty curve", [new { fan = 0, percent = 40 }, new { fan = 1, curve = Array.Empty<FanCurvePoint>() }]),
        };
        foreach (var (why, fans) in bad)
        {
            var refused = Switch(core, "software", fans);
            Check(Kind(refused) == "InvalidRequest" && NamedGroup(refused) == Group, $"{why} is refused and names the group");
            Check(!core.SoftwarePlanRecorded(0) && !core.SoftwarePlanRecorded(1) && core.GroupControl(Group) == FanGroupControl.Firmware,
                $"{why}: no plan recorded and the group stays under firmware");
        }
        var notArray = Send(core, """{"op":"group","group":"uniwill-shared-mode","control":"software","fans":{"fan":0}}""");
        Check(Kind(notArray) == "InvalidRequest" && NamedGroup(notArray) == Group, "fans must be an array; the reply names the group");
        var notInteger = Send(core, """{"op":"group","group":"uniwill-shared-mode","control":"software","fans":[{"fan":0.5,"percent":1},{"fan":1,"percent":1}]}""");
        Check(Kind(notInteger) == "InvalidRequest" && NamedGroup(notInteger) == Group, "fan must be an integer; the reply names the group");
        Check(Kind(Switch(core, "software", new { fan = 0, percent = 140 }, new { fan = 1, percent = 40 })) == "InvalidRequest",
            "a fixed speed above 100 % is refused");
        Check(hardware.TotalWrites == 0, "refused switches wrote nothing");

        var applied = Switch(core, "Software", new { fan = 0, curve = Curve }, new { fan = 1, percent = 40 });
        Check(Ok(applied) && Control(applied) == "Software", "valid switch applies; describe's casing is accepted");
        Check(core.SoftwarePlanRecorded(0) && core.SoftwarePlanRecorded(1), "both plans recorded by the one call");
        Check(applied.GetProperty("softwareCurveRunNeeded").GetBoolean()
            && applied.GetProperty("softwareCurveTemperatureKinds").EnumerateArray().Select(kind => kind.GetString()).SequenceEqual(["cpu"]),
            "receipt carries the run facts; the fixed member needs no temperature");
        Check(hardware.WritesTo(UniwillFanProfileTable.FanModeRegister) == 0, "the switch itself writes nothing; the run takes over");
        core.CurveEngine.BeforeIteration = hardware.MarkTick;
        using (var run = new CurveRun(core.RunSoftwareCurveAsync))
        {
            Until(() => hardware.ModeBit && hardware[UniwillFanProfileTable.SecondFanDuty] == UniwillFanProfileTable.DutyFromPercent(40)
                && hardware[UniwillFanProfileTable.MainFanDuty] == UniwillFanProfileTable.DutyFromPercent(50), "both members driven");
            Check(hardware.TakeoverStartedInOneTick(UniwillFanProfileTable.MainFanDuty, UniwillFanProfileTable.SecondFanDuty),
                "the takeover started for both members within one run tick");

            var replace = Switch(core, "software", new { fan = 0, percent = 30 });
            Check(Kind(replace) == "InvalidRequest" && core.ReadCurve(1)![0].Percent == 40 && core.ReadCurve(0)!.Count == 2,
                "an invalid replacement changes no plan");
            Check(Ok(Switch(core, "software", new { fan = 0, percent = 30 }, new { fan = 1, curve = Curve })), "whole-group replacement");
            Until(() => hardware[UniwillFanProfileTable.MainFanDuty] == UniwillFanProfileTable.DutyFromPercent(30)
                && hardware[UniwillFanProfileTable.SecondFanDuty] == UniwillFanProfileTable.DutyFromPercent(60), "replacement drives both");

            var back = Switch(core, "firmware");
            Check(Ok(back) && Control(back) == "Firmware", "group back to firmware");
            Until(() => !hardware.ModeBit && !core.CurveEngine.HasRuntime(0) && !core.CurveEngine.HasRuntime(1),
                "both handed back and the bit cleared");
            Check(core.Read().All(fan => fan.AutomaticControl == true), "both automatic");
            Check(Ok(Switch(core, "firmware")), "firmware again is a no-op");
        }
        Check(!hardware.ModeBit, "run end leaves the bit cleared");
    }

    /// <summary>
    /// H1: a blind member hands the whole group back to firmware, with the other plans suspended as SharedHandBack.
    /// The applied group mode stays software; the plans resume by themselves once the temperature returns.
    /// </summary>
    private static void BlindHandBackReturnsWholeGroup(string directory)
    {
        var hardware = new SharedModeHardware();
        using var core = Core(hardware, directory);
        core.CurveEngine.BlindTimeout = TimeSpan.FromMilliseconds(400);
        core.CurveEngine.BeforeIteration = hardware.MarkTick;
        using var temperatures = new Pusher(core, 40, 60);
        Check(Ok(Switch(core, "software", new { fan = 0, curve = Curve }, new { fan = 1, curve = Curve })), "group to software");
        var cpuDuty = UniwillFanProfileTable.DutyFromPercent(40);
        var gpuDuty = UniwillFanProfileTable.DutyFromPercent(60);
        using (var run = new CurveRun(core.RunSoftwareCurveAsync))
        {
            Until(() => hardware[UniwillFanProfileTable.MainFanDuty] == cpuDuty
                && hardware[UniwillFanProfileTable.SecondFanDuty] == gpuDuty && hardware.ModeBit, "both fans driven");
            Check(core.ExecutionOf(0) == FanCurveExecutions.Software && core.ExecutionOf(1) == FanCurveExecutions.Software,
                "both receipts report software");
            Check(core.Read().All(fan => fan.AutomaticControl == false) && hardware.Effective(1) == "duty-register",
                "shared bit set: no firmware table runs for either fan");

            temperatures.Gpu = null;
            Until(() => core.SoftwareCurveSuspension(1) == FanCurveSuspension.Blind
                && core.SoftwareCurveSuspension(0) == FanCurveSuspension.SharedHandBack
                && !core.CurveEngine.HasRuntime(0) && !core.CurveEngine.HasRuntime(1),
                "GPU blind ends the whole group's takeover; the CPU plan is suspended with a reason");
            Check(!hardware.ModeBit && hardware.Effective(0) == "firmware" && hardware.Effective(1) == "firmware",
                "bit cleared: both fans are under firmware control, none is left at a software value");
            Check(core.ExecutionOf(0) is null && core.ExecutionOf(1) is null, "neither receipt says software");
            Check(core.SoftwarePlanRecorded(0) && core.SoftwarePlanRecorded(1), "both plans are kept");
            Check(core.SoftwareCurveTemperatureKinds.SequenceEqual(["cpu", "gpu"]),
                "both suspended plans keep their kinds, so the host keeps pushing and they can resume");
            var cpuWrites = hardware.WritesTo(UniwillFanProfileTable.MainFanDuty);
            Thread.Sleep(400);
            Check(hardware.WritesTo(UniwillFanProfileTable.MainFanDuty) == cpuWrites && !hardware.ModeBit,
                "the CPU plan does not take the group back while the GPU plan is blind");
            Check(Send(core, new { op = "curve", fan = 1 }).GetProperty("softwareCurveSuspension").GetString() == "Blind"
                && Send(core, new { op = "curve", fan = 0 }).GetProperty("softwareCurveSuspension").GetString() == "SharedHandBack",
                "curve receipts carry the typed suspensions");
            var describe = Send(core, new { op = "describe" });
            Check(describe.GetProperty("softwareCurveSuspensions").GetProperty("1").GetString() == "Blind"
                && describe.GetProperty("softwareCurveSuspensions").GetProperty("0").GetString() == "SharedHandBack"
                && Control(describe) == "Software", "describe lists both suspensions; the applied group mode stays software");

            var gpuWrites = hardware.WritesTo(UniwillFanProfileTable.SecondFanDuty);
            var resumeMark = hardware.WriteMark;
            temperatures.Gpu = 60;
            Until(() => core.SoftwareCurveSuspension(1) is null && core.SoftwareCurveSuspension(0) is null
                && core.ExecutionOf(0) == FanCurveExecutions.Software && core.ExecutionOf(1) == FanCurveExecutions.Software,
                "once the GPU temperature returns both plans resume by themselves");
            Until(() => hardware.WritesTo(UniwillFanProfileTable.SecondFanDuty) > gpuWrites
                && hardware[UniwillFanProfileTable.SecondFanDuty] == gpuDuty
                && hardware[UniwillFanProfileTable.MainFanDuty] == cpuDuty && hardware.ModeBit, "both fans driven again");
            Check(hardware.TakeoverStartedInOneTick(UniwillFanProfileTable.MainFanDuty, UniwillFanProfileTable.SecondFanDuty, resumeMark),
                "the group resumed as a whole within one run tick, not one member first");

            Check(Ok(Switch(core, "firmware")), "group back to firmware");
            Until(() => !hardware.ModeBit && !core.CurveEngine.HasRuntime(0) && !core.CurveEngine.HasRuntime(1), "handed back");
        }
        Check(!hardware.ModeBit, "run end leaves the bit cleared");
    }

    /// <summary>H1 for a failed member: the whole group goes back to firmware and waits until the group is applied again.</summary>
    private static void FailedDriveReturnsWholeGroup(string directory)
    {
        var hardware = new SharedModeHardware();
        using var core = Core(hardware, directory);
        using var temperatures = new Pusher(core, 40, 60);
        Check(Ok(Switch(core, "software", new { fan = 0, curve = Curve }, new { fan = 1, curve = Curve })), "group to software");
        using (var run = new CurveRun(core.RunSoftwareCurveAsync))
        {
            Until(() => hardware.ModeBit && hardware[UniwillFanProfileTable.SecondFanDuty] == UniwillFanProfileTable.DutyFromPercent(60)
                && hardware[UniwillFanProfileTable.MainFanDuty] == UniwillFanProfileTable.DutyFromPercent(40), "both driven");
            hardware.Change(fake => fake.Rejected.Add(UniwillFanProfileTable.SecondFanDuty));
            temperatures.Gpu = 90; // a new target, so the GPU register is written and rejected
            Until(() => core.SoftwareCurveSuspension(1) == FanCurveSuspension.Failed
                && core.SoftwareCurveSuspension(0) == FanCurveSuspension.SharedHandBack
                && !core.CurveEngine.HasRuntime(0) && !core.CurveEngine.HasRuntime(1) && !hardware.ModeBit,
                "the failed GPU fan ends the whole group's takeover; the CPU plan is suspended with a reason");
            Thread.Sleep(400);
            Check(!hardware.ModeBit && core.SoftwareCurveSuspension(0) == FanCurveSuspension.SharedHandBack,
                "a failed member keeps the group under firmware until the group is applied again");
            hardware.Change(fake => fake.Rejected.Clear());
            Check(Ok(Switch(core, "software", new { fan = 0, curve = Curve }, new { fan = 1, curve = Curve })), "group applied again");
            Until(() => hardware.ModeBit && core.SoftwareCurveSuspension(0) is null && core.SoftwareCurveSuspension(1) is null
                && hardware[UniwillFanProfileTable.SecondFanDuty] == UniwillFanProfileTable.DutyFromPercent(90), "both run again");
            Check(Ok(Switch(core, "firmware")), "cleanup");
        }
        Check(!hardware.ModeBit, "run end leaves the bit cleared");
    }

    /// <summary>
    /// Round-3 H1: GPU goes blind (whole group handed back), then the CPU temperature disappears too, then the GPU
    /// temperature returns. The GPU alone must not take the shared bit: the CPU fan would leave firmware control with no
    /// runtime driving it or watching its temperature. Only when the CPU temperature is back does the group restart,
    /// as a whole and within one tick.
    /// </summary>
    private static void StaggeredRecoveryWaitsForWholeGroup(string directory)
    {
        var hardware = new SharedModeHardware();
        using var core = Core(hardware, directory);
        core.CurveEngine.BlindTimeout = TimeSpan.FromMilliseconds(300);
        core.CurveEngine.BeforeIteration = hardware.MarkTick;
        using var temperatures = new Pusher(core, 40, 60);
        Check(Ok(Switch(core, "software", new { fan = 0, curve = Curve }, new { fan = 1, curve = Curve })), "group to software");
        using (var run = new CurveRun(core.RunSoftwareCurveAsync))
        {
            Until(() => hardware.ModeBit && hardware[UniwillFanProfileTable.SecondFanDuty] == UniwillFanProfileTable.DutyFromPercent(60), "driven");
            temperatures.Gpu = null;
            Until(() => core.SoftwareCurveSuspension(1) == FanCurveSuspension.Blind
                && core.SoftwareCurveSuspension(0) == FanCurveSuspension.SharedHandBack && !hardware.ModeBit
                && !core.CurveEngine.HasRuntime(0) && !core.CurveEngine.HasRuntime(1), "GPU blind: whole group handed back");
            temperatures.Cpu = null;
            Thread.Sleep(300);
            var mark = hardware.WriteMark;
            temperatures.Gpu = 60;
            Thread.Sleep(800);
            Check(hardware.BitSetWrites(mark) == 0 && !hardware.ModeBit, "no shared-bit write while the CPU member is not ready");
            Check(!core.CurveEngine.HasRuntime(0) && !core.CurveEngine.HasRuntime(1), "neither member was started");
            Check(core.SoftwareCurveSuspension(0) == FanCurveSuspension.SharedHandBack && core.SoftwareCurveSuspension(1) is not null,
                "both plans stay suspended with a reason");
            temperatures.Cpu = 40;
            Until(() => hardware.ModeBit && core.ExecutionOf(0) == FanCurveExecutions.Software
                && core.ExecutionOf(1) == FanCurveExecutions.Software, "with every member ready the group restarts");
            Check(hardware.TakeoverStartedInOneTick(UniwillFanProfileTable.MainFanDuty, UniwillFanProfileTable.SecondFanDuty, mark),
                "the restart took both members within one tick");
            Check(Ok(Switch(core, "firmware")), "cleanup");
        }
    }

    /// <summary>
    /// Round-3 H1, failed hand-back, in the order round 4 (T-1) asked for: the CPU member (fan 0) goes blind, so the
    /// group is released in running order. Fan 0's release only gives up its share; fan 1 (SharedHandBack) is the last
    /// holder, and its bit-clear is rejected and stays pending, so fan 1 is still in our hands. When the CPU temperature
    /// returns every temperature is fresh, yet fan 0 must not take the bit alone (the round-3 code let it, through the
    /// per-fan Blind path). Once fan 1's release succeeds the group restarts as a whole.
    /// </summary>
    private static void PendingHandBackBlocksTakeover(string directory)
    {
        var hardware = new SharedModeHardware();
        using var core = Core(hardware, directory);
        core.CurveEngine.BlindTimeout = TimeSpan.FromMilliseconds(300);
        core.CurveEngine.BeforeIteration = hardware.MarkTick;
        using var temperatures = new Pusher(core, 40, 60);
        Check(Ok(Switch(core, "software", new { fan = 0, curve = Curve }, new { fan = 1, curve = Curve })), "group to software");
        using (var run = new CurveRun(core.RunSoftwareCurveAsync))
        {
            Until(() => hardware.ModeBit && hardware[UniwillFanProfileTable.SecondFanDuty] == UniwillFanProfileTable.DutyFromPercent(60)
                && hardware[UniwillFanProfileTable.MainFanDuty] == UniwillFanProfileTable.DutyFromPercent(40), "driven");
            Thread.Sleep(300); // settled: no more drives, so rejecting the mode register only affects the release
            hardware.Change(fake => fake.Rejected.Add(UniwillFanProfileTable.FanModeRegister));
            temperatures.Cpu = null;
            Until(() => core.SoftwareCurveSuspension(0) == FanCurveSuspension.Blind
                && core.SoftwareCurveSuspension(1) == FanCurveSuspension.SharedHandBack
                && !core.CurveEngine.HasRuntime(0) && core.CurveEngine.HasRuntime(1),
                "fan 0's release gave up its share; fan 1's release, the last holder, fails and stays pending");
            Check(hardware.ModeBit, "the bit is still set: fan 1 is not back under firmware");
            var mark = hardware.WriteMark;
            temperatures.Cpu = 40;
            Thread.Sleep(800);
            Check(hardware.BitSetWrites(mark) == 0 && !core.CurveEngine.HasRuntime(0),
                "fan 0's temperature is back, but it does not take the bit while fan 1's hand-back is pending");
            Check(core.SoftwareCurveSuspension(0) is not null && core.SoftwareCurveSuspension(1) == FanCurveSuspension.SharedHandBack
                && core.SoftwareCurveRunNeeded, "the group waits, visibly; the run is still needed for the retry");
            hardware.Change(fake => fake.Rejected.Clear());
            Until(() => hardware.ModeBit && core.ExecutionOf(0) == FanCurveExecutions.Software
                && core.ExecutionOf(1) == FanCurveExecutions.Software, "after the retry succeeds the group restarts", 10000);
            Check(hardware.TakeoverStartedInOneTick(UniwillFanProfileTable.MainFanDuty, UniwillFanProfileTable.SecondFanDuty, mark),
                "the restart took both members within one tick");
            Check(Ok(Switch(core, "firmware")), "cleanup");
        }
    }

    /// <summary>
    /// Round-4 M-B: a switch to firmware whose last bit-clear (fan 1's) fails lists nobody in handedBack. Fan 0's
    /// release only gave up its share, the shared bit is still set, and neither fan is under firmware. Both members stay
    /// pending: after the run's retry has cleared the bit, the next switch to firmware confirms both and lists both,
    /// and removes their crash-recovery records.
    /// </summary>
    private static void FailedFirmwareSwitchListsOnlyFirmwareFans(string directory)
    {
        var hardware = new SharedModeHardware();
        using var core = Core(hardware, directory);
        using var temperatures = new Pusher(core, 40, 60);
        Check(Ok(Switch(core, "software", new { fan = 0, curve = Curve }, new { fan = 1, curve = Curve })), "group to software");
        using (var run = new CurveRun(core.RunSoftwareCurveAsync))
        {
            FailFirmwareSwitchAtLastClear(core, hardware, directory);
            var retried = Switch(core, "firmware");
            Check(Ok(retried) && HandedBack(retried).SequenceEqual([0, 1])
                && Automatic(retried, 0) == true && Automatic(retried, 1) == true,
                "the next switch to firmware confirms both members and lists both");
            Check(JournalFans(directory) == 0, "no crash-recovery record is left");
            var again = Switch(core, "firmware");
            Check(Ok(again) && HandedBack(again).Length == 0, "a further switch has nothing left to hand back");
        }
    }

    /// <summary>
    /// Round-5 T-A: a pending member that gets its own fixed speed after the run's retry cleared the bit is firmware
    /// control again. The next switch to firmware hands back only the other pending member, keeps fan 0's table, its
    /// reading and its crash-recovery record.
    /// </summary>
    private static void PendingMemberKeepsLaterFixedSpeed(string directory)
    {
        var hardware = new SharedModeHardware();
        var original = hardware[UniwillFanTable.CpuFanSpeed];
        using var core = Core(hardware, directory);
        using var temperatures = new Pusher(core, 40, 60);
        Check(Ok(Switch(core, "software", new { fan = 0, curve = Curve }, new { fan = 1, curve = Curve })), "group to software");
        using (var run = new CurveRun(core.RunSoftwareCurveAsync))
        {
            FailFirmwareSwitchAtLastClear(core, hardware, directory);
            Check(Ok(Send(core, new { op = "set", fan = 0, percent = 50 }))
                && hardware[UniwillFanTable.CpuFanSpeed] == UniwillFanProfileTable.DutyFromPercent(50), "pending fan 0 gets a fixed speed");
            var switched = Switch(core, "firmware");
            Check(Ok(switched) && HandedBack(switched).SequenceEqual([1]),
                "only the other pending member is handed back and listed");
            Check(hardware[UniwillFanTable.CpuFanSpeed] == UniwillFanProfileTable.DutyFromPercent(50)
                && Automatic(switched, 0) == false && Automatic(switched, 1) == true,
                "fan 0's later fixed speed is kept, and its reading says so");
            Check(JournalFans(directory) == 1, "fan 0's crash-recovery record is kept");
            Check(Ok(Send(core, new { op = "auto", fan = 0 })) && hardware[UniwillFanTable.CpuFanSpeed] == original
                && JournalFans(directory) == 0, "cleanup: fan 0 hands its table back");
        }
    }

    /// <summary>
    /// Drives the group, then fails a switch to firmware at fan 1's bit-clear while the journal cannot be saved either.
    /// Checks M-B (nobody listed), round-5 L-3 (the reported failure is fan 1's real one, not fan 0's unrecorded release)
    /// and L-6 (a per-fan auto in that window is SharedTakeoverHeld, not ok), then lets the run's retry clear the bit.
    /// </summary>
    private static void FailFirmwareSwitchAtLastClear(FanCore core, SharedModeHardware hardware, string directory)
    {
        Until(() => hardware.ModeBit && hardware[UniwillFanProfileTable.SecondFanDuty] == UniwillFanProfileTable.DutyFromPercent(60)
            && hardware[UniwillFanProfileTable.MainFanDuty] == UniwillFanProfileTable.DutyFromPercent(40), "driven");
        Thread.Sleep(300); // settled: rejecting the mode register only affects the releases
        hardware.Change(fake => fake.Rejected.Add(UniwillFanProfileTable.FanModeRegister));
        var blocker = Path.Combine(directory, "temporary-takeovers.json.tmp");
        Directory.CreateDirectory(blocker); // fan 0's release is confirmed, but its record cannot be removed
        JsonElement failed;
        try { failed = Switch(core, "firmware"); }
        finally { Directory.Delete(blocker); }
        Check(Kind(failed) == "HardwareFailed" && !failed.GetProperty("error").GetString()!.Contains("风扇 0")
            && NamedGroup(failed) == Group && HandedBack(failed).Length == 0,
            "fan 1's bit-clear fails: that real failure is reported, not fan 0's unrecorded release, and nobody is listed");
        Check(hardware.ModeBit && Automatic(failed, 0) == false && Automatic(failed, 1) == false,
            "the shared bit is still set: neither fan is under firmware, and fans says the same");
        Check(core.GroupControl(Group) == FanGroupControl.Firmware && !core.SoftwarePlanRecorded(0) && !core.SoftwarePlanRecorded(1),
            "the group is recorded as firmware and its plans are gone");
        var auto = Send(core, new { op = "auto", fan = 0 });
        Check(Kind(auto) == "SharedTakeoverHeld" && hardware.ModeBit && Automatic(Send(core, new { op = "read" }), 0) == false,
            "a per-fan auto gives up fan 0's share but reports that fan 1 still holds the bit");
        hardware.Change(fake => fake.Rejected.Clear());
        Until(() => !hardware.ModeBit && !core.CurveEngine.HasRuntime(0) && !core.CurveEngine.HasRuntime(1),
            "the run's retry clears the bit", 10000);
    }

    /// <summary>
    /// Round-4 L-B: faults inside a group switch are typed failures that keep the group, handedBack and fans, and do not
    /// stop the remaining hand-backs. A transport fault in fan 1's table hand-back (switch to software), then a journal
    /// that cannot be saved after both hardware hand-backs (switch to firmware), which a retry completes.
    /// </summary>
    private static void HandBackFaultsKeepGroupResult(string directory)
    {
        var hardware = new SharedModeHardware();
        var original = hardware[UniwillFanTable.CpuFanSpeed];
        using var core = Core(hardware, directory);
        using var temperatures = new Pusher(core, 40, 60);
        Check(Ok(Send(core, new { op = "set", fan = 0, percent = 60 })) && Ok(Send(core, new { op = "set", fan = 1, percent = 50 })),
            "two fixed-duty tables under firmware");
        hardware.Change(fake => fake.Throwing.Add(UniwillFanTable.GpuFanSpeed));
        var faulted = Switch(core, "software", new { fan = 0, curve = Curve }, new { fan = 1, curve = Curve });
        Check(Kind(faulted) == "Other" && NamedGroup(faulted) == Group && HandedBack(faulted).SequenceEqual([0])
            && Automatic(faulted, 0) == true && Automatic(faulted, 1) == false,
            "a transport fault in fan 1's table hand-back is a typed failure with the group, handedBack and fans");
        Check(hardware[UniwillFanTable.CpuFanSpeed] == original && !core.SoftwarePlanRecorded(0) && !core.SoftwarePlanRecorded(1),
            "fan 0's table is back and no plan is recorded");
        hardware.Change(fake => fake.Throwing.Clear());
        var applied = Switch(core, "software", new { fan = 0, curve = Curve }, new { fan = 1, curve = Curve });
        Check(Ok(applied) && HandedBack(applied).SequenceEqual([1]), "after the fault the switch hands back fan 1's table");

        using (var run = new CurveRun(core.RunSoftwareCurveAsync))
        {
            Until(() => hardware.ModeBit && core.ExecutionOf(0) == FanCurveExecutions.Software
                && core.ExecutionOf(1) == FanCurveExecutions.Software, "the group runs");
            Check(JournalFans(directory) == 2, "both members are recorded for crash recovery");
            // The journal's next save cannot create its temporary file.
            var blocker = Path.Combine(directory, "temporary-takeovers.json.tmp");
            Directory.CreateDirectory(blocker);
            JsonElement unrecorded;
            try { unrecorded = Switch(core, "firmware"); }
            finally { Directory.Delete(blocker); }
            Check(Kind(unrecorded) == "StateUnavailable" && NamedGroup(unrecorded) == Group && HandedBack(unrecorded).SequenceEqual([0, 1])
                && Automatic(unrecorded, 0) == true && Automatic(unrecorded, 1) == true && !hardware.ModeBit,
                "a journal fault after the hardware hand-back is typed; both fans are listed, the second was not skipped");
            Check(JournalFans(directory) == 2, "the records stay, so a crash now would only release both again");
            var retried = Switch(core, "firmware");
            Check(Ok(retried) && HandedBack(retried).SequenceEqual([0, 1]) && JournalFans(directory) == 0,
                "a retry completes the records");
        }

        // Round-5 L-2: a firmware curve written while its journal entry cannot be removed is StateUnavailable, not a throw.
        Check(Ok(Send(core, new { op = "set", fan = 0, percent = 60 })) && JournalFans(directory) == 2, "fan 0 fixed again: both recorded");
        var journalBlocker = Path.Combine(directory, "temporary-takeovers.json.tmp");
        Directory.CreateDirectory(journalBlocker);
        JsonElement curveUnrecorded;
        try { curveUnrecorded = Send(core, new { op = "setcurve", fan = 0, execution = "firmware", curve = Curve }); }
        finally { Directory.Delete(journalBlocker); }
        Check(Kind(curveUnrecorded) == "StateUnavailable"
            && curveUnrecorded.GetProperty("error").GetString()!.Contains("固件曲线已写入并生效") && JournalFans(directory) == 2,
            "the curve is written and the unremoved record is reported as StateUnavailable");
        Check(Ok(Send(core, new { op = "setcurve", fan = 0, execution = "firmware", curve = Curve })) && JournalFans(directory) == 1,
            "writing the curve again removes fan 0's record");
    }

    /// <summary>
    /// Round-4 L-A, direct engine use: fan 0 runs a temporary fixed-duty table and both fans have plans. Fan 0's own
    /// drive passes the backend check (it excludes its own table) and sets the shared bit; fan 1's drive is refused as
    /// SharedTakeoverHeld. The refusal ends the takeover for both fans, as a blind or failed member does, instead of
    /// leaving fan 1 under the bit with nothing driving it. The group restarts only when fan 1's plan is recorded again.
    /// </summary>
    private static void EngineHeldRefusalEndsWholeTakeover(string directory)
    {
        var hardware = new SharedModeHardware();
        using var backend = new UniwillWmiFanBackend(hardware, directory, () => Device);
        Check(backend.TryOpen(out _) && backend.TrySetDuty(0, 60, out _), "fan 0 temporary fixed-duty table");
        using var engine = new SoftwareCurveEngine(backend);
        var gpu = new SoftwareCurvePlan(1, Curve, [new("gpu", () => 60)]);
        Check(engine.TryRecordPlan(new SoftwareCurvePlan(0, Curve, [new("cpu", () => 40)]), out _) && engine.TryRecordPlan(gpu, out _),
            "two plans");
        using (var run = new CurveRun(engine.RunAsync))
        {
            Until(() => engine.Suspension(1) == FanCurveSuspension.SharedTakeoverHeld
                && engine.Suspension(0) == FanCurveSuspension.SharedHandBack
                && !engine.HasRuntime(0) && !engine.HasRuntime(1) && !hardware.ModeBit,
                "fan 1's refusal hands the whole takeover back; neither fan is left under the bit");
            var mark = hardware.WriteMark;
            Thread.Sleep(300);
            Check(hardware.BitSetWrites(mark) == 0 && engine.Suspension(0) == FanCurveSuspension.SharedHandBack,
                "fan 0 does not take the bit back alone while fan 1's plan waits for a new recording");
            Check(engine.TryRecordPlan(gpu, out _), "record fan 1 again");
            Until(() => hardware.ModeBit && engine.Suspension(0) is null && engine.Suspension(1) is null
                && hardware[UniwillFanProfileTable.SecondFanDuty] == UniwillFanProfileTable.DutyFromPercent(60)
                && hardware[UniwillFanProfileTable.MainFanDuty] == UniwillFanProfileTable.DutyFromPercent(40),
                "the group restarts as a whole");
        }
        Check(!hardware.ModeBit, "run end hands back");
    }

    private static int[] HandedBack(JsonElement reply)
        => reply.GetProperty("handedBack").EnumerateArray().Select(entry => entry.GetInt32()).ToArray();

    private static int JournalFans(string directory)
    {
        using var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "temporary-takeovers.json")));
        return saved.RootElement.GetProperty("Fans").GetArrayLength();
    }

    /// <summary>Inside a software group, set and boost are each fan's own flat plan; no firmware table is touched.</summary>
    private static void FixedAndBoostInsideSoftwareGroup(string directory)
    {
        var hardware = new SharedModeHardware();
        using var core = Core(hardware, directory);
        using var temperatures = new Pusher(core, 40, 60);
        Check(Ok(Switch(core, "software", new { fan = 0, curve = Curve }, new { fan = 1, curve = Curve })), "group to software");
        using (var run = new CurveRun(core.RunSoftwareCurveAsync))
        {
            Until(() => hardware.ModeBit && hardware[UniwillFanProfileTable.SecondFanDuty] == UniwillFanProfileTable.DutyFromPercent(60), "driven");
            var tables = hardware.WritesIn(UniwillFanTable.CpuEndTemperature, 0x60);
            Check(Ok(Send(core, new { op = "boost", fan = 0 })), "boost inside the group");
            Until(() => hardware[UniwillFanProfileTable.MainFanDuty] == UniwillFanProfileTable.DutyFromPercent(100), "fan 0 at full duty", 1000);
            Check(core.ReadCurve(0)!.Single().Percent == 100 && core.ExecutionOf(0) == FanCurveExecutions.Software,
                "boost is a flat 100 % plan for fan 0");
            Check(hardware[UniwillFanProfileTable.SecondFanDuty] == UniwillFanProfileTable.DutyFromPercent(60), "fan 1 keeps its own curve");
            var set = Send(core, new { op = "set", fan = 1, percent = 35 });
            Check(Ok(set) && set.GetProperty("appliedPercent").ValueKind == JsonValueKind.Null, "set inside the group records a plan");
            Until(() => hardware[UniwillFanProfileTable.SecondFanDuty] == UniwillFanProfileTable.DutyFromPercent(35),
                "fan 1 goes straight to its fixed duty, not ramped", 1000);
            Check(hardware.WritesIn(UniwillFanTable.CpuEndTemperature, 0x60) == tables, "no firmware table was touched");
            Check(Ok(Switch(core, "firmware")), "group back to firmware");
            Until(() => !hardware.ModeBit && core.Read().All(fan => fan.AutomaticControl == true), "whole group automatic again");
        }
    }

    /// <summary>
    /// M1 through FanCore: switching a group to software first hands back a member's temporary fixed-duty table,
    /// which the shared bit would otherwise silently stop. If that hand-back fails, no plan is recorded.
    /// </summary>
    private static void GroupSwitchHandsBackFixedTables(string directory)
    {
        var hardware = new SharedModeHardware();
        var original = hardware[UniwillFanTable.CpuFanSpeed];
        using var core = Core(hardware, directory);
        using var temperatures = new Pusher(core, 40, 60);
        Check(Ok(Send(core, new { op = "set", fan = 0, percent = 60 })), "fan 0 fixed-duty table under firmware");
        Check(Ok(Send(core, new { op = "set", fan = 1, percent = 50 })), "fan 1 fixed-duty table under firmware");
        Check(hardware[UniwillFanTable.CpuFanSpeed] == UniwillFanProfileTable.DutyFromPercent(60), "table written");

        // Round-3 M2: fan 0's table is handed back, then fan 1's hand-back fails. Not rolled back, and reported.
        hardware.Change(fake => fake.Rejected.Add(UniwillFanTable.GpuFanSpeed));
        var refused = Switch(core, "software", new { fan = 0, curve = Curve }, new { fan = 1, curve = Curve });
        Check(Kind(refused) == "RestoreUnconfirmed" && NamedGroup(refused) == Group, "a failed table hand-back refuses the switch");
        Check(refused.GetProperty("handedBack").EnumerateArray().Select(entry => entry.GetInt32()).SequenceEqual([0])
            && Automatic(refused, 0) == true && Automatic(refused, 1) == false,
            "the refusal lists fan 0 as already handed back and carries the resulting states");
        Check(hardware[UniwillFanTable.CpuFanSpeed] == original, "fan 0's fixed duty is gone (no rollback)");
        Check(!core.SoftwarePlanRecorded(0) && !core.SoftwarePlanRecorded(1) && core.GroupControl(Group) == FanGroupControl.Firmware,
            "no plan recorded; the group stays under firmware");
        hardware.Change(fake => fake.Rejected.Clear());

        var applied = Switch(core, "software", new { fan = 0, curve = Curve }, new { fan = 1, curve = Curve });
        Check(Ok(applied) && applied.GetProperty("handedBack").EnumerateArray().Select(entry => entry.GetInt32()).SequenceEqual([1]),
            "switch succeeds after handing back fan 1's table");
        Check(hardware[UniwillFanTable.CpuFanSpeed] == original && !Directory.EnumerateFiles(directory, "uniwill-fans-*.json").Any(),
            "both original tables are back and no temporary table remains");
        using (var run = new CurveRun(core.RunSoftwareCurveAsync))
        {
            Until(() => hardware.ModeBit && core.ExecutionOf(0) == FanCurveExecutions.Software
                && core.ExecutionOf(1) == FanCurveExecutions.Software, "the group runs");
            Check(Ok(Switch(core, "firmware")), "cleanup");
        }
    }

    /// <summary>B-M4: unknown mode is null, a fixed duty is not automatic, the floor is described and the applied value reported.</summary>
    private static void TruthfulReadsAndFloor(string directory)
    {
        var hardware = new SharedModeHardware();
        using var core = Core(hardware, directory);
        var described = Send(core, new { op = "describe" }).GetProperty("fans");
        Check(described.EnumerateArray().All(fan => fan.GetProperty("minimumOnPercent").GetInt32() == 20), "20 % floor is described");
        Check(core.Read().All(fan => fan.AutomaticControl == true), "untouched fans are automatic");

        var low = Send(core, new { op = "set", fan = 0, percent = 5 });
        Check(Ok(low) && low.GetProperty("appliedPercent").GetDouble() == 20, "5 % reports the applied 20 %");
        Check(hardware[UniwillFanTable.CpuFanSpeed] == UniwillFanProfileTable.DutyFromPercent(20), "20 % really written");
        Check(Automatic(low, 0) == false && Automatic(low, 1) == true, "fixed duty is not automatic although the mode bit is clear");
        Check(!hardware.ModeBit, "fixed duty is a table, not the shared bit");
        var quantized = Send(core, new { op = "set", fan = 0, percent = 33.3 });
        Check(quantized.GetProperty("appliedPercent").GetDouble() == 33.5, "byte quantization reported (0.5 % steps)");
        var zero = Send(core, new { op = "set", fan = 0, percent = 0 });
        Check(zero.GetProperty("appliedPercent").GetDouble() == 0, "explicit stop stays 0");

        hardware.Change(fake => fake.MissingReads.Add(UniwillFanProfileTable.FanModeRegister));
        Check(core.Read().All(fan => fan.AutomaticControl is null), "unreadable mode register is unknown, not automatic");
        var unknown = Send(core, new { op = "read" });
        Check(unknown.GetProperty("fans")[0].GetProperty("automaticControl").ValueKind == JsonValueKind.Null, "JSON carries null");
        hardware.Change(fake => fake.MissingReads.Clear());

        Check(core.TrySetAutomatic(0, out _), "fixed duty handed back");
        Check(core.Read().All(fan => fan.AutomaticControl == true), "restored table is automatic again");
        Check(core.TrySetDuty(0, 60, out _), "fixed duty again");
        var baseline = Directory.EnumerateFiles(directory, "uniwill-fans-*.json").Single();
        var saved = File.ReadAllText(baseline);
        File.WriteAllText(baseline, "{}");
        // Reads use the set the backend last loaded (L3: no table IO on read); the next command that loads it finds the corruption.
        Check(core.Read()[0].AutomaticControl == false, "read does not touch the baseline file");
        Check(Kind(Send(core, new { op = "set", fan = 1, percent = 40 })) == "StateUnavailable", "the corrupt baseline is found by the next load");
        Check(core.Read().All(fan => fan.AutomaticControl is null), "unreadable baseline: whether a temporary table runs is unknown");
        File.WriteAllText(baseline, saved);
        Check(core.TrySetAutomatic(0, out _) && core.Read()[0].AutomaticControl == true, "valid baseline restores");
    }

    /// <summary>
    /// H2: the dead zone is decided by the curve target, not by where the ramp is. At the default 40 %/s and 100 ms
    /// ticks the first step from 0 is 4 %, which used to round back to 0 for ever. Idle at 0 % then a game load: the fan
    /// goes straight to the 20 % floor and ramps from there; falling, it holds the floor until the target is below 10 %.
    /// </summary>
    private static void EngineDeadZoneUsesFloor()
    {
        var hardware = new SharedModeHardware();
        using var backend = new UniwillWmiFanBackend(hardware, null, () => Device);
        Check(backend.TryOpen(out _), "dead-zone fixture opens");
        using var engine = new SoftwareCurveEngine(backend); // default 100 ms ticks
        var celsius = 0.0;
        Check(engine.TryRecordPlan(new(0, Curve, [new("cpu", () => Volatile.Read(ref celsius))]), out _),
            "default 40 %/s plan"); // Curve maps °C to %: idle 0 °C → 0 %
        using var run = new CurveRun(engine.RunAsync);
        var duty = UniwillFanProfileTable.MainFanDuty;
        Until(() => hardware.ValuesWrittenTo(duty).Count > 0, "idle writes its 0 % target");
        Check(hardware[duty] == 0, "idle fan stopped");

        Volatile.Write(ref celsius, 70); // game starts
        Until(() => hardware[duty] == UniwillFanProfileTable.DutyFromPercent(70), "ramps up to the 70 % target");
        var rising = hardware.ValuesWrittenTo(duty);
        Check(rising.SkipWhile(value => value == 0).First() == UniwillFanProfileTable.DutyFromPercent(20),
            "first step from 0 goes straight to the 20 % floor");
        Check(rising.Contains(UniwillFanProfileTable.DutyFromPercent(24)), "then ramps from the floor at the configured rate");

        Volatile.Write(ref celsius, 15); // target 15 % is above half the floor: hold the floor, do not stop
        Until(() => hardware[duty] == UniwillFanProfileTable.DutyFromPercent(20), "ramps down to the floor");
        Thread.Sleep(300);
        Check(hardware[duty] == UniwillFanProfileTable.DutyFromPercent(20), "holds the floor while the target is 15 %");

        Volatile.Write(ref celsius, 5); // below half the floor: stop
        Until(() => hardware[duty] == 0, "stops once the target is below half the floor");
        Check(hardware.ValuesWrittenTo(duty).All(value => value == 0 || value >= UniwillFanProfileTable.DutyFromPercent(20)),
            "no write ever lands inside the dead zone");
    }

    /// <summary>B-M5 on the Uniwill backend: each failure carries a typed kind through the JSON receipt.</summary>
    private static void TypedBackendFailures(string directory)
    {
        {
            var readOnly = new SharedModeHardware(writable: false);
            using var core = Core(readOnly, directory);
            Check(Kind(Send(core, new { op = "set", fan = 0, percent = 40 })) == "ReadOnly", "read-only firmware");
            Check(Kind(Send(core, new { op = "boost", fan = 1 })) == "ReadOnly", "read-only boost");
        }
        {
            var hardware = new SharedModeHardware();
            using var core = new FanCore(new UniwillWmiFanBackend(hardware, null, () => Device));
            Check(core.Open(), "no-storage opens");
            Check(Kind(Send(core, new { op = "set", fan = 0, percent = 40 })) == "StateUnavailable", "no baseline storage");
            Check(Kind(Send(core, new { op = "set", fan = 7, percent = 40 })) == "NoSuchFan", "unknown fan");
        }
        {
            var hardware = new SharedModeHardware();
            var store = Path.Combine(directory, "hardware");
            using var core = Core(hardware, store);
            hardware.Change(fake => fake.Rejected.Add(UniwillFanTable.CpuFanSpeed));
            Check(Kind(Send(core, new { op = "set", fan = 0, percent = 40 })) == "HardwareFailed", "rejected EC write");
            hardware.Change(fake => fake.Rejected.Clear());
            Check(Ok(Send(core, new { op = "auto", fan = 0 })), "failed write is cleaned up");
            Check(Ok(Send(core, new { op = "set", fan = 0, percent = 40 })), "fixed duty applied");
            hardware.Change(fake => fake.Ignored.Add((ushort)(UniwillFanTable.CpuFanSpeed + 3)));
            Check(Kind(Send(core, new { op = "auto", fan = 0 })) == "RestoreUnconfirmed", "restore readback mismatch");
            hardware.Change(fake => fake.Ignored.Clear());
            Check(Ok(Send(core, new { op = "auto", fan = 0 })), "retry restores");
        }
    }

    /// <summary>
    /// B-L1: new records use the firmware-table channel key, and a record written by the previous version with the
    /// display-name identity is still recovered (here: a crash that left the shared bit set).
    /// </summary>
    private static void StableRecoveryIdentity(string directory)
    {
        var hardware = new SharedModeHardware();
        using (var core = Core(hardware, directory))
        {
            Check(core.TrySetDuty(0, 50, out _), "takeover recorded");
            using var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "temporary-takeovers.json")));
            var identities = saved.RootElement.GetProperty("Fans").EnumerateArray()
                .Select(entry => entry.GetProperty("Identity").GetString()).ToArray();
            Check(identities.SequenceEqual(["ec-fan-table-0x0F00", "ec-fan-table-0x0F30"]), "records use the stable table key, not the display name");
            core.RestoreAll();
        }
        Check(!hardware.ModeBit, "fixture restored");

        // Simulate a previous-version crash: bit left set, journal with the old display-name identity.
        hardware.Change(fake => fake.Set(UniwillFanProfileTable.FanModeRegister, 0x81 | UniwillFanProfileTable.FullFanModeBit));
        File.WriteAllText(Path.Combine(directory, "temporary-takeovers.json"), JsonSerializer.Serialize(new
        {
            Version = 1, Machine,
            Fans = new[] { new { Backend = "uniwill-wmi-ec", Identity = "CPU 风扇\ncpu\nxy" } }
        }));
        using (var core = Core(hardware, directory))
        {
            Check(core.RecoveryBlockedReason is null && !hardware.ModeBit, "legacy display-name record recovered: bit released");
            using var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "temporary-takeovers.json")));
            Check(saved.RootElement.GetProperty("Fans").GetArrayLength() == 0, "legacy record consumed");
        }

        // An unmatched record still blocks writes, now with a typed kind and a describe flag.
        File.WriteAllText(Path.Combine(directory, "temporary-takeovers.json"), JsonSerializer.Serialize(new
        {
            Version = 1, Machine,
            Fans = new[] { new { Backend = "uniwill-wmi-ec", Identity = "renamed fan\ncpu\nxy" } }
        }));
        using (var core = Core(hardware, directory))
        {
            var describe = Send(core, new { op = "describe" });
            Check(describe.GetProperty("recoveryBlocked").GetBoolean()
                && describe.GetProperty("recoveryBlockedReason").GetString()!.Contains("recovery blocked"), "describe reports blocked recovery");
            Check(Kind(Send(core, new { op = "set", fan = 0, percent = 40 })) == "RecoveryBlocked", "writes rejected as RecoveryBlocked");
            Check(Kind(Switch(core, "software", new { fan = 0, percent = 40 }, new { fan = 1, percent = 40 })) == "RecoveryBlocked",
                "group switches too");
        }
        File.Delete(Path.Combine(directory, "temporary-takeovers.json"));
    }

    /// <summary>
    /// M1 for direct engine and backend use (FanCore avoids it by handing tables back on a group switch): while fan 0
    /// runs a temporary fixed-duty table, a drive or boost of fan 1 is refused before any write; the plan waits as
    /// SharedTakeoverHeld and runs when recorded again after the table ended.
    /// </summary>
    private static void EngineRefusesOverFixedTable(string directory)
    {
        var hardware = new SharedModeHardware();
        using var backend = new UniwillWmiFanBackend(hardware, directory, () => Device);
        Check(backend.TryOpen(out _), "engine fixture opens");
        Check(backend.TrySetDuty(0, 60, out _), "fan 0 temporary fixed-duty table");
        Check(!backend.TrySetFullSpeed(1, out var boostKind, out _) && boostKind == FanCommandErrorKind.SharedTakeoverHeld && !hardware.ModeBit,
            "boost refused before any write");
        using var engine = new SoftwareCurveEngine(backend);
        var plan = new SoftwareCurvePlan(1, Curve, [new("gpu", () => 60)]);
        Check(engine.TryRecordPlan(plan, out _), "fan 1 plan");
        using (var run = new CurveRun(engine.RunAsync))
        {
            Until(() => engine.Suspension(1) == FanCurveSuspension.SharedTakeoverHeld, "the drive is refused and the plan says why");
            Check(!hardware.ModeBit && hardware.WritesTo(UniwillFanProfileTable.SecondFanDuty) == 0, "no bit and no duty write");
            Check(hardware[UniwillFanTable.CpuFanSpeed] == UniwillFanProfileTable.DutyFromPercent(60), "fan 0's table still runs");
            Check(engine.SoftwareCurveTemperatureKinds.Count == 0, "a held plan waits for a command, so it needs no temperatures");
            Check(backend.TrySetAutomatic(0, out _), "fan 0 hands its table back");
            Thread.Sleep(300);
            Check(engine.Suspension(1) == FanCurveSuspension.SharedTakeoverHeld, "a held plan resumes only when recorded again");
            Check(engine.TryRecordPlan(plan, out _), "record again");
            Until(() => hardware.ModeBit && hardware[UniwillFanProfileTable.SecondFanDuty] == UniwillFanProfileTable.DutyFromPercent(60),
                "fan 1 is driven");
        }
        Check(!hardware.ModeBit, "run end hands back");
    }

    /// <summary>L1 at the backend: when clearing the bit fails, the fan keeps its share, so other fans are still refused.</summary>
    private static void FailedClearKeepsHolder(string directory)
    {
        var hardware = new SharedModeHardware();
        using var backend = new UniwillWmiFanBackend(hardware, directory, () => Device);
        Check(backend.TryOpen(out _) && backend.TrySetFullSpeed(0, out _) && hardware.ModeBit, "boost holds the bit");
        hardware.Change(fake => fake.Rejected.Add(UniwillFanProfileTable.FanModeRegister));
        Check(!backend.TrySetDuty(0, 40, out _, out var kind, out _) && kind == FanCommandErrorKind.HardwareFailed, "the clear before the table fails");
        Check(hardware.ModeBit && backend.SharedTakeoverHolders.SequenceEqual([0]), "bit still set and fan 0 still holds it");
        Check(!backend.TrySetDuty(1, 40, out _, out kind, out _) && kind == FanCommandErrorKind.SharedTakeoverHeld, "other fans stay refused");
        Check(!backend.TrySetAutomatic(0, out _) && backend.SharedTakeoverHolders.SequenceEqual([0]), "a failed release keeps the share too");
        hardware.Change(fake => fake.Rejected.Clear());
        Check(backend.TrySetAutomatic(0, out _) && !hardware.ModeBit && backend.SharedTakeoverHolders.Count == 0, "retry clears the bit and the share");
    }

    /// <summary>L3: a read does not wait behind a table write blocked in the transport.</summary>
    private static void ReadDoesNotWaitForTableWrites(string directory)
    {
        var hardware = new SharedModeHardware();
        using var core = Core(hardware, directory);
        Check(core.Read().Count == 2, "first read publishes the temporary-table set");
        using var release = new ManualResetEventSlim();
        hardware.Change(fake => fake.HoldWrite((ushort)(UniwillFanTable.CpuFanSpeed + 5), release));
        var set = Task.Run(() => core.TrySetDuty(0, 40, out _));
        try
        {
            Until(() => hardware.HeldWrites > 0, "table write is stuck in the transport");
            var read = Task.Run(() => core.Read());
            Check(read.Wait(1000) && read.Result.Count == 2, "read answers while the table write holds the table lock");
        }
        finally { release.Set(); }
        Check(set.Wait(3000) && set.Result, "the held write completes afterwards");
        Check(core.Read()[0].AutomaticControl == false, "the completed fixed duty is published");
        Check(core.TrySetAutomatic(0, out _), "cleanup");
    }

    /// <summary>L4 at the backend: a boost whose bit write throws still counts as a holder until auto on that fan.</summary>
    private static void ThrowingBoostKeepsHolder(string directory)
    {
        var hardware = new SharedModeHardware();
        using var backend = new UniwillWmiFanBackend(hardware, directory, () => Device);
        Check(backend.TryOpen(out _), "throwing fixture opens");
        hardware.Change(fake => fake.Throwing.Add(UniwillFanProfileTable.FanModeRegister));
        var threw = false;
        try { backend.TrySetFullSpeed(0, out _); }
        catch (InvalidOperationException) { threw = true; }
        Check(threw && backend.SharedTakeoverHolders.SequenceEqual([0]), "the throwing attempt still counts as holding the bit");
        Check(!backend.TrySetDuty(1, 40, out _, out var kind, out _) && kind == FanCommandErrorKind.SharedTakeoverHeld, "other fans are refused");
        hardware.Change(fake => fake.Throwing.Clear());
        Check(backend.TrySetAutomatic(0, out _) && backend.SharedTakeoverHolders.Count == 0 && !hardware.ModeBit, "auto on that fan clears the share");
    }

    private static void DeleteFlat(string root)
    {
        // Fixed fixture layout: root/<case>[/hardware]; bounded, no recursion.
        if (!Directory.Exists(root)) return;
        var pending = new Stack<string>([root]);
        var directories = new List<string>();
        while (pending.TryPop(out var directory))
        {
            if (directories.Count > 64) throw new IOException("Test cleanup bound exceeded.");
            directories.Add(directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (!Path.GetFullPath(entry).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) throw new IOException("Unexpected test path.");
                if (Directory.Exists(entry)) pending.Push(entry); else File.Delete(entry);
            }
        }
        for (var index = directories.Count - 1; index >= 0; index--) Directory.Delete(directories[index]);
    }

    /// <summary>Thread-safe Uniwill EC fake: the engine thread and the test thread use it concurrently.</summary>
    internal sealed class SharedModeHardware : IUniwillFanHardware
    {
        private readonly object sync = new();
        private readonly Dictionary<ushort, byte> values = [];
        private readonly List<(ushort Address, byte Value)> writes = [];
        private readonly Dictionary<ushort, ManualResetEventSlim> holds = [];
        private int heldWrites;
        internal HashSet<ushort> MissingReads { get; } = [];
        internal HashSet<ushort> Rejected { get; } = [];
        internal HashSet<ushort> Ignored { get; } = [];
        /// <summary>Writes to these addresses throw, like a transport that faults mid-call.</summary>
        internal HashSet<ushort> Throwing { get; } = [];

        internal SharedModeHardware(bool writable = true)
        {
            values[UniwillFanProfileTable.FanControlStatus] = writable ? UniwillFanProfileTable.HasFanControlBit : (byte)0;
            values[UniwillFanProfileTable.FanModeRegister] = 0x81;
            values[UniwillFanTable.UseCustomTablesRegister] = 0x81;
            for (var fan = 0; fan < 2; fan++)
            {
                var (end, start, speed) = UniwillFanTable.AddressesFor(fan);
                for (var zone = 0; zone < UniwillFanTable.ZoneCount; zone++)
                {
                    values[(ushort)(end + zone)] = (byte)(40 + zone * 4);
                    values[(ushort)(start + zone)] = (byte)(38 + zone * 4);
                    values[(ushort)(speed + zone)] = (byte)(30 + zone + fan * 20);
                }
            }
        }

        internal byte this[ushort address] { get { lock (sync) return values.GetValueOrDefault(address); } }
        internal bool ModeBit => (this[UniwillFanProfileTable.FanModeRegister] & UniwillFanProfileTable.FullFanModeBit) != 0;
        /// <summary>What drives this fan: with the shared bit set its duty register, otherwise the firmware's table logic.</summary>
        internal string Effective(int fan) => fan is 0 or 1 ? ModeBit ? "duty-register" : "firmware" : throw new ArgumentOutOfRangeException(nameof(fan));
        internal int TotalWrites { get { lock (sync) return writes.Count; } }
        internal int WritesTo(ushort address) { lock (sync) return writes.Count(write => write.Address == address); }
        internal List<byte> ValuesWrittenTo(ushort address)
        { lock (sync) return writes.Where(write => write.Address == address).Select(write => write.Value).ToList(); }
        internal int WritesIn(ushort start, int length)
        { lock (sync) return writes.Count(write => write.Address >= start && write.Address < start + length); }
        private readonly List<int> tickStarts = [];
        /// <summary>Hooked to the engine's BeforeIteration: records where each run tick begins in the write log.</summary>
        internal void MarkTick() { lock (sync) tickStarts.Add(writes.Count); }
        /// <summary>Position in the write log, to look only at writes made after it.</summary>
        internal int WriteMark { get { lock (sync) return writes.Count; } }
        /// <summary>Writes that set the shared bit (a takeover drive or boost), from a log position on.</summary>
        internal int BitSetWrites(int from = 0)
        {
            lock (sync)
                return writes.Skip(from).Count(write => write.Address == UniwillFanProfileTable.FanModeRegister
                    && (write.Value & UniwillFanProfileTable.FullFanModeBit) != 0);
        }
        /// <summary>
        /// The first write at or after <paramref name="from"/> that set the shared bit lies in a run tick (bounded by
        /// MarkTick) that also writes both duty registers, and neither register was written between from and that tick.
        /// A member started one tick later fails this even if its write is the very next one.
        /// </summary>
        internal bool TakeoverStartedInOneTick(ushort first, ushort second, int from = 0)
        {
            lock (sync)
            {
                var start = writes.FindIndex(from, write => write.Address == UniwillFanProfileTable.FanModeRegister
                    && (write.Value & UniwillFanProfileTable.FullFanModeBit) != 0);
                if (start < 0) return false;
                var tickBegin = tickStarts.LastOrDefault(position => position <= start, -1);
                if (tickBegin < from) return false;
                var tickEnd = tickStarts.FirstOrDefault(position => position > start, writes.Count);
                var tick = writes.Skip(tickBegin).Take(tickEnd - tickBegin).Select(write => write.Address).ToArray();
                return tick.Contains(first) && tick.Contains(second)
                    && writes.Skip(from).Take(tickBegin - from).All(write => write.Address != first && write.Address != second);
            }
        }
        internal int HeldWrites => Volatile.Read(ref heldWrites);
        /// <summary>The next write to this address blocks (outside the fake's lock) until the event is set.</summary>
        internal void HoldWrite(ushort address, ManualResetEventSlim release) => holds[address] = release;
        internal void Change(Action<SharedModeHardware> change) { lock (sync) change(this); }
        internal void Set(ushort address, byte value) => values[address] = value;

        public bool IsAvailable => true;
        public byte? Read(ushort address) { lock (sync) return MissingReads.Contains(address) ? null : values.GetValueOrDefault(address); }
        public int? ReadBigEndianWord(ushort highAddress, ushort lowAddress)
        { lock (sync) return Read(highAddress) is { } high && Read(lowAddress) is { } low ? (high << 8) | low : null; }
        public bool WriteBit(ushort address, byte bit, bool set, int attempts = 3)
        {
            Wait(address);
            lock (sync)
            {
                var value = values.GetValueOrDefault(address);
                return Write(address, set ? (byte)(value | bit) : (byte)(value & ~bit));
            }
        }
        public bool WriteWithRetry(ushort address, byte value, int attempts = 3)
        {
            Wait(address);
            lock (sync) return Write(address, value);
        }
        private void Wait(ushort address)
        {
            ManualResetEventSlim? hold;
            lock (sync)
            {
                if (Throwing.Contains(address)) throw new InvalidOperationException($"EC transport fault at 0x{address:X4}.");
                if (holds.Remove(address, out hold)) Interlocked.Increment(ref heldWrites);
            }
            hold?.Wait(5000);
        }
        private bool Write(ushort address, byte value)
        {
            writes.Add((address, value));
            if (Rejected.Contains(address)) return false;
            if (!Ignored.Contains(address)) values[address] = value;
            return true;
        }
        public void Dispose() { }
    }
}
