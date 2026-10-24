using System.Runtime.Versioning;
using FanControlCore.Transport;

namespace FanControlCore.Protocols;

/// <summary>
/// Acer Predator / Nitro 平台的风扇通道，走 Acer 的 Gaming WMI 接口。
///
/// 按 README 的分类这条是 **WMI / ACPI**。它是这几条里唯一**给得了任意占空比**的
/// 厂商通道：Acer 的 <c>SET_GAMING_FAN_SPEED</c> 直接收一个百分比。
///
/// 协议事实来自 Linuwu-Sense（GPL-2.0，只取事实，不抄代码）：
/// <list type="bullet">
/// <item>接口 GUID <c>7A4DDFE7-5B5D-40B4-8595-4408E0CC7F56</c>，
///   方法按 ID 寻址，一进一出都是 64 位整数。</item>
/// <item>方法 14 设风扇行为，16 设风扇转速。</item>
/// <item>风扇行为：自动 <c>0x410009</c>、全速 <c>0x820009</c>、
///   自定义 <c>0xC30009</c>。</item>
/// <item>转速参数的编码是
///   <c>((百分比 * 25600) / 100) &amp; 0xFF00 | 风扇号</c>，
///   风扇号 CPU 是 1、显卡是 4。</item>
/// </list>
///
/// **这条通道没有在 Acer 机器上实测过。** 手上没有那样的机器。
/// 所以转速**只写不读**：Acer 读转速要走另一条 <c>GET_GAMING_SYS_INFO</c> 加位掩码的路，
/// 各世代的掩码不一样，没有机器就没法确认读到的是不是转速 ——
/// 宁可如实说读不到，也不报一个可能是别的字段的数字。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AcerWmiFanBackend : IFanBackend
{
    private const string Scope = @"root\WMI";

    /// <summary>Acer 的 Gaming 接口。上游一律用这个 GUID 指认它。</summary>
    private const string GamingGuid = "7A4DDFE7-5B5D-40B4-8595-4408E0CC7F56";

    /// <summary>方法按 ID 寻址。WMI 上它们的名字是 <c>WmiMethodId_&lt;ID&gt;</c> 之类的本地别名。</summary>
    private const int SetFanBehaviorMethodId = 14;
    private const int SetFanSpeedMethodId = 16;

    private const ulong BehaviorAutomatic = 0x0041_0009;
    private const ulong BehaviorFullSpeed = 0x0082_0009;
    private const ulong BehaviorCustom = 0x00C3_0009;

    /// <summary>
    /// CPU 风扇是 1，显卡风扇是 4。
    ///
    /// **角色跟着这个编号走**，认得出来就照实报上去 ——
    /// 上层拿它当词条，不报的话界面上只剩"风扇1 / 风扇2"，分不出哪把吹显卡。
    /// </summary>
    private static readonly (uint Id, string Name, string Role)[] Fans =
    [
        (1, "CPU 风扇", FanRoles.Cpu),
        (4, "显卡风扇", FanRoles.Gpu)
    ];

    /// <summary>占空比的硬边界。低于这个值风扇可能转不起来，高于 100 没有意义。</summary>
    private const double MinimumDutyPercent = 20;
    private const double MaximumDutyPercent = 100;

    private readonly WmiMethodChannel wmi = WmiMethodChannel.ForGuid(Scope, GamingGuid);

    /// <summary>
    /// 我们现在把哪些风扇按住了。
    ///
    /// Acer 的行为位是**整机一个**，而占空比是每个风扇一份；
    /// 切回自动之后这一份就不作数了，所以状态跟着行为位走。
    /// </summary>
    private readonly Dictionary<int, double> heldDuty = [];
    private bool fullSpeed;
    private bool customDuty;
    private bool disposed;

    public string Name => "acer-gaming-wmi";

    public bool TryOpen(out string? unavailableReason)
    {
        if (!OperatingSystem.IsWindows())
        {
            unavailableReason = "这条通道只有 Windows 上有。";
            return false;
        }
        if (!wmi.IsAvailable)
        {
            unavailableReason = "这台机器上没有 Acer 的 Gaming WMI 接口，或者没有管理员权限。";
            return false;
        }
        unavailableReason = null;
        return true;
    }

    /// <summary>
    /// Acer 这条是**唯一给得了任意占空比**的厂商通道 —— 它的接口直接收百分比。
    /// 转速读不到，如实报 null，不换算一个假的出来。
    /// </summary>
    public IReadOnlyList<FanDescription> Describe()
    {
        var descriptions = new List<FanDescription>(Fans.Length);
        for (var index = 0; index < Fans.Length; index++)
        {
            descriptions.Add(new FanDescription(
                index,
                Fans[index].Name,
                Writable: true,
                SupportsDuty: true,
                Role: Fans[index].Role,
                // 主线 acer-wmi 对支持 PWM 的机型能独立写 CPU/GPU 0-100%，
                // 但**没有**通用的固件曲线表接口。所以曲线由我们自己跑。
                SupportsSoftwareCurve: true, SoftwareTakeoverCoversAllFans: true));
        }
        return descriptions;
    }

    public IReadOnlyList<FanState> Read()
    {
        var states = new List<FanState>(Fans.Length);
        for (var index = 0; index < Fans.Length; index++)
        {
            states.Add(new FanState(
                index,
                Fans[index].Name,
                // 转速要走另一条加位掩码的路，各世代掩码不一样。没验证过就不报数字。
                Rpm: null,
                DutyPercent: heldDuty.TryGetValue(index, out var duty) ? duty : null,
                DutyRaw: null,
                Writable: true,
                SupportsDuty: true,
                AutomaticControl: !fullSpeed && !customDuty));
        }
        return states;
    }

    /// <summary>
    /// 把一个风扇按在这个占空比。
    ///
    /// **夹了要说**：低于下界的值会被抬到下界，那时如实报出来，
    /// 不默默写一个和请求不一样的值然后说成功。
    /// </summary>
    public bool TrySetDuty(int fanIndex, double percent, out string? failureReason)
    {
        if (fanIndex < 0 || fanIndex >= Fans.Length)
        {
            failureReason = "没有这个风扇。";
            return false;
        }
        if (!double.IsFinite(percent))
        {
            failureReason = "占空比得是个数。";
            return false;
        }
        if (percent < MinimumDutyPercent || percent > MaximumDutyPercent)
        {
            failureReason = $"占空比要在 {MinimumDutyPercent} 到 {MaximumDutyPercent} 之间。";
            return false;
        }

        // 先切到自定义行为，再写占空比 —— 反过来的话固件还在自动模式，写进去马上被盖掉。
        if (wmi.InvokeOrderedByMethodId(SetFanBehaviorMethodId, BehaviorCustom) is null)
        {
            failureReason = "Acer 的 Gaming 接口不认切换风扇行为这次调用。";
            return false;
        }
        var raw = ((ulong)Math.Round(percent * 25600 / 100) & 0xFF00)
            + Fans[fanIndex].Id;
        if (wmi.InvokeOrderedByMethodId(SetFanSpeedMethodId, raw) is null)
        {
            failureReason = "Acer 的 Gaming 接口不认设置转速这次调用。";
            return false;
        }

        customDuty = true;
        fullSpeed = false;
        heldDuty[fanIndex] = percent;
        failureReason = null;
        return true;
    }

    public bool TrySetFullSpeed(int fanIndex, out string? failureReason)
    {
        if (wmi.InvokeOrderedByMethodId(SetFanBehaviorMethodId, BehaviorFullSpeed) is null)
        {
            failureReason = "切不到全速模式。";
            return false;
        }
        fullSpeed = true;
        customDuty = false;
        heldDuty.Clear();
        failureReason = null;
        return true;
    }

    public bool TrySetAutomatic(int fanIndex, out string? failureReason)
    {
        if (wmi.InvokeOrderedByMethodId(SetFanBehaviorMethodId, BehaviorAutomatic) is null)
        {
            failureReason = "交还固件自动控制失败。";
            return false;
        }
        fullSpeed = false;
        customDuty = false;
        heldDuty.Clear();
        failureReason = null;
        return true;
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
