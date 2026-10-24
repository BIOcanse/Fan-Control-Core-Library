using System.Management;
using System.Runtime.Versioning;

namespace FanControlCore.Protocols;

[SupportedOSPlatform("windows")]
internal static class FanMachineIdentity
{
    internal static (string? Manufacturer, string? Model) ReadSystemModel()
    {
        try
        {
            using var query = new ManagementObjectSearcher("SELECT Manufacturer, Model FROM Win32_ComputerSystem");
            using var results = query.Get();
            foreach (ManagementObject result in results)
            {
                using (result)
                    return (result["Manufacturer"]?.ToString(), result["Model"]?.ToString());
            }
        }
        catch (Exception error) when (error is ManagementException or UnauthorizedAccessException or InvalidOperationException) { }
        return (null, null);
    }

    internal static string? ReadSystemUuid()
    {
        try
        {
            using var query = new ManagementObjectSearcher("SELECT UUID FROM Win32_ComputerSystemProduct");
            using var results = query.Get();
            foreach (ManagementObject result in results)
            {
                using (result)
                {
                    if (Guid.TryParse(result["UUID"]?.ToString(), out var uuid)
                        && uuid != Guid.Empty && uuid != new Guid("ffffffff-ffff-ffff-ffff-ffffffffffff"))
                        return uuid.ToString("D");
                }
            }
        }
        catch (Exception error) when (error is ManagementException or UnauthorizedAccessException or InvalidOperationException) { }
        return null;
    }
}
