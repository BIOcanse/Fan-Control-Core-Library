using System.Diagnostics;
using System.Text.Json;
using FanControlCore;
using FanControlCore.Discovery;

static class CrashRecoveryTests
{
    private static FanCoreOptions Options(string root) => new(Path.Combine(root, "state"), MachineIdentity: () => "test-machine");
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }

    internal static void RecordAndExit(string root)
    {
        var backend = new RecoveryBackend(root);
        var core = new FanCore(backend, Options(root));
        core.Open();
        Check(core.TrySetDuty(0, 40, out var reason), reason ?? "takeover failed");
        Environment.Exit(0); // No Dispose/ProcessExit restoration: OS releases the owner's file handle.
    }

    private static void Crash(string root)
    {
        Directory.CreateDirectory(root);
        var start = new ProcessStartInfo(Environment.ProcessPath!)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(CrashRecoveryTests).Assembly.Location);
        start.ArgumentList.Add("--crash-takeover"); start.ArgumentList.Add(root);
        using var child = Process.Start(start)!;
        if (!child.WaitForExit(10000)) { child.Kill(); child.WaitForExit(); throw new Exception("Crash fixture timed out"); }
        Check(child.ExitCode == 0, child.StandardError.ReadToEnd());
    }

    internal static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "fancore-recovery-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var mode in new[] { "recover", "failure", "identity", "order" })
            {
                var path = Path.Combine(root, mode);
                Crash(path);
                var backend = new RecoveryBackend(path) { FailRestore = mode == "failure", Reversed = mode == "order" };
                var options = mode == "identity" ? Options(path) with { MachineIdentity = () => "different-machine" } : Options(path);
                using (var core = new FanCore(backend, options))
                {
                    core.Open();
                    var success = core.TrySetDuty(backend.Reversed ? 0 : 1, 50, out var reason);
                    Check(success == (mode is "recover" or "order"), "Recovery must precede and gate the next write: " + reason);
                    var operations = File.ReadAllLines(Path.Combine(path, "operations"));
                    if (success)
                        Check(operations.SequenceEqual(new[] { "set:CPU", "auto:CPU", "set:GPU" }), "Stable identity restoration ordering");
                    else
                    {
                        Check(!operations.Contains("set:GPU"), "Failed recovery cannot permit a new write");
                        Check(File.Exists(Path.Combine(path, "state", "temporary-takeovers.json")), "Pending state retained");
                    }
                }
            }
            var persistentRoot = Path.Combine(root, "persistent");
            Directory.CreateDirectory(persistentRoot);
            var persistent = new RecoveryBackend(persistentRoot);
            using (var core = new FanCore(persistent, Options(persistentRoot)))
            {
                core.Open();
                Check(core.TryWriteCurve(0, [new(60, 45)], "firmware", out _), "persistent curve");
                Check(core.TrySetDuty(0, 70, out _), "temporary over persistent curve");
            }
            Check(persistent.PersistentCurve == 45, "Normal cleanup preserves the persistent curve");
            Check(File.ReadAllLines(Path.Combine(persistentRoot, "operations")).Last() == "auto:CPU", "Normal cleanup releases temporary takeover");
            var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(persistentRoot, "state", "temporary-takeovers.json")));
            using (state) Check(state.RootElement.GetProperty("Fans").GetArrayLength() == 0, "Confirmed release clears pending state");
            Console.WriteLine("FanControlCore crash recovery: abrupt owner exit, restore-before-write, failed recovery, identity mismatch, reordered fans, normal cleanup and persistent curves passed.");
        }
        finally
        {
            // Fixed, small test fixture layout: no recursive traversal/deletion.
            if (Directory.Exists(root))
            {
                foreach (var path in Directory.EnumerateDirectories(root))
                {
                    var state = Path.Combine(path, "state");
                    if (Directory.Exists(state)) { foreach (var file in Directory.EnumerateFiles(state)) File.Delete(file); Directory.Delete(state); }
                    foreach (var file in Directory.EnumerateFiles(path)) File.Delete(file);
                    Directory.Delete(path);
                }
                Directory.Delete(root);
            }
        }
    }

    private sealed class RecoveryBackend(string root) : IFanBackend
    {
        public bool FailRestore, Reversed;
        public double PersistentCurve;
        public string Name => "durable-fake";
        private string Fan(int index) => (index == 0) != Reversed ? "CPU" : "GPU";
        public bool TryOpen(out string? reason) { reason = null; return true; }
        public IReadOnlyList<FanDescription> Describe() => Enumerable.Range(0,2).Select(index => new FanDescription(index, Fan(index), true, true,
            Fan(index) == "CPU" ? "cpu" : "gpu", FirmwareCurve: "xy")).ToArray();
        public IReadOnlyList<FanState> Read() => [];
        public bool TrySetDuty(int index, double percent, out string? reason)
        { File.AppendAllLines(Path.Combine(root, "operations"), ["set:" + Fan(index)]); reason = null; return true; }
        public bool TrySetFullSpeed(int index, out string? reason) => TrySetDuty(index, 100, out reason);
        public bool TrySetAutomatic(int index, out string? reason)
        {
            reason = FailRestore ? "Injected release failure" : null;
            if (FailRestore) return false;
            File.AppendAllLines(Path.Combine(root, "operations"), ["auto:" + Fan(index)]);
            return true;
        }
        public bool TryWriteCurve(int index, IReadOnlyList<FanCurvePoint> curve, out string? reason)
        { PersistentCurve = curve[0].Percent; reason = null; return true; }
        public void Dispose() { }
    }
}
