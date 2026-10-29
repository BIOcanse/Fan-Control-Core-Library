using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using FanControlCore;
using FanControlCore.Discovery;
using FanControlCore.Profiles;
using FanControlCore.Protocols;
using FanControlCore.Transport;

[SupportedOSPlatform("windows")]
internal static class UniwillFirmwareOwnershipTests
{
    private const string Device = "038f9b6e-eef2-4156-8130-a6eb43de5046";
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception("Uniwill ownership: " + message); }
    private static FanCurvePoint[] Curve(double percent) => [new(45, percent), new(75, percent)];
    private static UniwillWmiFanBackend Backend(Hardware hardware, string? directory, string? device = Device)
    {
        var backend = new UniwillWmiFanBackend(hardware, directory, () => device);
        Check(backend.TryOpen(out _), "injected hardware opens without constructing real transport");
        Check(backend.Describe().All(fan => fan.SupportsFirmwareCurveReset), "descriptions advertise original-table restoration");
        return backend;
    }

    internal static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "fcc-uniwill-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            TestRpmLayouts();
            TestDutyCapabilityReceipts();
            TestSiblingCurveAndTemporaryDuty(Path.Combine(root, "initial-off"), false);
            TestSiblingCurveAndTemporaryDuty(Path.Combine(root, "initial-on"), true);
            TestRestartAndProtocol(Path.Combine(root, "restart"));
            TestDutyToCurveAndFailedReplacement(Path.Combine(root, "replacement"));
            TestRestoreFailures(Path.Combine(root, "restore"));
            TestFailedWritesKeepCleanupOwnership(Path.Combine(root, "failed-write"));
            TestCompletionSaveFailure(Path.Combine(root, "completion-save"));
            TestMissingOrInvalidStorage(Path.Combine(root, "invalid"));
        }
        finally { DeleteOwnedTree(root); }
    }

    private static void TestDutyCapabilityReceipts()
    {
        foreach (var writable in new[] { true, false })
        {
            var hardware = new Hardware(false);
            hardware.Values[UniwillFanProfileTable.FanControlStatus] = writable
                ? UniwillFanProfileTable.HasFanControlBit : (byte)0;
            using var core = new FanCore(new UniwillWmiFanBackend(hardware, null, () => Device));
            Check(core.Open(), "capability receipt opens read-only and writable hardware");
            using var input = new StringReader("{\"id\":1,\"op\":\"describe\"}\n{\"id\":2,\"op\":\"read\"}\n");
            using var output = new StringWriter();
            FanCommandServer.Run(core, input, output);
            var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Check(lines.Length == 2, "capability receipt protocol returned two responses");
            using var description = JsonDocument.Parse(lines[0]);
            using var reading = JsonDocument.Parse(lines[1]);
            var described = description.RootElement.GetProperty("fans");
            var read = reading.RootElement.GetProperty("fans");
            Check(described.GetArrayLength() == 2 && read.GetArrayLength() == 2, "both fans report capability");
            for (var index = 0; index < 2; index++)
            {
                Check(described[index].GetProperty("supportsDuty").GetBoolean() == writable,
                    "description reflects firmware permission");
                Check(read[index].GetProperty("supportsDuty").GetBoolean() == writable,
                    "real read response agrees with description");
            }
            Check(hardware.Writes.Count == 0, "capability reads issue no hardware writes");
        }
    }

    private static void TestRpmLayouts()
    {
        foreach (var (manufacturer, model, project, oem) in new (string?, string?, byte?, bool)[]
        {
            ("MECHREVO", "JIAOLONG Series", 0x1A, true),
            (" mechrevo ", " jiaolong series ", 0x1A, true),
            ("MECHREVO", "JIAOLONG Series", 0x1B, false),
            ("MECHREVO", "JIAOLONG Series", null, false),
            ("MECHREVO", "JIAOLONG", 0x1A, false),
            ("MECHREVO", "Other Series", 0x1A, false),
            ("Other", "JIAOLONG Series", 0x1A, false),
            (null, null, 0x1A, false)
        })
        {
            var hardware = new Hardware(false);
            hardware.Values[UniwillFanProfileTable.MainFanRpmHigh] = 0x12;
            hardware.Values[UniwillFanProfileTable.MainFanRpmLow] = 0x34;
            hardware.Values[UniwillFanProfileTable.SecondFanRpmHigh] = 9;
            hardware.Values[0x046B] = 145;
            hardware.Values[0x046D] = 17;
            if (project is { } value) hardware.Values[UniwillFanProfileTable.ProjectId] = value;
            else hardware.MissingReads.Add(UniwillFanProfileTable.ProjectId);
            using var backend = new UniwillWmiFanBackend(hardware, null, () => Device, () => (manufacturer, model));
            Check(backend.TryOpen(out _), "RPM fixture opens");
            Check(backend.Read()[0].Rpm == 0x1234, "main fan layout is unchanged");
            Check(backend.Read()[1].Rpm == (oem ? 2449 : 2321), "RPM selects only the verified OEM combination");
            Check(hardware.Writes.Count == 0, "model detection and RPM read perform no writes");
            var low = oem ? (ushort)0x046B : (ushort)0x046D;
            hardware.Values[UniwillFanProfileTable.SecondFanRpmHigh] = 0;
            hardware.Values[low] = 0;
            Check(backend.Read()[1].Rpm == 0, "zero RPM remains a reading");
            hardware.MissingReads.Add(low);
            Check(backend.Read()[1].Rpm is null, "missing selected byte is unknown, not fallback or zero");
        }
    }

    private static void TestSiblingCurveAndTemporaryDuty(string directory, bool initiallyEnabled)
    {
        var hardware = new Hardware(initiallyEnabled);
        var original0 = hardware.Table(0); var original1 = hardware.Table(1);
        var originalFlag = hardware.Values[UniwillFanTable.UseCustomTablesRegister];
        var backend = new UniwillWmiFanBackend(hardware, directory, () => Device);
        using var core = new FanCore(backend);
        Check(core.Open(), "core opens fake hardware");
        Check(core.TryWriteCurve(0, Curve(60), "firmware", out _), "first persistent curve");
        Check(core.TryWriteCurve(0, Curve(80), "firmware", out _), "curve update keeps first original");
        var persistent = hardware.Table(0);
        Check(core.TrySetDuty(1, 30, out _), "other fan temporary duty");
        core.RestoreAll();
        Check(hardware.Table(1).SequenceEqual(original1), "shutdown restores only temporary duty table");
        Check(hardware.Table(0).SequenceEqual(persistent) && hardware.CustomEnabled, "sibling persistent curve remains enabled");
        Check(core.TrySetDuty(0, 25, out _), "temporary duty over own persistent curve");
        Check(core.TrySetAutomatic(0, out _), "temporary duty handback");
        Check(hardware.Table(0).SequenceEqual(persistent) && hardware.CustomEnabled, "handback returns to preceding persistent curve");
        // Through FanCore a boost needs the control group under software control; the backend's own boost is what
        // this checks: setting and clearing the machine-wide bit leaves the firmware tables alone.
        Check(backend.TrySetFullSpeed(1, out _), "temporary global boost");
        Check(backend.TrySetAutomatic(1, out _), "boost handback leaves firmware tables alone");
        Check(hardware.Table(0).SequenceEqual(persistent) && hardware.CustomEnabled, "boost does not disable custom curve");
        Check(core.TrySetAutomatic(0, out _, resetFirmwareCurve: true), "explicit curve cancel");
        Check(hardware.Table(0).SequenceEqual(original0), "raw first original restored rather than latest edit");
        Check(hardware.Values[UniwillFanTable.UseCustomTablesRegister] == originalFlag, "last table returns original global bit and preserves unrelated bits");
        Check(!Directory.EnumerateFiles(directory, "*.json").Any(), "confirmed last release removes baseline");
        Check(hardware.Table(1).SequenceEqual(original1), "curve cancellation leaves sibling raw table untouched");
    }

    private static void TestRestartAndProtocol(string directory)
    {
        var hardware = new Hardware(false);
        var original0 = hardware.Table(0); var original1 = hardware.Table(1);
        using (var backend = Backend(hardware, directory))
        {
            Check(backend.TryWriteCurve(0, Curve(50), out _), "fan zero owns persistent table");
            Check(backend.TryWriteCurve(1, Curve(90), out _), "fan one owns persistent table");
        }
        var curve1 = hardware.Table(1);
        using var core = new FanCore(new UniwillWmiFanBackend(hardware, directory, () => Device));
        Check(core.Open(), "new helper opens");
        using var input = new StringReader("{\"id\":17,\"op\":\"auto\",\"fan\":0,\"resetFirmwareCurve\":true}\n");
        using var output = new StringWriter();
        FanCommandServer.Run(core, input, output);
        using var receipt = JsonDocument.Parse(output.ToString().Trim());
        Check(receipt.RootElement.GetProperty("ok").GetBoolean()
            && receipt.RootElement.GetProperty("firmwareCurveReset").GetBoolean(), "protocol confirms actual curve restoration");
        Check(hardware.Table(0).SequenceEqual(original0) && hardware.Table(1).SequenceEqual(curve1) && hardware.CustomEnabled,
            "restart cancellation affects only selected table and leaves global enabled for sibling");
        Check(core.TrySetAutomatic(1, out _, resetFirmwareCurve: true), "last sibling cancels");
        Check(hardware.Table(1).SequenceEqual(original1) && !hardware.CustomEnabled, "original off bit returns after last release");
        Check(!Directory.EnumerateFiles(directory, "*.json").Any(), "no completed restoration file remains");
    }

    private static void TestDutyToCurveAndFailedReplacement(string directory)
    {
        var hardware = new Hardware(false);
        var original = hardware.Table(0);
        using (var core = new FanCore(new UniwillWmiFanBackend(hardware, directory, () => Device)))
        {
            Check(core.Open() && core.TrySetDuty(0, 20, out _), "temporary duty first");
            Check(core.TryWriteCurve(0, Curve(70), "firmware", out _), "persistent curve replaces duty");
            var curve = hardware.Table(0);
            core.RestoreAll();
            Check(hardware.Table(0).SequenceEqual(curve), "successful replacement removes temporary cleanup ownership");
            Check(core.TrySetAutomatic(0, out _, resetFirmwareCurve: true), "replacement curve cancels");
            Check(hardware.Table(0).SequenceEqual(original), "curve baseline came from pre-duty original, not temporary fixed table");

            Check(core.TrySetDuty(0, 40, out _), "another temporary duty");
            Check(!core.TryWriteCurve(0, [new(45, 50), new(double.NaN, 70)], "firmware", out _), "mixed invalid points rejected");
            core.RestoreAll();
            Check(hardware.Table(0).SequenceEqual(original), "failed replacement retains prior temporary cleanup ownership");

            Check(core.TrySetDuty(0, 35, out _), "temporary duty before transport failure");
            hardware.Rejected.Add(UniwillFanTable.CpuEndTemperature);
            Check(!core.TryWriteCurve(0, Curve(85), "firmware", out _), "partial curve write failure");
            hardware.Rejected.Clear();
            core.RestoreAll();
            Check(hardware.Table(0).SequenceEqual(original), "partial replacement retains prior table original for cleanup");
            Check(!hardware.CustomEnabled && !Directory.EnumerateFiles(directory, "*.json").Any(),
                "failed first persistent edit does not keep a global-table claim after cleanup");
        }
    }

    private static void TestRestoreFailures(string directory)
    {
        var hardware = new Hardware(false);
        var original = hardware.Table(0);
        using (var backend = Backend(hardware, directory))
            Check(backend.TrySetDuty(0, 80, out _), "duty baseline captured");
        var path = Directory.EnumerateFiles(directory, "*.json").Single();
        using var restarted = Backend(hardware, directory);
        hardware.Ignored.Add(UniwillFanTable.CpuFanSpeed);
        Check(!restarted.TrySetAutomatic(0, out _), "successful write receipt without raw restoration is rejected");
        Check(File.Exists(path), "failed readback keeps durable baseline");
        hardware.Ignored.Clear();
        hardware.Rejected.Add(UniwillFanTable.UseCustomTablesRegister);
        Check(!restarted.TrySetAutomatic(0, out _), "global bit handback failure");
        Check(File.Exists(path), "failed global handback keeps baseline");
        hardware.Rejected.Clear();
        Check(restarted.TrySetAutomatic(0, out _), "restoration retry succeeds");
        Check(hardware.Table(0).SequenceEqual(original) && !hardware.CustomEnabled && !File.Exists(path), "retry confirms raw original and releases record");

        hardware.Rejected.Add(UniwillFanTable.CpuFanSpeed);
        Check(!restarted.TrySetDuty(0, 60, out _), "failed initial write retains original");
        hardware.Rejected.Clear();
        Check(restarted.TrySetAutomatic(0, out _), "failed initial write is recoverable");
        Check(hardware.Table(0).SequenceEqual(original), "failed initial mutation still restores original table");
    }

    private static void TestFailedWritesKeepCleanupOwnership(string directory)
    {
        var hardware = new Hardware(false);
        var original = hardware.Table(0);
        using var core = new FanCore(new UniwillWmiFanBackend(hardware, directory, () => Device));
        Check(core.Open(), "failed-write fixture opens");
        hardware.Rejected.Add(UniwillFanTable.CpuEndTemperature);
        Check(!core.TryWriteCurve(0, Curve(85), "firmware", out _), "first curve partially fails");
        core.RestoreAll();
        Check(hardware.CustomEnabled, "still rejected restore keeps pending ownership");
        hardware.Rejected.Clear();
        core.RestoreAll();
        Check(hardware.Table(0).SequenceEqual(original) && !hardware.CustomEnabled,
            "second cleanup restores partial first curve and original global bit");
        Check(!Directory.EnumerateFiles(directory, "*.json").Any(), "unapplied first curve has no persistent claim");

        hardware.Rejected.Add(UniwillFanTable.CpuFanSpeed);
        Check(!core.TrySetDuty(0, 90, out _), "first duty mutation can fail after enabling custom mode");
        core.RestoreAll();
        hardware.Rejected.Clear();
        core.RestoreAll();
        Check(hardware.Table(0).SequenceEqual(original) && !hardware.CustomEnabled, "failed duty still participates in cleanup retry");

        Check(core.TryWriteCurve(0, Curve(50), "firmware", out _), "persistent curve before failed edit");
        var previousCurve = hardware.Table(0);
        hardware.Rejected.Add(UniwillFanTable.CpuEndTemperature);
        Check(!core.TryWriteCurve(0, Curve(80), "firmware", out _), "persistent edit partially fails");
        hardware.Rejected.Clear();
        core.RestoreAll();
        Check(hardware.Table(0).SequenceEqual(previousCurve) && hardware.CustomEnabled,
            "failed edit returns to previous applied persistent curve while keeping original baseline");
        Check(core.TrySetAutomatic(0, out _, resetFirmwareCurve: true), "previous persistent curve can be canceled");
        Check(hardware.Table(0).SequenceEqual(original) && !hardware.CustomEnabled, "cancellation still restores first original");
    }

    private static void TestCompletionSaveFailure(string directory)
    {
        var hardware = new Hardware(false);
        var original = hardware.Table(0);
        using var core = new FanCore(new UniwillWmiFanBackend(hardware, directory, () => Device));
        Check(core.Open(), "completion-save fixture opens");
        FileStream? blocked = null;
        hardware.BeforeWrite = () =>
        {
            if (blocked is not null) return;
            var path = Directory.EnumerateFiles(directory, "*.json").Single();
            blocked = new FileStream(path + ".tmp", FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        };
        try
        {
            Check(!core.TryWriteCurve(0, Curve(80), "firmware", out _), "completion save failure is not reported as applied");
            Check(!hardware.Table(0).SequenceEqual(original), "table changed before completion metadata failed");
        }
        finally { blocked?.Dispose(); hardware.BeforeWrite = null; }
        core.RestoreAll();
        Check(hardware.Table(0).SequenceEqual(original) && !hardware.CustomEnabled,
            "failed completion still owns cleanup and restores original mode");
        Check(!Directory.EnumerateFiles(directory, "*.json").Any(), "failed completion leaves no persistent curve claim");
    }

    private static void TestMissingOrInvalidStorage(string directory)
    {
        Directory.CreateDirectory(directory);
        var hardware = new Hardware(true);
        using (var noStore = Backend(hardware, null))
            Check(!noStore.TryWriteCurve(0, Curve(50), out _), "no storage prevents firmware mutation");
        using (var noIdentity = Backend(hardware, directory, null))
            Check(!noIdentity.TrySetDuty(0, 40, out _), "no device identity prevents table mutation");
        Check(hardware.Writes.Count == 0, "preconditions fail before any write");
        var blocked = Path.Combine(directory, "blocked"); File.WriteAllText(blocked, "not a directory");
        using (var backend = Backend(hardware, blocked))
            Check(!backend.TryWriteCurve(0, Curve(70), out _), "unwritable state path prevents mutation");
        Check(hardware.Writes.Count == 0, "unwritable storage prevents mode or table changes");
        var readable = Path.Combine(directory, "readable");
        using var valid = Backend(hardware, readable);
        Check(!valid.TryResetFirmwareCurve(0, out _), "missing original cannot fabricate a reset");
        Check(hardware.Writes.Count == 0, "missing reset baseline does not clear global bit");
        hardware.MissingReads.Add(UniwillFanTable.CpuStartTemperature);
        Check(!valid.TrySetDuty(0, 45, out _), "incomplete original table rejects takeover");
        Check(hardware.Writes.Count == 0, "unreadable original prevents mode mutation");
        hardware.MissingReads.Clear();
        Check(valid.TryWriteCurve(0, Curve(60), out _), "valid baseline saved");
        var path = Directory.EnumerateFiles(readable, "*.json").Single();
        var json = File.ReadAllText(path);
        var writes = hardware.Writes.Count;
        File.WriteAllText(path, "{}");
        Check(!valid.TrySetDuty(0, 40, out _) && !valid.TryResetFirmwareCurve(0, out _), "missing fields cannot become zero defaults");
        Check(hardware.Writes.Count == writes && File.ReadAllText(path) == "{}", "corrupt baseline remains unchanged");
        var document = JsonNode.Parse(json)!;
        document["Fans"]!["0"]!["CurveOriginal"] = Convert.ToBase64String([1]);
        File.WriteAllText(path, document.ToJsonString());
        Check(!valid.TryResetFirmwareCurve(0, out _), "invalid raw table length rejected");
        Check(hardware.Writes.Count == writes, "bad raw table is never written");
        File.WriteAllText(path, json);
        using (var otherDevice = Backend(hardware, readable, "other-device"))
            Check(!otherDevice.TryResetFirmwareCurve(0, out _), "another UUID cannot restore this table");
        Check(hardware.Writes.Count == writes, "foreign device leaves hardware unchanged");
        Check(valid.TryResetFirmwareCurve(0, out _), "valid original remains recoverable after invalid attempts");
    }

    private static void DeleteOwnedTree(string root)
    {
        var pending = new Stack<string>(); var directories = new List<string>(); pending.Push(root);
        var visited = 0;
        while (pending.TryPop(out var directory))
        {
            if (++visited > 256) throw new IOException("Test cleanup bound exceeded.");
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

    private sealed class Hardware : IUniwillFanHardware
    {
        internal Dictionary<ushort, byte> Values { get; } = [];
        internal HashSet<ushort> Rejected { get; } = []; internal HashSet<ushort> Ignored { get; } = [];
        internal HashSet<ushort> MissingReads { get; } = [];
        internal List<(ushort Address, byte Value)> Writes { get; } = [];
        internal bool CustomEnabled => (Values[UniwillFanTable.UseCustomTablesRegister] & UniwillFanTable.UseCustomTablesBit) != 0;
        internal Hardware(bool initiallyEnabled)
        {
            Values[UniwillFanProfileTable.FanControlStatus] = UniwillFanProfileTable.HasFanControlBit;
            Values[UniwillFanProfileTable.FanModeRegister] = 0x81;
            Values[UniwillFanTable.UseCustomTablesRegister] = (byte)(initiallyEnabled ? 0x85 : 0x81);
            for (var fan = 0; fan < 2; fan++)
            {
                var (end, start, speed) = UniwillFanTable.AddressesFor(fan);
                for (var zone = 0; zone < UniwillFanTable.ZoneCount; zone++)
                {
                    Values[(ushort)(end + zone)] = (byte)(40 + zone * 4);
                    Values[(ushort)(start + zone)] = (byte)(38 + zone * 4);
                    Values[(ushort)(speed + zone)] = (byte)(30 + zone + fan * 20);
                }
            }
        }
        internal byte[] Table(int fan)
        {
            var (end, start, speed) = UniwillFanTable.AddressesFor(fan);
            return new[] { end, start, speed }.SelectMany(address => Enumerable.Range(0, UniwillFanTable.ZoneCount)
                .Select(zone => Values[(ushort)(address + zone)])).ToArray();
        }
        public bool IsAvailable => true;
        public byte? Read(ushort address) => MissingReads.Contains(address) ? null : Values.GetValueOrDefault(address);
        public int? ReadBigEndianWord(ushort highAddress, ushort lowAddress)
            => Read(highAddress) is { } high && Read(lowAddress) is { } low ? (high << 8) | low : null;
        public bool WriteBit(ushort address, byte bit, bool set, int attempts = 3)
        {
            var value = Values.GetValueOrDefault(address);
            return WriteWithRetry(address, set ? (byte)(value | bit) : (byte)(value & ~bit), attempts);
        }
        public bool WriteWithRetry(ushort address, byte value, int attempts = 3)
        {
            BeforeWrite?.Invoke();
            Writes.Add((address, value));
            if (Rejected.Contains(address)) return false;
            if (!Ignored.Contains(address)) Values[address] = value;
            return true;
        }
        public void Dispose() { }
        internal Action? BeforeWrite { get; set; }
    }
}
