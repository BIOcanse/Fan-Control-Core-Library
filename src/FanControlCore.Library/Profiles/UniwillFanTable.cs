namespace FanControlCore.Profiles;

/// <summary>
/// Uniwill 固件的 CPU/GPU 风扇表：每把风扇各有 16 个温区，每区包含起始温度、
/// 结束温度和转速。固定转速保留已有温区结构；用户曲线通过 LayOut 写成固件表。
/// 曲线末段按用户设置延续，不插入隐式的高温强制满速区。
/// </summary>
public static class UniwillFanTable
{
    /// <summary>使能位一：两个风扇各用各的表。</summary>
    public const ushort SeparateTablesRegister = 0x07C5;
    public const byte SeparateTablesBit = 1 << 7;

    /// <summary>使能位二：启用 0x0Fxx 这组自定义表。</summary>
    public const ushort UseCustomTablesRegister = 0x07C6;
    public const byte UseCustomTablesBit = 1 << 2;

    /// <summary>CPU 风扇的表：结束温度 / 起始温度 / 转速，各 16 个字节。</summary>
    public const ushort CpuEndTemperature = 0x0F00;
    public const ushort CpuStartTemperature = 0x0F10;
    public const ushort CpuFanSpeed = 0x0F20;

    /// <summary>GPU 风扇的表。</summary>
    public const ushort GpuEndTemperature = 0x0F30;
    public const ushort GpuStartTemperature = 0x0F40;
    public const ushort GpuFanSpeed = 0x0F50;

    public const int ZoneCount = 16;

    /// <summary>固件未使用温区沿用的转速字节；它不表示额外的高温保护区。</summary>
    public const byte UnusedZoneSpeed = 0xC8;

    /// <summary>
    /// 回差：上升阈值比下降阈值高这么多度。
    ///
    /// 不是我们定的，是**从固件自己那张表里量出来的**：出厂表里
    /// <c>结束温度[k] = 起始温度[k+1] + 2</c> 处处成立。照着它写，
    /// 换上去的曲线和原厂曲线是同一种脾气 —— 没有回差的话，
    /// 温度在阈值上下抖两度，风扇就跟着一直换挡。
    /// </summary>
    public const byte HysteresisCelsius = 2;

    /// <summary>温度字节是这个值，表示"这一区没用上"或"没有上界"。</summary>
    public const byte UnusedTemperature = 0xFF;

    /// <summary>
    /// 把用户画的曲线摆成固件那张表。
    ///
    /// 先说清楚固件是怎么查表的 —— 这是从出厂表反推出来的，不是猜的：
    ///
    /// <list type="bullet">
    /// <item>温区 k 的**结束温度**是**上升阈值**：温度升过它就进 k+1 区，
    ///   跑 k+1 区的转速。</item>
    /// <item>温区 k 的**起始温度**是**下降阈值**：温度掉到它以下才退回 k-1 区。
    ///   两者差 <see cref="HysteresisCelsius"/> 度，那个重叠就是回差。</item>
    /// </list>
    ///
    /// 所以"用户说温度到 T 时转速是 P"落到表上就是：
    /// **某一区的结束温度 = T，而下一区的转速 = P**。
    ///
    /// 拿出厂表验算过：把出厂的上升阈值和转速当成用户的点喂进来，
    /// 这个映射会还原出几乎一模一样的一张表 —— 只有最后一区的起始温度差 1 度，
    /// 因为出厂最后一段的回差是 3 度不是 2 度。
    ///
    /// 0 区是第一个点以下那一段，转速取第一个点的转速 ——
    /// 曲线本身就是这么定义的（比最低点还低就用最低点）。
    ///
    /// **不额外塞一个"高温强制满速"的兜底区。** 那是用户看不见的隐式行为；
    /// 他画的曲线就该是他得到的曲线。要兜底也得是一项看得见、关得掉的设置。
    /// </summary>
    /// <param name="temperatures">每个点的温度，升序。</param>
    /// <param name="duties">每个点的转速原始值（0–<see cref="UniwillFanProfileTable.MaximumDuty"/>）。</param>
    /// <returns>16 个温区各自的 (结束温度, 起始温度, 转速)。</returns>
    public static (byte End, byte Start, byte Speed)[] LayOut(
        IReadOnlyList<byte> temperatures,
        IReadOnlyList<byte> duties)
    {
        ArgumentNullException.ThrowIfNull(temperatures);
        ArgumentNullException.ThrowIfNull(duties);
        if (temperatures.Count != duties.Count)
        {
            throw new ArgumentException("温度和转速的点数必须一样多。", nameof(duties));
        }
        // 16 个区里 0 区是"第一个点以下"，所以最多放得下 15 个点。
        if (temperatures.Count is 0 or > ZoneCount - 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(temperatures),
                $"点数要在 1 到 {ZoneCount - 1} 之间。");
        }

        var zones = new (byte End, byte Start, byte Speed)[ZoneCount];
        var points = temperatures.Count;

        /*
         * 0 度上的那个点是**底noise**：它说的不是"升到 0 度怎样"，而是
         * "第一个阈值以下风扇跑多少"，对应 0 区的转速。
         * 所以它不该再占一个温区 —— 否则读一次写一次，表就多出一个区来，
         * 来回几轮之后 16 个区就被撑满了。有它就从下一个点开始排阈值。
         */
        var firstThreshold = temperatures[0] == 0 ? 1 : 0;

        // 0 区：从 0 度到第一个阈值。转速就是第一个点的转速。
        zones[0] = (
            firstThreshold < points ? temperatures[firstThreshold] : UnusedTemperature,
            0,
            duties[0]);

        var zone = 0;
        for (var j = firstThreshold; j < points; j++)
        {
            zone++;
            // 第 zone 区跑的是第 j 个点的转速，从那个点的温度往上生效；
            // 往下要掉过回差才退回去。
            var start = (byte)Math.Max(0, temperatures[j] - HysteresisCelsius);
            // 最后一区没有上界：再高的温度都算它。
            var end = j + 1 < points ? temperatures[j + 1] : UnusedTemperature;
            zones[zone] = (end, start, duties[j]);
        }

        // 剩下的区没用上。起始温度写 0xFF 就是"到此为止"；
        // 转速沿用固件在空区里放的满速，那是它自己的写法。
        for (var k = zone + 1; k < ZoneCount; k++)
        {
            zones[k] = (UnusedTemperature, UnusedTemperature, UnusedZoneSpeed);
        }
        return zones;
    }

    /// <summary>某个风扇的三张表各自的起始地址。</summary>
    public static (ushort End, ushort Start, ushort Speed) AddressesFor(int fanIndex)
        => fanIndex == 0
            ? (CpuEndTemperature, CpuStartTemperature, CpuFanSpeed)
            : (GpuEndTemperature, GpuStartTemperature, GpuFanSpeed);

}
