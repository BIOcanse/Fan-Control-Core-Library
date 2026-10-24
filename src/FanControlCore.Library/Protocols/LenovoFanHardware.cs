using System.Runtime.Versioning;
using FanControlCore.Transport;

namespace FanControlCore.Protocols;

internal interface ILenovoFanHardware : IDisposable
{
    bool IsAvailable { get; }
    string? Identity { get; }
    ulong? ReadCoolingSupported();
    ulong? ReadCooling();
    bool SetCooling(bool enabled);
    ulong? ReadRpm(uint fanId);
    byte[]? ReadTable(uint fanId);
    bool WriteTable(uint fanId, byte[] buffer);
}

[SupportedOSPlatform("windows")]
internal sealed class LenovoFanHardware : ILenovoFanHardware
{
    private readonly WmiMethodChannel gameZone = new(@"root\WMI", "LENOVO_GAMEZONE_DATA");
    private readonly WmiMethodChannel fanMethod = new(@"root\WMI", "LENOVO_FAN_METHOD");
    private readonly Lazy<string?> identity = new(FanMachineIdentity.ReadSystemUuid);

    public bool IsAvailable => gameZone.IsAvailable && fanMethod.IsAvailable;
    public string? Identity => identity.Value;
    public ulong? ReadCoolingSupported() => gameZone.InvokeOrdered("IsSupportFanCooling");
    public ulong? ReadCooling() => gameZone.InvokeOrdered("GetFanCoolingStatus");
    public bool SetCooling(bool enabled) => gameZone.InvokeOrdered("SetFanCooling", enabled ? 1u : 0u) is not null;
    public ulong? ReadRpm(uint fanId) => fanMethod.InvokeOrdered("Fan_GetCurrentFanSpeed", fanId);
    public byte[]? ReadTable(uint fanId) => fanMethod.InvokeOrderedForBuffer("Fan_Get_Table", fanId)
        ?? fanMethod.InvokeOrderedForBuffer("Fan_Get_Table");
    public bool WriteTable(uint fanId, byte[] buffer) => fanMethod.InvokeOrderedWithBuffer("Fan_Set_Table", buffer, fanId);

    public void Dispose() { gameZone.Dispose(); fanMethod.Dispose(); }
}
