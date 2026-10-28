using System.Management;
using System.Runtime.Versioning;

namespace FanControlCore.Transport;

/// <summary>
/// Uniwill / Tongfang 的 EC RAM 通道，走**厂商固件自己的 ACPI WMI 接口**。
///
/// 这一点很重要：我们没有自己写内核驱动，也没有直接捅 EC 的 I/O 端口 ——
/// 调的是固件已经暴露出来的方法。所以在开着内存完整性（HVCI）的机器上照样能用，
/// 而 WinRing0 那类端口 I/O 驱动在那些机器上根本加载不了。
///
/// 协议取自公开的 GPL 实现（tuxedo-drivers 的 uniwill_wmi）：
/// GUID <c>ABBC0F6F-…</c>，method id 4，入参是一个 UInt64 ——
/// 低 32 位是参数，第 5 个字节是功能号（0 写 / 1 读）。读的参数就是 16 位 EC 地址，
/// 写的参数是 <c>数据 &lt;&lt; 16 | 地址</c>；返回值低字节是数据。
///
/// Windows 上这个 GUID 落在 <c>root\WMI</c> 的 <c>AcpiTest_MULong</c> ——
/// Uniwill 复用了微软 ACPI 示例 MOF 的 GUID，所以类名看着毫不相干。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UniwillWmiEcTransport : IUniwillFanHardware
{
    private const string Scope = @"root\WMI";
    private const string ClassName = "AcpiTest_MULong";
    private const string MethodName = "GetSetULong";
    private const string InstanceName = @"ACPI\PNP0C14\1_0";
    private const ulong FunctionRead = 1;
    private const ulong FunctionWrite = 0;
    private const int FunctionBitShift = 40;
    private const uint CallFailed = 0xFEFE_FEFE;

    /// <summary>
    /// 一次读取要连续读到几次相同才作数。
    ///
    /// 直接读 EC 会偶发撞上固件自己的写入，返回一个假的 <c>0x00</c>。
    /// 在风扇这件事上，把假的 0 当成"转速为零"会直接导致误判。
    /// </summary>
    private const int AgreeingReads = 3;
    private const int ReadAttempts = 6;

    /// <summary>
    /// 这条通道的名字。同一台机器上任何用这条 ACPI WMI 接口的进程都该用它
    /// —— 包括宿主程序自己直接读 EC 的那部分。名字变了就等于没有锁。
    /// </summary>
    public const string ChannelName = "Uniwill-AcpiWmi-EcChannel";

    private readonly object gate = new();
    private readonly EcChannelLock channel = new(ChannelName);
    // 找设备、发方法、设备失效之后重新解析 —— 那些是所有 WMI 通道共同的事。
    // 这条通道自己负责的只有 EC 那套参数编码。
    private readonly WmiMethodChannel wmi = new(Scope, ClassName, InstanceName);
    private bool disposed;

    /// <summary>这台机器有没有这条通道。</summary>
    public bool IsAvailable => wmi.IsAvailable;
    public FanOpenFailureKind AvailabilityFailureKind => wmi.AvailabilityFailureKind;
    public string? Diagnostic => channel.Diagnostic;

    /// <summary>读一个 EC 字节。读到连续一致的值才返回，否则 null。</summary>
    public byte? Read(ushort address)
        => channel.Hold(() => ReadHeld(address), out var value) ? value : null;

    private byte? ReadHeld(ushort address)
    {
        lock (gate)
        {
            var agreed = 0;
            byte? last = null;
            for (var attempt = 0; attempt < ReadAttempts; attempt++)
            {
                if (Invoke((ulong)address | (FunctionRead << FunctionBitShift)) is not { } value)
                {
                    return null;
                }
                var current = (byte)(value & 0xFF);
                if (last == current)
                {
                    agreed++;
                    if (agreed >= AgreeingReads - 1)
                    {
                        return current;
                    }
                }
                else
                {
                    agreed = 0;
                    last = current;
                }
            }
            return null;
        }
    }

    /// <summary>
    /// 读两个相邻字节拼成的 16 位**大端**值。转速就是这么存的：低地址放高字节。
    /// </summary>
    public int? ReadBigEndianWord(ushort highAddress, ushort lowAddress)
        => channel.Hold(
            // 两个字节要在同一次独占里读完，否则中间被别人插进去就读到半新半旧的转速。
            () => ReadHeld(highAddress) is { } high && ReadHeld(lowAddress) is { } low
                ? (high << 8) | low
                : (int?)null,
            out var value)
            ? value
            : null;

    /// <summary>
    /// 写一个 EC 字节，然后**回读核对**。
    ///
    /// 这个固件接口对任何调用都返回成功，所以"写成功"只能由回读来定义。
    /// </summary>
    public bool Write(ushort address, byte value)
        // 写和回读之间不能松手：松手了回读到的可能是别人写进去的。
        => channel.Hold(
            () => SendWrite(address, value) && ReadHeld(address) == value,
            out var written) && written;

    /// <summary>
    /// 改一个 EC 寄存器里的**一位**，其余位保持固件当时的值。
    ///
    /// 和 <see cref="Write"/> 的区别在于**核对什么**。这类寄存器是和固件共用的，
    /// 固件随时会动里面别的位；整字节回读相等会因为别人改了别的位而判成失败。
    /// 实测就撞到过：把风扇交还固件时偶发报一次失败，而风扇那时正停在全速上 ——
    /// 位其实已经清掉了，只是同一个字节里另一位跟着变了。所以这里只核对那一位。
    /// </summary>
    public bool WriteBit(ushort address, byte bit, bool set, int attempts = 3)
        => channel.Hold(() => WriteBitHeld(address, bit, set, attempts), out var written)
            && written;

    private bool WriteBitHeld(ushort address, byte bit, bool set, int attempts)
    {
        var attempt = 0;
        while (attempt < attempts)
        {
            attempt++;
            if (ReadHeld(address) is not { } current)
            {
                continue;
            }
            var wanted = set ? (byte)(current | bit) : (byte)(current & ~bit);
            if (current == wanted)
            {
                return true;
            }
            if (SendWrite(address, wanted)
                && ReadHeld(address) is { } after
                && ((after & bit) != 0) == set)
            {
                return true;
            }
        }
        return false;
    }

    private bool SendWrite(ushort address, byte value)
    {
        lock (gate)
        {
            return Invoke((ulong)address
                | ((ulong)value << 16)
                | (FunctionWrite << FunctionBitShift)) is not null;
        }
    }

    /// <summary>
    /// 写一个 EC 字节，写不进就重试。
    ///
    /// 风扇表那一段（0x0F00 起）的写入**本来就会偶发失败** —— 固件自己也在动它，
    /// 参考实现对这一段同样是带重试写的。单次写加回读会稳定地失败在半路，
    /// 而失败在半路比失败得干脆更糟：表已经改了一半。
    /// </summary>
    public bool WriteWithRetry(ushort address, byte value, int attempts = 3)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            if (Write(address, value))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 发一条 EC 命令。<c>0xFEFEFEFE</c> 是这个固件说"这条我不认"的方式 ——
    /// 它对别的一切都返回成功，所以只有这一个码能当失败看。
    /// </summary>
    private uint? Invoke(ulong data)
        => wmi.Invoke(MethodName, new Dictionary<string, object?> { ["Data"] = data })
            is { } result
            && result.TryGetValue("Return", out var returned)
            && returned is uint value
            && value != CallFailed
                ? value
                : null;

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        wmi.Dispose();
        channel.Dispose();
    }
}
