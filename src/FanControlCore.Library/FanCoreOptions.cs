using FanControlCore.Protocols;

namespace FanControlCore;

/// <summary>Explicit host inputs. Embedded consumers must supply a durable state directory.</summary>
/// <param name="StateDirectory">Durable directory for the takeover journal and firmware-table baselines.</param>
/// <param name="MachineIdentity">Stable machine identity for durable recovery; the system UUID by default.</param>
/// <param name="LibreHardwareMonitorComputer">
/// The host's already open <c>LibreHardwareMonitor.Hardware.Computer</c> with its motherboard group enabled. It is the
/// only way the desktop Super I/O route becomes available; the library never opens a <c>Computer</c> of its own.
/// LibreHardwareMonitor 0.9.6 keeps process-wide statics (CPUID/RDTSC code buffer, ISA/PCI/EC mutexes) that
/// <c>Computer.Open</c> replaces and <c>Computer.Close</c> frees, so a second instance in the same process breaks the
/// first one. The route walks only the motherboard tree and never opens, closes, resets or updates it; fan readings
/// are as fresh as the host's own updates. The host keeps it open until the <c>FanCore</c> is disposed.
/// Null (the default) leaves the route unavailable.
/// </param>
public sealed record FanCoreOptions(string? StateDirectory = null, Func<string?>? MachineIdentity = null,
    object? LibreHardwareMonitorComputer = null)
{
    public static FanCoreOptions FromEnvironment() => new(
        Environment.GetEnvironmentVariable("FANCONTROLCORE_STATE_DIRECTORY"));

    internal string? ReadMachineIdentity()
    {
        if (MachineIdentity is { } read) return read();
        if (OperatingSystem.IsWindows()) return FanMachineIdentity.ReadSystemUuid();
        if (OperatingSystem.IsLinux() && File.Exists("/etc/machine-id"))
            return File.ReadAllText("/etc/machine-id").Trim();
        return null;
    }
}
