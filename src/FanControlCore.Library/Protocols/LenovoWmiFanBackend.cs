using System.Runtime.Versioning;

namespace FanControlCore.Protocols;

/// <summary>
/// Lenovo Legion 平台的风扇通道，走联想固件的两个 WMI 类。
///
/// 按 README 的分类这条是 **WMI / ACPI**。它比别家多一处：
/// **读转速和开关全速在两个不同的类上**，所以这里有两条 WMI 通道。
///
/// 协议事实来自公开实现（只取事实，不抄代码）：
/// <list type="bullet">
/// <item>主线内核文档 <c>lenovo-wmi-gamezone</c>：
///   数据类 GUID <c>887B54E3-DDDC-4B2C-8B88-68A26A8835D0</c>
///   （Windows 上是 <c>root\WMI</c> 的 <c>LENOVO_GAMEZONE_DATA</c>），
///   方法 <c>IsSupportFanCooling</c>(12) / <c>SetFanCooling</c>(13) /
///   <c>GetFanCoolingStatus</c>(20)。那个"fan cooling"就是一键强冷。</item>
/// <item>LegionFanSpeedPlugin：<c>root\WMI</c> 的 <c>LENOVO_FAN_METHOD</c>，
///   <c>Fan_GetCurrentFanSpeed(byte FanID)</c> 回转速。</item>
/// </list>
///
/// **这条通道没有在联想机器上实测过。** 边界照样画死：
/// 固件不说自己支持 fan cooling、或者读不出合理转速，整条通道判不可用。
/// 固件表必须通过布局检测，写入时保留固件模板并保存原表；只在自定义模式下改表。
/// 任意实时占空比仍不声称支持，恢复只用于已保存并绑定设备身份的原表。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class LenovoWmiFanBackend : IFanBackend
{
    /// <summary>
    /// 固件那张表里，十个档位值放在哪儿。
    ///
    /// 缓冲区开头是电源模式（FSTM）、风扇号（FSID）、表长（FSTL），
    /// 从 <c>0x06</c> 起每隔 2 字节一个档位值，共十个（FSS0..FSS9）。
    ///
    /// **各机型的布局不一样，所以我们不自己拼这段缓冲区** ——
    /// 先把固件那份读回来，只改这十个字节再原样写回。这样模式、风扇号、表长
    /// 全是固件自己填的，我们不去猜。
    /// </summary>
    private const int TableValueOffset = 0x06;
    private const int TableValueStride = 2;
    private const int TablePointCount = 10;

    /// <summary>档位是 0..10 的索引，不是百分比。</summary>
    private const int MaximumLevel = 10;

    /// <summary>
    /// 缓冲区第 0 字节（FSTM）就是**当前电源模式**：
    /// 0x01 静音、0x02 均衡、0x03 性能、0xFF 自定义。
    ///
    /// EC 只在自定义模式下拿这张表去跑风扇；别的模式下它跑自己内置的那条，
    /// 存着的自定义表看都不看 —— 于是会出现"写成功、回读一致、风扇没反应"。
    /// </summary>
    private const byte CustomPowerMode = 0xFF;

    private const string CustomModeRequired =
        "仅自定义电源模式下生效（联想笔记本电脑）。"
        + "固件的风扇表按电源模式分开存，EC 只在自定义模式下执行它；"
        + "切过去之后重新检测硬件即可解锁。";

    /// <summary>
    /// 每个点的下限。前八点最低 1 档，第九点 3，第十点 5。
    ///
    /// 低于这个值固件会拒（上游驱动回 <c>EOPNOTSUPP</c>），
    /// 所以在这里就夹住 —— 让用户看到"写失败"，不如一开始就给他能写进去的值。
    /// </summary>
    private static readonly byte[] MinimumLevels = [1, 1, 1, 1, 1, 1, 1, 1, 3, 5];

    /// <summary>
    /// 这十个档位挂在哪些温度上。
    ///
    /// **这是位置刻度，不是固件真正的温度断点。** 联想既不公布断点，也不让改：
    /// <c>Fan_Get_Table</c> 回复里传感器那半边恒为零。但曲线这个类型要求每个点
    /// 带一个温度，所以这里给一组固定的、均匀的刻度，让十个点能一一对上。
    ///
    /// 这件事必须由界面讲出来（曲线种类报 <c>table</c> 就是为了这个），
    /// 否则用户会以为拖动温度轴能改断点 —— 拖了不生效，比不给更糟。
    /// </summary>
    private static readonly double[] PointTemperatures =
        [40, 45, 50, 55, 60, 65, 70, 75, 80, 85];

    /// <summary>
    /// 联想的风扇号从 1 开始：1 是 CPU 侧，2 是显卡侧。
    ///
    /// **角色跟着风扇号走，不是从名字里猜的** —— 这是这条通道的固定编号约定，
    /// 所以认得出来就照实报上去。上层拿它当词条用；不报的话界面上这两把风扇
    /// 就只剩"风扇1 / 风扇2"，用户分不出哪把吹显卡。
    /// </summary>
    private static readonly (uint Id, string Name, string Role)[] CandidateFans =
    [
        (1, "CPU 风扇", FanRoles.Cpu),
        (2, "显卡风扇", FanRoles.Gpu)
    ];

    /// <summary>超出这个范围的"转速"不是转速。</summary>
    private const int MaximumPlausibleRpm = 12_000;

    private readonly ILenovoFanHardware hardware;
    private readonly string? baselineDirectory;

    public LenovoWmiFanBackend() : this(new LenovoFanHardware(),
        Environment.GetEnvironmentVariable("FANCONTROLCORE_STATE_DIRECTORY")) { }

    public LenovoWmiFanBackend(string? baselineDirectory) : this(new LenovoFanHardware(), baselineDirectory) { }

    internal LenovoWmiFanBackend(ILenovoFanHardware hardware, string? baselineDirectory)
    {
        this.hardware = hardware;
        this.baselineDirectory = baselineDirectory;
    }
    private readonly List<(uint Id, string Name, string Role)> fans = [];

    /// <summary>
    /// 这台机器的固件表读得通、布局也对得上。开通道和用户显式重新检测时读取。
    /// </summary>
    private bool firmwareTableUsable;

    /// <summary>
    /// 固件曲线现在要满足什么才生效。null 表示当前就生效（已经在自定义模式）。
    /// 和 <see cref="firmwareTableUsable"/> 一起显式刷新。
    /// </summary>
    private string? firmwareCurveRequires;
    private bool disposed;

    public string Name => "lenovo-legion-wmi";

    public bool TryOpen(out string? unavailableReason)
    {
        if (!OperatingSystem.IsWindows())
        {
            unavailableReason = "这条通道只有 Windows 上有。";
            return false;
        }
        if (!hardware.IsAvailable)
        {
            unavailableReason = "这台机器上没有联想的 Legion WMI 接口，或者没有管理员权限。";
            return false;
        }
        // 固件自己说支不支持强冷。它说不支持就别去写 —— 这是它的接口，它说了算。
        if (hardware.ReadCoolingSupported() is not { } supported
            || supported == 0)
        {
            unavailableReason = "联想的 WMI 接口在，但这台机器上没有风扇强冷这一项。";
            return false;
        }

        fans.Clear();
        foreach (var candidate in CandidateFans)
        {
            if (ReadRpm(candidate.Id) is not null)
            {
                fans.Add(candidate);
            }
        }
        if (fans.Count == 0)
        {
            unavailableReason = "联想的 WMI 接口在，但它没有报出任何风扇转速。";
            return false;
        }

        /*
         * 固件表能不能用，**在这儿验一次，不靠声明**。
         *
         * 那张表的字节布局各机型不一样。读得回来、而且十个档位值都落在 0..10 里，
         * 才说明这台机器和我们假定的布局一致；对不上就照旧只报全速/自动，
         * 绝不照着错的偏移去改一台陌生机器的散热表。
         *
         * 放在开通道这一刻做，是因为 Describe() 不碰硬件 ——
         * 挂着什么能力在这一刻就要定下来。
         */
        RefreshDetection();

        unavailableReason = null;
        return true;
    }

    /// <summary>
    /// 显式重读固件布局与电源模式，不切换模式或修改风扇设定。
    /// </summary>
    public void RefreshDetection()
    {
        firmwareCurveRequires = null;
        firmwareTableUsable = ReadLevels(0) is not null;

        /*
         * 表能写，不等于固件会去执行它。
         *
         * FSTM（缓冲区第 0 字节）就是当前电源模式，而 EC 只在自定义模式下
         * 拿这张表跑风扇。不是自定义模式就如实报出这个前置 ——
         * 上层据此锁住并把话给用户看，**我们不替他去切模式**：
         * 那会连带改整机功耗策略，是背着用户的副作用。
         */
        if (firmwareTableUsable && FanIdAt(0) is { } probeFan)
        {
            firmwareCurveRequires = ReadTableBuffer(probeFan) is { } buffer
                && buffer[0] == CustomPowerMode
                    ? null
                    : CustomModeRequired;
        }

    }

    /// <summary>联想强冷是整机开关，风扇会一起动；布局和模式来自开通道或显式刷新。</summary>
    public IReadOnlyList<FanDescription> Describe()
    {
        var descriptions = new List<FanDescription>(fans.Count);
        for (var index = 0; index < fans.Count; index++)
        {
            descriptions.Add(new FanDescription(
                index,
                fans[index].Name,
                Writable: true,
                SupportsDuty: false,
                Role: fans[index].Role,
                /*
                 * Legion 的固件表是 **10 个转速档**（FSS0..FSS9），
                 * 温度断点由固件定死、既不公布也不可写，**不是能任意拖点的 XY 曲线** ——
                 * 伪装成 XY 的话，用户拖了温度却不生效，比不给更糟。所以报 table。
                 *
                 * 而且**开通道或显式刷新时验过布局才报**：那张表的字节布局各机型不一样，
                 * 验不过就退回只有全速/自动，不去改一台陌生机器的散热表。
                 */
                FirmwareCurve: firmwareTableUsable ? FanFirmwareCurveKinds.Table : null,
                SupportsSoftwareCurve: false,
                CurvePoints: firmwareTableUsable ? TablePointCount : null,
                FirmwareCurveRequires: firmwareCurveRequires,
                SupportsFirmwareCurveReset: true));
        }
        return descriptions;
    }

    public IReadOnlyList<FanState> Read()
    {
        var automatic = ReadCooling() != true;
        var states = new List<FanState>(fans.Count);
        for (var index = 0; index < fans.Count; index++)
        {
            states.Add(new FanState(
                index,
                fans[index].Name,
                ReadRpm(fans[index].Id),
                // 这条通道给的是转速，不是占空比。没有就是没有。
                DutyPercent: null,
                DutyRaw: null,
                Writable: true,
                SupportsDuty: false,
                AutomaticControl: automatic));
        }
        return states;
    }

    /// <summary>
    /// 固件那张表现在是什么样。
    ///
    /// **给出的温度是位置刻度，不是固件真正的断点。** 联想不公布断点
    /// （<c>Fan_Get_Table</c> 回复里传感器那半边恒为零），也不接受我们改断点。
    /// 曲线种类因此报 <see cref="FanFirmwareCurveKinds.Table"/>，
    /// 由界面说明"只能调每一档的转速"——否则就成了一条拖得动温度却不生效的假曲线。
    /// </summary>
    public IReadOnlyList<FanCurvePoint>? ReadCurve(int fanIndex)
    {
        if (ReadLevels(fanIndex) is not { } levels)
        {
            return null;
        }
        /*
         * 没写过表之前固件回十个零。
         *
         * 那**不是**一条"全程停转"的曲线，是"这台机器还没有自定义表"。
         * 当成曲线交上去，用户会以为他的风扇被设成了全停。
         */
        if (levels.All(static level => level == 0))
        {
            return null;
        }
        return [.. levels.Select(static (level, index) => new FanCurvePoint(
            PointTemperatures[index],
            level * 100.0 / MaximumLevel))];
    }

    /// <summary>
    /// 把一条曲线写进固件那张表。
    ///
    /// **只改十个档位字节，缓冲区的其余部分原样沿用固件给的那份** ——
    /// 电源模式、风扇号、表长都是它自己填的，我们不去猜（各机型布局不同）。
    /// 写完回读核对：这类接口返回成功不代表固件收下了。
    /// </summary>
    public bool TryWriteCurve(
        int fanIndex,
        IReadOnlyList<FanCurvePoint> curve,
        out string? failureReason)
    {
        ArgumentNullException.ThrowIfNull(curve);
        if (FanIdAt(fanIndex) is not { } fanId)
        {
            failureReason = "没有这个风扇。";
            return false;
        }
        if (curve.Count != TablePointCount)
        {
            failureReason = $"联想这张表固定 {TablePointCount} 档，给了 {curve.Count} 个点。";
            return false;
        }
        if (ReadTableBuffer(fanId) is not { } buffer)
        {
            failureReason = "读不回固件那张表，不能在没有模板的情况下拼一份写进去。";
            return false;
        }

        if (buffer[0] != CustomPowerMode)
        {
            failureReason = CustomModeRequired;
            return false;
        }

        if (curve.Any(point => !double.IsFinite(point.Percent) || !double.IsFinite(point.TemperatureCelsius))
            || !IsValidTable(buffer))
        {
            failureReason = "固件表或曲线包含无效值，未继续调整。";
            return false;
        }
        if (!TryCaptureBaseline(fanId, buffer, out failureReason)) return false;

        var wanted = new byte[TablePointCount];
        for (var point = 0; point < TablePointCount; point++)
        {
            var level = (int)Math.Round(curve[point].Percent * MaximumLevel / 100.0);
            // 夹住每点下限：低于它固件会拒，先夹住比让用户吃一个失败强。
            wanted[point] = (byte)Math.Clamp(level, MinimumLevels[point], MaximumLevel);
            buffer[TableValueOffset + (point * TableValueStride)] = wanted[point];
        }

        if (!hardware.WriteTable(fanId, buffer))
        {
            failureReason = "联想的 WMI 接口不认这次写表调用。";
            return false;
        }
        if (ReadLevels(fanIndex) is not { } written || !written.SequenceEqual(wanted))
        {
            failureReason = "表写下去了，但回读对不上 —— 固件没有收下。";
            return false;
        }
        failureReason = null;
        return true;
    }

    /// <summary>这个风扇的十个档位值。读不回来、或者表不成形就是 null。</summary>
    private byte[]? ReadLevels(int fanIndex)
    {
        if (FanIdAt(fanIndex) is not { } fanId || ReadTableBuffer(fanId) is not { } buffer)
        {
            return null;
        }
        var levels = new byte[TablePointCount];
        for (var point = 0; point < TablePointCount; point++)
        {
            var level = buffer[TableValueOffset + (point * TableValueStride)];
            // 超出 0..10 说明这台机器的布局和我们假定的不一样。
            // **宁可说读不到，也不要照着错的偏移去改它的散热表。**
            if (level > MaximumLevel)
            {
                return null;
            }
            levels[point] = level;
        }
        return levels;
    }

    /// <summary>
    /// 固件那份表缓冲区。
    ///
    /// 方法签名各机型不一（有的收风扇号，有的什么都不收），而我们手上没有那些机器，
    /// 所以两种形状都试一次 —— **只试这两种，试不出就认输**，不做无边界的搜索。
    /// </summary>
    private byte[]? ReadTableBuffer(uint fanId)
    {
        var buffer = hardware.ReadTable(fanId);
        var needed = TableValueOffset + ((TablePointCount - 1) * TableValueStride) + 1;
        return buffer is not null && buffer.Length >= needed ? buffer : null;
    }

    private uint? FanIdAt(int fanIndex)
        => fanIndex >= 0 && fanIndex < fans.Count ? fans[fanIndex].Id : null;

    public bool TrySetDuty(int fanIndex, double percent, out string? failureReason)
    {
        failureReason = "联想的这条通道只有全速和自动两档，给不了任意占空比。";
        return false;
    }

    public bool TrySetFullSpeed(int fanIndex, out string? failureReason)
        => SetCooling(true, "切不到全速模式。", out failureReason);

    public bool TrySetAutomatic(int fanIndex, out string? failureReason)
        => TryReleaseOverride(fanIndex, out failureReason);

    public bool TryResetFirmwareCurve(int fanIndex, out string? failureReason)
        => RestoreAutomatic(fanIndex, out failureReason);

    private bool RestoreAutomatic(int fanIndex, out string? failureReason)
    {
        if (FanIdAt(fanIndex) is not { } fanId)
        {
            failureReason = "没有这个风扇。";
            return false;
        }
        if (!TryRestoreTable(fanId, out failureReason)) return false;
        if (!TryReleaseOverride(fanIndex, out failureReason)) return false;
        return TryRemoveBaseline(fanId, out failureReason);
    }

    public bool TryReleaseOverride(int fanIndex, out string? failureReason)
        => SetCooling(false, "交还固件自动控制失败。", out failureReason);

    /// <summary>开关强冷，然后**回读核对** —— 返回码各机型含义不一，不拿它当凭据。</summary>
    private bool SetCooling(bool enabled, string failureText, out string? failureReason)
    {
        if (!hardware.SetCooling(enabled))
        {
            failureReason = "联想的 WMI 接口不认这次调用。";
            return false;
        }
        if (ReadCooling() != enabled)
        {
            failureReason = failureText;
            return false;
        }
        failureReason = null;
        return true;
    }

    private bool? ReadCooling()
        => hardware.ReadCooling() is { } status ? status != 0 : null;

    private double? ReadRpm(uint fanId)
    {
        if (hardware.ReadRpm(fanId) is not { } raw)
        {
            return null;
        }
        // 停转的风扇就是 0 转 —— 那是个真读数，不是"读不到"。
        // 读不到由上面那个 null 表示：固件根本没有这个风扇号的时候才走到那里。
        var rpm = (int)raw;
        return rpm >= 0 && rpm <= MaximumPlausibleRpm ? rpm : null;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        hardware.Dispose();
    }
}
