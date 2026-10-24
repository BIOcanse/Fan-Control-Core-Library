namespace FanControlCore.Transport;

internal interface IUniwillFanHardware : IDisposable
{
    bool IsAvailable { get; }
    string? Diagnostic => null;
    byte? Read(ushort address);
    int? ReadBigEndianWord(ushort highAddress, ushort lowAddress);
    bool WriteBit(ushort address, byte bit, bool set, int attempts = 3);
    bool WriteWithRetry(ushort address, byte value, int attempts = 3);
}
