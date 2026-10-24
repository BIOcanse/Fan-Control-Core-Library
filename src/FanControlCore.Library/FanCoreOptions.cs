using FanControlCore.Protocols;

namespace FanControlCore;

/// <summary>Explicit host paths. Embedded consumers must supply a durable state directory.</summary>
public sealed record FanCoreOptions(string? StateDirectory = null, string? LibreHardwareMonitorPath = null,
    Func<string?>? MachineIdentity = null)
{
    public static FanCoreOptions FromEnvironment() => new(
        Environment.GetEnvironmentVariable("FANCONTROLCORE_STATE_DIRECTORY"),
        Environment.GetEnvironmentVariable("FANCONTROLCORE_LHM_PATH"));

    internal string? ReadMachineIdentity()
    {
        if (MachineIdentity is { } read) return read();
        if (OperatingSystem.IsWindows()) return FanMachineIdentity.ReadSystemUuid();
        if (OperatingSystem.IsLinux() && File.Exists("/etc/machine-id"))
            return File.ReadAllText("/etc/machine-id").Trim();
        return null;
    }
}
