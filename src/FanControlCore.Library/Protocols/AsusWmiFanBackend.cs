using System.Runtime.Versioning;
using FanControlCore.Transport;

namespace FanControlCore.Protocols;

/// <summary>
/// ASUS 平台的风扇通道，走厂商固件的 ATK WMI 接口。
///
/// 按 README 的分类这条是 **WMI / ACPI**，不是 Raw EC ——
/// 调的是固件自己暴露的两个方法，不往寄存器里写。
///
/// 协议事实取自主线内核的 <c>asus-wmi</c>
/// （<c>include/linux/platform_data/x86/asus-wmi.h</c>，GPL-2.0，只取事实不抄代码）：
/// <list type="bullet">
/// <item>管理 GUID <c>97845ED0-4E6D-11DE-8A39-0800200C9A66</c>，
///   在 Windows 上落成 <c>root\WMI</c> 的 <c>AsusAtkWmi_WMNB</c>。</item>
/// <item><c>DSTS</c>（读一项状态）和 <c>DEVS</c>（设一项状态），按设备 ID 寻址。</item>
/// <item>CPU 风扇 <c>0x00110013</c>，GPU 风扇 <c>0x00110014</c>，
///   中间那个风扇 <c>0x00110031</c>；DSTS 回的是**转速除以 100**。</item>
/// <item>风扇模式 <c>0x00110012</c>：0 = 交还固件自动，1 = 全速。</item>
/// </list>
///
/// **这条通道没有在 ASUS 机器上实测过。** 手上没有那样的机器。
/// 所以它的安全边界画得很死：`DSTS` 读不出合理转速就整条通道不可用；
/// 任意占空比一律不声称支持（ASUS 的自定义曲线是另一套八点表接口，
/// 不是"设一个占空比"，见 <c>0x00110024</c> —— 那要等有机器验证再说）。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AsusWmiFanBackend : IFanBackend
{
    private const string Scope = @"root\WMI";
    private const string ClassName = "AsusAtkWmi_WMNB";
    private const string ReadMethod = "DSTS";
    private const string WriteMethod = "DEVS";

    /// <summary>DSTS 回的值里，低 16 位才是数据；高位是"这一项存不存在"之类的标志。</summary>
    private const uint DataMask = 0x0000_FFFF;

    /// <summary>这一项固件认不认。不认的话 DSTS 回的值里没有这一位。</summary>
    private const uint PresenceBit = 0x0001_0000;

    /// <summary>DSTS 回的是转速除以 100。</summary>
    private const int RpmScale = 100;

    /// <summary>超出这个范围的"转速"不是转速，是我们把某个别的字段当成了转速。</summary>
    private const int MaximumPlausibleRpm = 12_000;

    private const uint FanModeDevice = 0x0011_0012;
    private const uint FanModeAutomatic = 0;
    private const uint FanModeFullSpeed = 1;

    /// <summary>这台机器上认出来的风扇，按设备 ID 排好。</summary>
    private readonly List<(uint Device, string Name, string Role)> fans = [];

    private readonly WmiMethodChannel wmi = new(Scope, ClassName);
    private bool disposed;

    /// <summary>
    /// 候选风扇。哪几个真的在，由固件的"存在位"回答。
    ///
    /// **角色跟着设备 ID 走**：这两个 ID 在华硕这条链路上就是 CPU 和显卡侧，
    /// 认得出来就照实报，上层拿它当词条。
    /// 第三个"中央风扇"各机型吹的东西不一样（有的压 VRM，有的只是中置排风），
    /// **认不出就不挂** —— 挂一个猜的角色会让用户按它去调，调的却是另一把。
    /// </summary>
    private static readonly (uint Device, string Name, string Role)[] CandidateFans =
    [
        (0x0011_0013, "CPU 风扇", FanRoles.Cpu),
        (0x0011_0014, "显卡风扇", FanRoles.Gpu),
        (0x0011_0031, "中央风扇", FanRoles.Unknown)
    ];

    public string Name => "asus-atk-wmi";

    public bool TryOpen(out string? unavailableReason)
    {
        if (!OperatingSystem.IsWindows())
        {
            unavailableReason = "这条通道只有 Windows 上有。";
            return false;
        }
        if (!wmi.IsAvailable)
        {
            unavailableReason = "这台机器上没有 ASUS 的 ATK WMI 接口，或者没有管理员权限。";
            return false;
        }

        fans.Clear();
        foreach (var candidate in CandidateFans)
        {
            if (ReadRpm(candidate.Device) is not null)
            {
                fans.Add(candidate);
            }
        }
        if (fans.Count == 0)
        {
            unavailableReason = "ATK 接口在，但它没有报出任何风扇。";
            return false;
        }
        unavailableReason = null;
        return true;
    }

    /// <summary>
    /// ASUS 的全速/自动是**整机一个开关**（<c>0x00110012</c>），不是每个风扇一个。
    /// 所以每个风扇都报可写，但它们会一起动 —— 这一点由调用方的界面去表达。
    /// </summary>
    public IReadOnlyList<FanDescription> Describe()
    {
        var descriptions = new List<FanDescription>(fans.Count);
        for (var index = 0; index < fans.Count; index++)
        {
            descriptions.Add(new FanDescription(
                index,
                fans[index].Name,
                Writable: true,
                // ASUS 的任意占空比走另一套八点曲线表，不是"设一个占空比"。
                // 没有机器验证之前不声称支持。
                SupportsDuty: false,
                Role: fans[index].Role,
                /*
                 * **这条链路有真正的固件曲线：8 个温度点 + 8 个转速点**
                 * （payload 固定 16 字节，CPU / GPU / 中置风扇各一个入口，
                 * 还能写升降温滞回）。
                 *
                 * 但传输层还没接，所以这里如实报 preset —— 现在能做的
                 * 确实只有切模式。**不能先声明再说**：声明了界面就会给出
                 * 一个拖得动却写不下去的曲线图。接上之后改成 FirmwareXy，
                 * 并把 CurvePoints 填 8。
                 */
                SupportsSoftwareCurve: false));
        }
        return descriptions;
    }

    public IReadOnlyList<FanState> Read()
    {
        var automatic = ReadStatus(FanModeDevice) is not { } mode || mode == FanModeAutomatic;
        var states = new List<FanState>(fans.Count);
        for (var index = 0; index < fans.Count; index++)
        {
            states.Add(new FanState(
                index,
                fans[index].Name,
                ReadRpm(fans[index].Device),
                // 这条通道给的是转速，不是占空比。**没有就是没有**，不换算一个假的出来。
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
        failureReason = "ASUS 的这条通道只有全速和自动两档，给不了任意占空比。";
        return false;
    }

    public bool TrySetFullSpeed(int fanIndex, out string? failureReason)
        => SetFanMode(fanIndex, FanModeFullSpeed, "切不到全速模式。", out failureReason);

    public bool TrySetAutomatic(int fanIndex, out string? failureReason)
        => SetFanMode(fanIndex, FanModeAutomatic, "交还固件自动控制失败。", out failureReason);

    /// <summary>
    /// 切风扇模式，然后**回读核对**。
    ///
    /// DEVS 的返回码各机型含义不一，所以不拿它当成功的凭据 —— 以回读为准。
    /// </summary>
    private bool SetFanMode(int fanIndex, uint mode, string failureText, out string? failureReason)
    {
        if (fanIndex < 0 || fanIndex >= fans.Count)
        {
            failureReason = "没有这个风扇。";
            return false;
        }
        if (wmi.InvokeOrdered(WriteMethod, FanModeDevice, mode) is null)
        {
            failureReason = "ATK 接口不认这次调用。";
            return false;
        }
        if (ReadStatus(FanModeDevice) is not { } after || after != mode)
        {
            failureReason = failureText;
            return false;
        }
        failureReason = null;
        return true;
    }

    /// <summary>读一项状态。固件说这一项不存在时返回 null，不返回 0。</summary>
    private ulong? ReadStatus(uint device)
        => wmi.InvokeOrdered(ReadMethod, device) is { } raw && (raw & PresenceBit) != 0
            ? raw & DataMask
            : null;

    /// <summary>
    /// 读一个风扇的转速。
    ///
    /// 读不出合理的数就返回 null —— 一个越界的"转速"说明我们把别的字段当成了转速，
    /// 那种情况下报一个数字比报读不到更糟。
    /// </summary>
    private double? ReadRpm(uint device)
    {
        if (ReadStatus(device) is not { } raw)
        {
            return null;
        }
        var rpm = (int)raw * RpmScale;
        return rpm >= 0 && rpm <= MaximumPlausibleRpm ? rpm : null;
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
