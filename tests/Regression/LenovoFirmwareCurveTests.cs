using System.Runtime.Versioning;
using System.Text.Json;
using FanControlCore;
using FanControlCore.Discovery;
using FanControlCore.Protocols;

[SupportedOSPlatform("windows")]
internal static class LenovoFirmwareCurveTests
{
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception("Lenovo: " + message); }
    private static FanCurvePoint[] Curve(double percent)
        => Enumerable.Range(0, 10).Select(index => new FanCurvePoint(40 + index * 5, percent)).ToArray();

    internal static void Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fcc-lenovo-" + Guid.NewGuid().ToString("N"));
        try
        {
            TestOpenFailures();
            TestPersistentCancellation(directory);
            TestFailures(directory);
            TestMissingAndInvalid(directory);
            TestDetectionRefresh(directory);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    private static void TestOpenFailures()
    {
        foreach (var supported in new ulong?[] { 0, null })
        {
            using var backend = new LenovoWmiFanBackend(new Hardware { CoolingSupport = supported }, null);
            Check(!backend.TryOpen(out var kind, out _)
                && kind == (supported == 0 ? FanOpenFailureKind.NoSupportedController : FanOpenFailureKind.Other),
                "explicit unsupported cooling differs from an unreadable response");
        }
        foreach (var kind in new[] { FanOpenFailureKind.AccessDenied, FanOpenFailureKind.DriverMissing })
        {
            using var backend = new LenovoWmiFanBackend(new Hardware { Available = false, AvailabilityFailureKind = kind }, null);
            Check(!backend.TryOpen(out var actual, out _) && actual == kind, "typed transport failure reaches backend");
        }
    }

    private static void TestDetectionRefresh(string directory)
    {
        var hardware = new Hardware();
        using var core = new FanCore(new LenovoWmiFanBackend(hardware, Path.Combine(directory, "refresh")));
        Check(core.Open(), "refresh fixture opens");
        Check(core.Describe()[0].FirmwareCurveRequires is null, "custom mode has no prerequisite");
        hardware.Tables[1][0] = 1;
        Check(core.Describe()[0].FirmwareCurveRequires is null, "ordinary describe preserves cached capability");
        var afterModeChange = DescribeThroughProtocol(core);
        Check(afterModeChange[0].FirmwareCurveRequires is not null, "explicit refresh reports changed mode");
        hardware.Tables[1][6] = 255;
        var afterInvalidLayout = DescribeThroughProtocol(core);
        Check(afterInvalidLayout[0].FirmwareCurve is null && afterInvalidLayout[0].FirmwareCurveRequires is null,
            "invalid layout removes curve and stale mode prerequisite together");
        hardware.Tables[1][6] = 0; hardware.Tables[1][0] = 0xFF;
        var restored = DescribeThroughProtocol(core);
        Check(restored[0].FirmwareCurve == FanFirmwareCurveKinds.Table && restored[0].FirmwareCurveRequires is null,
            "returning to a valid custom mode recovers capability");
        Check(hardware.TableWrites == 0 && !hardware.Cooling, "refresh does not write tables or cooling mode");
    }

    private static FanDescription[] DescribeThroughProtocol(FanCore core)
    {
        using var input = new StringReader("{\"id\":11,\"op\":\"describe\",\"refresh\":true}\n");
        using var output = new StringWriter();
        FanCommandServer.Run(core, input, output);
        using var document = JsonDocument.Parse(output.ToString().Trim());
        Check(document.RootElement.GetProperty("ok").GetBoolean(), "refresh protocol succeeds");
        return JsonSerializer.Deserialize<FanDescription[]>(document.RootElement.GetProperty("fans").GetRawText(),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    private static void TestPersistentCancellation(string directory)
    {
        var hardware = new Hardware();
        var original0 = hardware.Tables[1].ToArray();
        var original1 = hardware.Tables[2].ToArray();
        var storage = Path.Combine(directory, "persistent");
        using (var core = new FanCore(new LenovoWmiFanBackend(hardware, storage)))
        {
            Check(core.Open(), "fake Lenovo opens");
            Check(core.ReadCurve(0) is null, "all-zero original is not a stopped-fan curve");
            Check(core.TryWriteCurve(0, Curve(60), "firmware", out _), "first table writes");
            Check(core.TryWriteCurve(0, Curve(80), "firmware", out _), "editing preserves first baseline");
            Check(core.TryWriteCurve(1, Curve(90), "firmware", out _), "second fan writes independently");
            Check(core.TrySetFullSpeed(0, out _), "temporary boost starts");
            var written = hardware.Tables[1].ToArray();
            Check(core.TrySetAutomatic(0, out _), "ordinary auto only releases temporary boost");
            Check(hardware.Tables[1].SequenceEqual(written), "ordinary auto preserves configured firmware curve");
            Check(core.TrySetFullSpeed(0, out _), "boost restarts before shutdown test");
            core.RestoreAll();
            Check(!hardware.Cooling && hardware.Tables[1].SequenceEqual(written), "shutdown releases boost without undoing table");
        }
        Check(Directory.GetFiles(storage, "*.json").Length == 2, "firmware baselines survive helper shutdown");
        using (var core = new FanCore(new LenovoWmiFanBackend(hardware, storage)))
        {
            Check(core.Open(), "new helper opens");
            var sibling = hardware.Tables[2].ToArray();
            Check(core.TrySetAutomatic(0, out _, resetFirmwareCurve: true), "explicit cancellation after restart succeeds");
            Check(hardware.Tables[1].SequenceEqual(original0), "complete original including zeros and opaque bytes restored");
            Check(hardware.Tables[2].SequenceEqual(sibling), "other fan table remains unchanged");
            Check(Directory.GetFiles(storage, "*.json").Length == 1, "only restored fan baseline removed");
            using var input = new StringReader("{\"id\":10,\"op\":\"auto\",\"fan\":1,\"resetFirmwareCurve\":true}\n");
            using var output = new StringWriter();
            FanCommandServer.Run(core, input, output);
            using var receipt = JsonDocument.Parse(output.ToString().Trim());
            Check(receipt.RootElement.GetProperty("ok").GetBoolean(), "host auto protocol dispatches required table restore");
            Check(hardware.Tables[2].SequenceEqual(original1), "second original restored through protocol");
            Check(Directory.GetFiles(storage, "*.json").Length == 0, "verified restore consumes baseline");
        }
    }

    private static void TestFailures(string directory)
    {
        var hardware = new Hardware();
        var original = hardware.Tables[1].ToArray();
        var storage = Path.Combine(directory, "failures");
        using var backend = new LenovoWmiFanBackend(hardware, storage);
        Check(backend.TryOpen(out _), "failure fixture opens");
        Check(backend.TryWriteCurve(0, Curve(80), out _), "failure fixture writes");
        var saved = File.ReadAllText(Directory.GetFiles(storage, "*.json").Single());
        hardware.Tables[1][0] = 2;
        var writes = hardware.TableWrites;
        Check(!backend.TryResetFirmwareCurve(0, out _) && hardware.TableWrites == writes, "power mode change cannot write or silently switch mode");
        hardware.Tables[1][0] = 0xFF;
        hardware.IgnoreWrites = true;
        Check(!backend.TryResetFirmwareCurve(0, out _), "readback mismatch cannot release");
        Check(File.ReadAllText(Directory.GetFiles(storage, "*.json").Single()) == saved, "failed restore retains exact baseline");
        hardware.IgnoreWrites = false;
        hardware.FailCooling = true;
        Check(!backend.TryResetFirmwareCurve(0, out _), "cooling release failure retains baseline after table restored");
        Check(hardware.Tables[1].SequenceEqual(original), "table restoration occurs before release receipt");
        hardware.FailCooling = false;
        Check(backend.TryResetFirmwareCurve(0, out _), "pending restore can retry");
        Check(Directory.GetFiles(storage, "*.json").Length == 0, "successful retry removes record");
        hardware.RejectTable = true;
        Check(!backend.TryWriteCurve(0, Curve(80), out _), "rejected write is not applied");
        Check(Directory.GetFiles(storage, "*.json").Length == 1, "uncertain write retains pre-write original");
    }

    private static void TestMissingAndInvalid(string directory)
    {
        var hardware = new Hardware();
        var storage = Path.Combine(directory, "invalid");
        using var backend = new LenovoWmiFanBackend(hardware, storage);
        Check(backend.TryOpen(out _), "invalid fixture opens");
        Check(!backend.TryResetFirmwareCurve(0, out _) && hardware.TableWrites == 0, "missing baseline cannot report cancellation success");
        Check(!backend.TryWriteCurve(0, Curve(double.NaN), out _) && !Directory.Exists(storage), "nonfinite curve cannot capture or write");
        Check(backend.TryWriteCurve(0, Curve(80), out _), "invalid fixture obtains baseline");
        var path = Directory.GetFiles(storage, "*.json").Single();
        File.WriteAllText(path, "{}");
        var writes = hardware.TableWrites;
        Check(!backend.TryResetFirmwareCurve(0, out _) && hardware.TableWrites == writes, "missing fields cannot become zero restoration");
        Check(!backend.TryWriteCurve(0, Curve(60), out _) && hardware.TableWrites == writes, "corruption cannot be replaced by current altered values");
        Check(File.ReadAllText(path) == "{}", "corrupt evidence preserved");
        using var noStorage = new LenovoWmiFanBackend(new Hardware(), null);
        Check(noStorage.TryOpen(out _), "read-only standalone fixture opens");
        Check(!noStorage.TryWriteCurve(0, Curve(80), out _), "standalone curve needs explicit durable storage");
        var unidentified = new Hardware { Identity = null };
        using var noIdentity = new LenovoWmiFanBackend(unidentified, storage);
        Check(noIdentity.TryOpen(out _), "unknown identity fixture opens for observation");
        Check(!noIdentity.TryWriteCurve(0, Curve(80), out _) && unidentified.TableWrites == 0, "unidentified device cannot share another machine's baseline");
    }

    private sealed class Hardware : ILenovoFanHardware
    {
        public bool Available = true;
        public bool IsAvailable => Available;
        public FanOpenFailureKind AvailabilityFailureKind { get; init; } = FanOpenFailureKind.Other;
        public ulong? CoolingSupport = 1;
        public string? Identity { get; init; } = "b0b0f66a-13ce-4ef0-868f-e145cd703cef";
        public bool Cooling, FailCooling, IgnoreWrites, RejectTable;
        public int TableWrites;
        public Dictionary<uint, byte[]> Tables { get; } = new() { [1] = Table(1), [2] = Table(2) };
        private static byte[] Table(byte id)
        {
            var table = Enumerable.Range(0, 32).Select(index => (byte)(index + 100)).ToArray();
            table[0] = 0xFF; table[1] = id;
            for (var point = 0; point < 10; point++) table[6 + point * 2] = 0;
            return table;
        }
        public ulong? ReadCoolingSupported() => CoolingSupport;
        public ulong? ReadCooling() => Cooling ? 1ul : 0ul;
        public bool SetCooling(bool enabled) { if (FailCooling) return false; Cooling = enabled; return true; }
        public ulong? ReadRpm(uint fanId) => Tables.ContainsKey(fanId) ? 2000ul : null;
        public byte[]? ReadTable(uint fanId) => Tables.TryGetValue(fanId, out var table) ? [.. table] : null;
        public bool WriteTable(uint fanId, byte[] buffer)
        {
            TableWrites++;
            if (RejectTable) return false;
            if (!IgnoreWrites) Tables[fanId] = [.. buffer];
            return true;
        }
        public void Dispose() { }
    }
}
