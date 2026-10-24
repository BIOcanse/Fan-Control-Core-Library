using System.Management;
using System.Runtime.Versioning;
using FanControlCore.Transport;

namespace FanControlCore.Protocols;

/// <summary>
/// HP（OMEN / Victus）平台的风扇通道，走 HP 的 WMI BIOS 信箱。
///
/// 按 README 的分类这条是 **WMI / ACPI**：调的是固件自己的 BIOS 例程，
/// 不往 EC 寄存器里写。
///
/// 这条通道的形状和别家不一样：方法只收**一个结构体**，
/// 结构体里放签名、命令、命令类型、数据长度和数据。
/// 协议事实来自两处公开实现（只取事实，不抄代码）：
/// <list type="bullet">
/// <item>主线内核 <c>drivers/platform/x86/hp/hp-wmi.c</c>（GPL-2.0）：
///   命令 <c>HPWMI_GM = 0x20008</c>；风扇个数 <c>0x10</c>、转速 <c>0x11</c>、
///   全速开关读 <c>0x26</c> 写 <c>0x27</c>。
///   转速用 4 字节出参的第 2、3 字节拼成 <c>(高 &lt;&lt; 8) | 低</c>。</item>
/// <item>OmenHwCtl（Windows 侧）：<c>root\wmi</c> 的 <c>hpqBIntM</c>，
///   方法名是 <c>hpqBIOSInt</c> 加上**出参字节数**（要 4 字节就是 <c>hpqBIOSInt4</c>）；
///   入参实例类是 <c>hpqBDataIn</c>，字段 <c>Sign</c>（"SECU"）、<c>Command</c>、
///   <c>CommandType</c>、<c>Size</c>、<c>hpqBData</c>；
///   出参读 <c>rwReturnCode</c> 和 <c>Data</c>。</item>
/// </list>
///
/// **这条通道没有在 HP 机器上实测过。** 手上没有那样的机器，所以边界画得死：
/// 读不出合理的风扇个数就整条通道不可用，任意占空比一律不声称支持
/// （HP 的自定义转速是 Victus S 那套另外的表接口，要有机器才能验）。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HpWmiFanBackend : IFanBackend
{
    private const string Scope = @"root\WMI";
    private const string ClassName = "hpqBIntM";
    private const string InputClassName = "hpqBDataIn";

    /// <summary>方法名带着出参字节数。要几个字节就调哪一个。</summary>
    private const string MethodPrefix = "hpqBIOSInt";

    /// <summary>入参签名，ASCII "SECU"。</summary>
    private static readonly byte[] Signature = [0x53, 0x45, 0x43, 0x55];

    private const uint CommandGeneralMailbox = 0x0002_0008;
    private const uint QueryFanCount = 0x10;
    private const uint QueryFanSpeed = 0x11;
    private const uint QueryMaximumFanGet = 0x26;
    private const uint QueryMaximumFanSet = 0x27;

    /// <summary>这套接口最多认几个风扇。读出比这更多说明读到的不是风扇个数。</summary>
    private const int MaximumFanCount = 4;

    /// <summary>超出这个范围的"转速"不是转速。</summary>
    private const int MaximumPlausibleRpm = 12_000;

    private readonly WmiMethodChannel wmi = new(Scope, ClassName);
    private int fanCount;
    private bool disposed;

    public string Name => "hp-wmi-bios";

    public bool TryOpen(out string? unavailableReason)
    {
        if (!OperatingSystem.IsWindows())
        {
            unavailableReason = "这条通道只有 Windows 上有。";
            return false;
        }
        if (!wmi.IsAvailable)
        {
            unavailableReason = "这台机器上没有 HP 的 WMI BIOS 接口，或者没有管理员权限。";
            return false;
        }

        // 风扇个数由固件回答。读不出合理的数就不认这条通道 ——
        // 猜一个 2 出来，等于在不认识的机器上开始写风扇。
        var count = Query(QueryFanCount, [0], outputBytes: 4) is { Length: > 0 } data
            ? data[0]
            : 0;
        if (count <= 0 || count > MaximumFanCount)
        {
            unavailableReason = "HP 的 BIOS 接口在，但它没有报出风扇个数。";
            return false;
        }
        fanCount = count;
        unavailableReason = null;
        return true;
    }

    /// <summary>
    /// HP 的全速是**整机一个开关**（<c>0x27</c>），不是每个风扇一个。
    /// 每个风扇都报可写，但它们会一起动。
    /// </summary>
    public IReadOnlyList<FanDescription> Describe()
    {
        var descriptions = new List<FanDescription>(fanCount);
        for (var index = 0; index < fanCount; index++)
        {
            descriptions.Add(new FanDescription(
                index,
                index == 0 ? "CPU 风扇" : $"风扇 {index + 1}",
                Writable: true,
                SupportsDuty: false,
                /*
                 * **这里不报角色。**
                 *
                 * 别家的角色是有出处的编号约定（宏碁 1/4、联想 1/2、华硕按设备 ID），
                 * HP 这条链路的风扇个数是固件现报的，"0 号就是 CPU 风扇"只是个
                 * 合理猜测。上面那个名字只进诊断，猜错了没人看见；
                 * 角色不一样，它会变成界面上的词条 —— 挂一个猜的词条，
                 * 用户会按它去调，调的却可能是另一把风扇。认不出就不挂。
                 */
                /*
                 * HP 这条链路能设固定转速目标，够跑软件曲线；
                 * `GET_FAN_TABLE` 只是报能力和范围，**不是可写的固件曲线**。
                 *
                 * 固定转速的传输还没接（现在只有全速和自动），所以报 preset。
                 * 接上之后改 Software，并且 RequiresPeriodicReassert 要置真：
                 * 这条链路的固件约 120 秒会把控制权收回去。
                 */
                SupportsSoftwareCurve: false));
        }
        return descriptions;
    }

    public IReadOnlyList<FanState> Read()
    {
        var automatic = ReadMaximumFan() != true;
        var states = new List<FanState>(fanCount);
        for (var index = 0; index < fanCount; index++)
        {
            states.Add(new FanState(
                index,
                index == 0 ? "CPU 风扇" : $"风扇 {index + 1}",
                ReadRpm(index),
                // 这条通道给的是转速，不是占空比。没有就是没有。
                DutyPercent: null,
                DutyRaw: null,
                Writable: true,
                SupportsDuty: false,
                AutomaticControl: automatic));
        }
        return states;
    }

    public bool TrySetDuty(int fanIndex, double percent, out string? failureReason)
    {
        failureReason = "HP 的这条通道只有全速和自动两档，给不了任意占空比。";
        return false;
    }

    public bool TrySetFullSpeed(int fanIndex, out string? failureReason)
        => SetMaximumFan(true, "切不到全速模式。", out failureReason);

    public bool TrySetAutomatic(int fanIndex, out string? failureReason)
        => SetMaximumFan(false, "交还固件自动控制失败。", out failureReason);

    /// <summary>
    /// 开关全速，然后**回读核对**。
    /// BIOS 的返回码各机型含义不一，所以不拿它当成功的凭据。
    /// </summary>
    private bool SetMaximumFan(bool enabled, string failureText, out string? failureReason)
    {
        // 这条命令收一个 4 字节整数，不要出参。
        if (Query(
                QueryMaximumFanSet,
                [(byte)(enabled ? 1 : 0), 0, 0, 0],
                outputBytes: 0) is null)
        {
            failureReason = "HP 的 BIOS 接口不认这次调用。";
            return false;
        }
        if (ReadMaximumFan() != enabled)
        {
            failureReason = failureText;
            return false;
        }
        failureReason = null;
        return true;
    }

    private bool? ReadMaximumFan()
        => Query(QueryMaximumFanGet, [0], outputBytes: 4) is { Length: > 0 } data
            ? data[0] != 0
            : null;

    private double? ReadRpm(int fanIndex)
    {
        if (Query(QueryFanSpeed, [(byte)fanIndex], outputBytes: 4) is not { Length: 4 } data)
        {
            return null;
        }
        // 出参的第 2、3 字节拼成转速，高字节在前。
        var rpm = (data[2] << 8) | data[3];
        return rpm >= 0 && rpm <= MaximumPlausibleRpm ? rpm : null;
    }

    /// <summary>
    /// 发一条 BIOS 命令。返回出参数据；这次调用没成功就是 null ——
    /// null 和"全零的数据"是两件事，不要混。
    /// </summary>
    private byte[]? Query(uint commandType, byte[] data, int outputBytes)
    {
        using var input = wmi.CreateInstance(InputClassName);
        if (input is null)
        {
            return null;
        }
        input["Sign"] = Signature;
        input["Command"] = CommandGeneralMailbox;
        input["CommandType"] = commandType;
        input["Size"] = (uint)data.Length;
        input["hpqBData"] = data;

        var output = wmi.InvokeEmbedded($"{MethodPrefix}{outputBytes}", input);
        if (output is null)
        {
            return null;
        }
        try
        {
            // 固件自己说这条没办成，就当没办成 —— 别去读后面那段数据。
            if (output["rwReturnCode"] is { } code
                && Convert.ToInt32(code, System.Globalization.CultureInfo.InvariantCulture) != 0)
            {
                return null;
            }
            return outputBytes == 0
                ? []
                : output["Data"] as byte[];
        }
        catch (Exception error) when (error is ManagementException
            or FormatException
            or InvalidCastException
            or OverflowException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        wmi.Dispose();
    }
}
