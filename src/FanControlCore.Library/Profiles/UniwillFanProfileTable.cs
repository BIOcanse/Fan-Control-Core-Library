namespace FanControlCore.Profiles;

/// <summary>
/// Uniwill / Tongfang（同方公版，机械革命等整机厂都用这一族）的风扇寄存器。
///
/// 这些地址是**事实**，取自公开的 GPL 实现（tuxedo-drivers 与上游 uniwill-laptop）
/// 里对同一批硬件的描述，这里按自己的结构重新表达，不复制源码。
///
/// **没有"通用 Uniwill 机器"这回事**：同一族里 EC 固件的功能差别很大
/// （cTGP 就是个例子，有的固件实现、有的不实现）。所以写入之前必须先看
/// <see cref="FanControlStatus"/> 那一位 —— 固件自己说有风扇控制，我们才写。
/// 认不出的机器默认只读。
/// </summary>
public sealed class UniwillFanProfileTable
{
    /// <summary>
    /// 主风扇转速，两个字节拼成 16 位，**大端**：低地址是高字节。
    ///
    /// 按小端读会得到六万转这种荒唐读数 —— 而且它还会随负载变化，
    /// 看着像"在工作"，很容易蒙混过去。所以这一条要写明。
    /// </summary>
    public const ushort MainFanRpmHigh = 0x0464;
    public const ushort MainFanRpmLow = 0x0465;

    /// <summary>副风扇（有的机器只有一个）。同样是大端。</summary>
    public const ushort SecondFanRpmHigh = 0x046C;
    public const ushort SecondFanRpmLow = 0x046D;

    public const ushort ProjectId = 0x0740;

    /// <summary>
    /// MECHREVO JIAOLONG Series / 项目 0x1A 的 OEM 地址表和实测使用 0x046B。
    /// 仅对这个已确认组合修正读数；其他固件保留公开参考的 0x046D。
    /// </summary>
    internal static ushort ResolveSecondFanRpmLow(string? manufacturer, string? model, byte? projectId)
        => string.Equals(manufacturer?.Trim(), "MECHREVO", StringComparison.OrdinalIgnoreCase)
            && string.Equals(model?.Trim(), "JIAOLONG Series", StringComparison.OrdinalIgnoreCase)
            && projectId == 0x1A ? (ushort)0x046B : SecondFanRpmLow;

    /// <summary>
    /// 当前占空比，读这里。
    ///
    /// <see cref="MainFanDuty"/> 那两个是**写**入口，读它们只能读到我们自己写过的值；
    /// 固件自动控制时的真实占空比在这里。
    /// </summary>
    public const ushort MainFanPwm = 0x075B;
    public const ushort SecondFanPwm = 0x075C;

    /// <summary>
    /// 风扇控制状态。<see cref="HasFanControlBit"/> 是固件自己声明
    /// "这台机器支持 Uniwill 风扇控制" —— 这就是我们的固件指纹检查。
    /// </summary>
    public const ushort FanControlStatus = 0x078E;
    public const byte HasFanControlBit = 0x40;

    /// <summary>
    /// 模式寄存器。<see cref="FullFanModeBit"/> 置位表示"由我们直接给定转速"，
    /// 清掉就交还固件自动控制。
    /// </summary>
    public const ushort FanModeRegister = 0x0751;
    public const byte FullFanModeBit = 0x40;

    /// <summary>直接给定转速的**写**入口，一个风扇一个。读当前值用上面那两个。</summary>
    public const ushort MainFanDuty = 0x1804;
    public const ushort SecondFanDuty = 0x1809;

    /// <summary>
    /// 转速字节的上限是 0xC8（200），不是 0xFF。
    /// 按 0xFF 换算百分比会让 78% 以上全部写成满速，用户拖到哪儿都一样。
    /// </summary>
    public const byte MaximumDuty = 0xC8;

    /// <summary>
    /// 风扇能转起来的最低占空比。
    ///
    /// 低于这个值电机根本转不动，写下去等于停转 —— 而"以为在转、实际停了"
    /// 是这类工具最危险的失败方式。所以低于它就夹到它，真想停就明确写 0。
    /// </summary>
    public const double MinimumRunningPercent = 20;

    /// <summary>这台机器的固件认不认这套风扇控制。</summary>
    public static bool IsSupported(byte fanControlStatus)
        => (fanControlStatus & HasFanControlBit) != 0;

    /// <summary>
    /// 百分比换算成寄存器值。
    ///
    /// 0 就是 0（停转），但**不要写 0 以外的极低值** —— 那一段电机转不起来，
    /// 效果和停转一样，却看不出来是停了。
    /// </summary>
    public static byte DutyFromPercent(double percent)
    {
        var clamped = Math.Clamp(percent, 0, 100);
        if (clamped <= 0)
        {
            return 0;
        }
        if (clamped < MinimumRunningPercent)
        {
            clamped = MinimumRunningPercent;
        }
        return (byte)Math.Round(clamped * MaximumDuty / 100);
    }

    public static double PercentFromDuty(byte duty)
        => Math.Round(Math.Min(duty, MaximumDuty) * 100.0 / MaximumDuty, 1);
}
