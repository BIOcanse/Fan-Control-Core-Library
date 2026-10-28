using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using FanControlCore;
using FanControlCore.Discovery;
using FanControlCore.Policy;

if (args is ["--crash-takeover", var crashRoot]) { CrashRecoveryTests.RecordAndExit(crashRoot); return; }

static void Check(bool result, string message) { if (!result) throw new Exception(message); }
static void Until(Func<bool> ready, string message) { Check(SpinWait.SpinUntil(ready, 3000), message); }
try
{
    TemperatureKindTests.Run();
    if (args is ["--review-run-needed"]) { RunEntryReviewTests.RunNeeded(); return; }
    if (args is ["--review-plan-removal"]) { RunEntryReviewTests.PlanRemoval(); return; }
    RunEntryReviewTests.RunNeeded();
    RunEntryReviewTests.PlanRemoval();
    OpenFailureTests.Run();
    SoftwareCurveRunTests.Run();
    CrashRecoveryTests.Run();
    CurveIsolationTests.Run();
    var curve = new FanCurvePoint[] { new(60, 35), new(95, 100) };

    foreach (var action in new[] { "set", "boost", "auto" })
    {
        var backend = new FakeBackend();
        using var core = new FanCore(backend);
        Check(core.Open(), "fake backend opens");
        core.PushTemperatures([new("cpu", 60, DateTime.UtcNow.Ticks)]);
        Check(core.TryWriteCurve(0, curve, "software", out _), "software curve starts");
        using var run = new CurveRun(core.RunSoftwareCurveAsync);
        Until(() => backend.Drives > 0, "curve drives fake fan");
        Check(core.ReadCurve(0)!.SequenceEqual(curve), "software receipt contains active software curve");
        Check(backend.CurveReads == 0, "software receipt does not read firmware");
        using (var input = new MessageInput())
        using (var output = new StringWriter())
        {
            input.Push("{\"id\":17,\"op\":\"curve\",\"fan\":0}");
            input.End();
            FanCommandServer.Run(core, input, output);
            using var reply = JsonDocument.Parse(output.ToString().Trim());
            Check(reply.RootElement.GetProperty("execution").GetString() == "software", "curve protocol reports actual software execution");
            Check(reply.RootElement.GetProperty("curve").GetArrayLength() == curve.Length, "curve protocol returns the complete active plan");
            Check(backend.CurveReads == 0, "software protocol receipt does not read firmware");
        }
        Check(action switch {
            "set" => core.TrySetDuty(0, 70, out _),
            "boost" => core.TrySetFullSpeed(0, out _),
            _ => core.TrySetAutomatic(0, out _)
        }, "manual action succeeds");
        var writes = backend.Drives;
        core.PushTemperatures([new("cpu", 100, DateTime.UtcNow.Ticks)]);
        Thread.Sleep(250);
        Check(backend.Drives == writes, $"{action} cannot be overwritten by old software curve");
    }

    {
        var backend = new FakeBackend { FailDrive = true, RestoreFailures = 2 };
        using var engine = new SoftwareCurveEngine(backend, TimeSpan.FromMilliseconds(5));
        Check(engine.TryRecordPlan(new(0, curve, [new("cpu", () => 60)]), out _), "failed-write fixture starts");
        using var run = new CurveRun(engine.RunAsync);
        Until(() => backend.AutomaticCalls >= 3 && !engine.IsRunning(0), "false writes hand back and failed restoration retries");
        Check(backend.Drives == 5, "stop driving after five failures");
    }

    {
        var store = new FanTemperatureStore();
        var old = DateTime.UtcNow.AddSeconds(-10).Ticks;
        var now = DateTime.UtcNow.Ticks;
        store.Push([new("cpu", 45, old), new("gpu0", 80, now)]);
        Check(store.Read("cpu") is null && store.Read("gpu0") == 80, "freshness is checked per sensor at observation time");
        store.Push([new("cpu", 45, old)]);
        Check(!store.IsFresh && store.Names.Count == 0, "repeated old temperatures cannot renew freshness");
    }

    {
        var backend = new FakeBackend { BlockCurve = true };
        using var core = new FanCore(backend);
        core.Open();
        using var input = new MessageInput();
        using var output = new StringWriter();
        var server = Task.Run(() => FanCommandServer.Run(core, input, output));
        input.Push("{\"id\":1,\"op\":\"curve\",\"fan\":0}");
        Until(() => backend.CurveReads == 1, "slow hardware read started");
        input.Push(JsonSerializer.Serialize(new { op = "temp", notification = true,
            samples = new { cpu = 86 }, observedAtUtcTicks = new { cpu = DateTime.UtcNow.Ticks } }));
        Until(() => core.Temperatures.Read("cpu") == 86, "temperature arrives during a blocked hardware read");
        backend.ReleaseCurve.Set();
        input.End();
        Check(server.Wait(3000), "protocol closes normally");
        using var receipt = JsonDocument.Parse(output.ToString().Trim());
        Check(receipt.RootElement.GetProperty("id").GetInt64() == 1, "notification generates no competing receipt");
    }
    {
        var backend = new FakeBackend { Role = FanRoles.Unknown };
        using var core = new FanCore(backend);
        core.Open();
        core.PushTemperatures([new("gpu0", 70, DateTime.UtcNow.Ticks)]);
        Check(core.TryWriteCurve(0, curve, "software", out _), "unknown fan records before required CPU is available");
        Check(backend.Drives == 0, "missing CPU cannot take software ownership");
        core.PushTemperatures([new("cpu", 60, DateTime.UtcNow.Ticks), new("gpu0", 70, DateTime.UtcNow.Ticks)]);
        Check(core.TryWriteCurve(0, curve, "software", out _), "unknown fan starts with CPU and GPU temperatures");
        using var run = new CurveRun(core.RunSoftwareCurveAsync);
        Until(() => backend.Drives > 0, "unknown fan drives with required CPU present");
    }

    {
        var backend = new FakeBackend();
        using var engine = new SoftwareCurveEngine(backend, TimeSpan.FromMilliseconds(5));
        var cpuAvailable = 1;
        var plan = new SoftwareCurvePlan(0, curve,
            [new("cpu", () => Volatile.Read(ref cpuAvailable) != 0 ? 60 : null, Required: true), new("gpu0", () => 100)]);
        Check(engine.TryRecordPlan(plan, out _), "required-source fixture starts");
        using var run = new CurveRun(engine.RunAsync);
        Until(() => backend.Drives > 0, "required-source fixture drives");
        Volatile.Write(ref cpuAvailable, 0);
        Thread.Sleep(50);
        var writes = backend.Drives;
        Check(engine.TryRecordPlan(plan, out _), "replacement does not require a current CPU observation");
        Check(SpinWait.SpinUntil(() => !engine.IsRunning(0), 6500), "required CPU loss cannot be replaced by fresh GPU; existing blind timeout restores firmware");
        Check(backend.Drives == writes && backend.AutomaticCalls > 0, "no GPU-only writes during CPU loss");
    }

    {
        var backend = new FakeBackend();
        using var core = new FanCore(backend);
        core.Open();
        using var input = new MessageInput();
        using var output = new StringWriter();
        input.Push("{\"id\":9,\"op\":\"describe\",\"refresh\":true}");
        input.End();
        FanCommandServer.Run(core, input, output);
        Check(backend.Refreshes == 1, "explicit describe refresh reaches existing backend");
        using var receipt = JsonDocument.Parse(output.ToString().Trim());
        Check(receipt.RootElement.GetProperty("fans")[0].GetProperty("firmwareCurveRequires").GetString() == "fixture requirement", "fresh prerequisite is serialized to host");
        Check(backend.Drives == 0, "detection refresh does not drive a fan");
    }

    if (OperatingSystem.IsWindows())
    {
        LenovoFirmwareCurveTests.Run();
        UniwillFirmwareOwnershipTests.Run();
        var backendType = typeof(FanControlCore.Protocols.DesktopSuperIoFanBackend);
        using var backend = new FanControlCore.Protocols.DesktopSuperIoFanBackend();
        var channels = (System.Collections.IList)backendType.GetField("channels", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(backend)!;
        var channelType = backendType.GetNestedType("Channel", System.Reflection.BindingFlags.NonPublic)!;
        channels.Add(Activator.CreateInstance(channelType, [0, "owned fake", new object(), null, new object()])!);
        Check(!backend.TrySetDuty(0, 45, out _), "missing reflection write method cannot report success");
        Check(!backend.TrySetAutomatic(0, out _), "missing reflection restore method cannot report success");
    }

    Console.WriteLine("FanControlCore regressions: ownership transitions, false-write restoration, software receipts, observation freshness, and slow-command temperature delivery passed.");
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    Environment.ExitCode = 1;
}

sealed class MessageInput : TextReader
{
    private readonly BlockingCollection<string> messages = new();
    public void Push(string line) => messages.Add(line);
    public void End() => messages.CompleteAdding();
    public override string? ReadLine() => messages.TryTake(out var line, Timeout.Infinite) ? line : null;
    protected override void Dispose(bool disposing) { if (disposing) messages.Dispose(); base.Dispose(disposing); }
}

sealed class FakeBackend : IFanBackend
{
    public string Name => "fixture";
    public int Drives, AutomaticCalls, CurveReads, Refreshes;
    public bool FailDrive, BlockCurve;
    public string Role = FanRoles.Cpu;
    public int RestoreFailures;
    public ManualResetEventSlim ReleaseCurve { get; } = new();
    public bool TryOpen(out string? reason) { reason = null; return true; }
    public void RefreshDetection() => Refreshes++;
    public IReadOnlyList<FanDescription> Describe() => [new(0, "Fake", true, true, Role, SupportsSoftwareCurve: true, FirmwareCurveRequires: Refreshes > 0 ? "fixture requirement" : null)];
    public IReadOnlyList<FanState> Read() => [];
    public IReadOnlyList<FanCurvePoint>? ReadCurve(int fan) { Interlocked.Increment(ref CurveReads); if (BlockCurve) ReleaseCurve.Wait(TimeSpan.FromSeconds(5)); return [new(50, 10)]; }
    public bool TryDriveDuty(int fan, double percent, out string? reason) { Interlocked.Increment(ref Drives); reason = null; return !FailDrive; }
    public bool TrySetDuty(int fan, double percent, out string? reason) { reason = null; return true; }
    public bool TrySetFullSpeed(int fan, out string? reason) { reason = null; return true; }
    public bool TrySetAutomatic(int fan, out string? reason) { var count = Interlocked.Increment(ref AutomaticCalls); reason = count <= RestoreFailures ? "fixture failure" : null; return count > RestoreFailures; }
    public void Dispose() => ReleaseCurve.Dispose();
}
