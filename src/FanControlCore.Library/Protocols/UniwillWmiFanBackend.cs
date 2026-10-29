using System.Runtime.Versioning;
using FanControlCore.Profiles;
using FanControlCore.Transport;

namespace FanControlCore.Protocols;

/// <summary>
/// Uniwill / Tongfang 平台的风扇通道，经由厂商固件的 ACPI WMI 接口。
///
/// 按 README 的分类，这条算 **Raw EC 兜底**：我们读写的是 EC RAM 里的寄存器，
/// 而不是一个"设置风扇转速"的固件方法。所以规矩比别的通道严：
///
/// <list type="bullet">
/// <item>写之前必须过固件指纹检查（<see cref="UniwillFanProfileTable.IsSupported"/>）——
///   固件自己说有风扇控制，我们才写。认不出的机器**只读**。</item>
/// <item>占空比夹在 profile 声明的范围里，而且不允许落进"转不起来"的那一段；
///   这个下限由 Describe 的 <c>MinimumOnPercent</c> 报出来，定速回执带实际下发的百分比。</item>
/// <item>固定转速保留固件温区，只修改各区转速；曲线按用户明确选择的点布局，
///   不额外插入高温强制区（见 <see cref="UniwillFanTable"/>）。</item>
/// <item>全速模式位（0x0751 的 0x40）是整机共用的，由正在接管的风扇共同持有：
///   软件驱动和全速各记一份，交还一把只放下它那一份，最后一份放下才清位；
///   另一把持有时拒绝会清位的定速表和固件曲线（<see cref="FanCommandErrorKind.SharedTakeoverHeld"/>）。</item>
/// <item>退出时恢复已保存的临时定速表并放下全速模式位；持久曲线保留。
///   明确取消曲线才恢复对应原表，整机自定义表使能由剩余表的所有权决定。</item>
/// </list>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class UniwillWmiFanBackend : IFanBackend
{
    private readonly IUniwillFanHardware transport;
    private readonly string? baselineDirectory;
    private readonly Lazy<string?> identity;
    /// <summary>Serializes every mutation of the EC fan state and the baseline files.</summary>
    private readonly object tableGate = new();
    /// <summary>
    /// Fans whose software drive or full speed currently holds the shared mode bit. Mutated only while
    /// <see cref="tableGate"/> is held (so check-then-clear cannot race a drive); read under its own small lock so
    /// queries never wait behind table IO.
    /// </summary>
    private readonly HashSet<int> sharedModeHolders = [];
    private readonly object holdersGate = new();
    private bool writable;
    private ushort secondFanRpmLow = UniwillFanProfileTable.SecondFanRpmLow;
    private readonly Func<(string? Manufacturer, string? Model)> readSystemModel;

    public UniwillWmiFanBackend() : this(new UniwillWmiEcTransport(),
        Environment.GetEnvironmentVariable("FANCONTROLCORE_STATE_DIRECTORY"), FanMachineIdentity.ReadSystemUuid,
        FanMachineIdentity.ReadSystemModel) { }

    public UniwillWmiFanBackend(string? baselineDirectory) : this(new UniwillWmiEcTransport(),
        baselineDirectory, FanMachineIdentity.ReadSystemUuid, FanMachineIdentity.ReadSystemModel) { }

    internal UniwillWmiFanBackend(IUniwillFanHardware transport, string? baselineDirectory, Func<string?> readIdentity,
        Func<(string? Manufacturer, string? Model)>? readSystemModel = null)
    {
        (this.transport, this.baselineDirectory, identity) = (transport, baselineDirectory, new(readIdentity));
        this.readSystemModel = readSystemModel ?? (() => (null, null));
    }

    public string Name => "uniwill-wmi-ec";
    public IReadOnlyList<string> Diagnostics => transport.Diagnostic is { } detail ? [detail] : [];

    /// <summary>
    /// 恢复日志用的身份：**这把风扇对应的固件表**，不是显示名。
    ///
    /// 0 号固定对 CPU 表（0x0F00 起）、1 号固定对 GPU 表（0x0F30 起），这是固件定的硬件通道；
    /// 显示名以后改名或换语言，未完成的恢复记录照样对得上。旧版本按显示名记下的记录，
    /// 由恢复日志的旧身份兼容规则认领（见 TemporaryTakeoverJournal）。
    /// </summary>
    public string? RecoveryIdentity(int fanIndex)
        => fanIndex is 0 or 1 ? $"ec-fan-table-0x{UniwillFanTable.AddressesFor(fanIndex).End:X4}" : null;

    public IReadOnlyCollection<int> SharedTakeoverHolders
    {
        get { lock (holdersGate) return sharedModeHolders.ToArray(); }
    }

    /// <summary>Releasing one fan keeps the 0x0751 bit while another fan holds it.</summary>
    public bool SharedTakeoverCountsHolders => true;

    /// <summary>Both fans share bit 0x40 of 0x0751, so they switch to software control together.</summary>
    public IReadOnlyList<FanControlGroup> ControlGroups { get; } =
        [new("uniwill-shared-mode", [0, 1], FanControlGroupReason.UniwillSharedModeBit, FirmwareFixedDuty: true)];

    public bool TryOpen(out string? unavailableReason) => TryOpen(out _, out unavailableReason);

    public bool TryOpen(out FanOpenFailureKind failureKind, out string? unavailableReason)
    {
        failureKind = FanOpenFailureKind.Other;
        if (!OperatingSystem.IsWindows())
        {
            failureKind = FanOpenFailureKind.NoSupportedController;
            unavailableReason = "这条通道只有 Windows 上有。";
            return false;
        }
        if (!transport.IsAvailable)
        {
            failureKind = transport.AvailabilityFailureKind;
            unavailableReason = "这台机器上没有 Uniwill 的 ACPI WMI 接口，或者没有管理员权限。";
            return false;
        }
        if (transport.Read(UniwillFanProfileTable.FanControlStatus) is not { } status)
        {
            unavailableReason = "读不到风扇控制状态。";
            return false;
        }

        // 固件指纹：它自己说支持，我们才敢写。
        writable = UniwillFanProfileTable.IsSupported(status);
        var model = readSystemModel();
        secondFanRpmLow = UniwillFanProfileTable.ResolveSecondFanRpmLow(
            model.Manufacturer, model.Model, transport.Read(UniwillFanProfileTable.ProjectId));
        // 读转速这件事不需要它支持 —— 认不出也能只读，那仍然有价值。
        unavailableReason = null;
        return true;
    }

    /// <summary>
    /// 两个风扇，能力都由固件指纹决定，不碰 EC。
    /// 固定转速沿用固件表；<c>supportsDuty</c> 与固件声明的可写能力一致。
    ///
    /// **角色不是猜的**：这个平台的自定义风扇表本身就分成 CPU 表和 GPU 表
    /// （<see cref="UniwillFanTable"/> 的两组地址），0 号对 CPU 表、1 号对 GPU 表。
    /// 固件自己是这么分的，所以我们照它说的报。
    /// </summary>
    public IReadOnlyList<FanDescription> Describe() =>
    [
        Describe(0, "CPU 风扇", FanRoles.Cpu),
        Describe(1, "显卡风扇", FanRoles.Gpu)
    ];

    private FanDescription Describe(int index, string name, string role)
        => new(
            index,
            name,
            writable,
            /*
             * **固定转速就是把每一区的转速写成同一个值。**
             *
             * 先前这里报 false，理由是"自定义风扇表建不起来"。那个理由不对：
             * 表能用，缺的只是 `0x07C6` 的启用位 —— 本机**默认是关的**，
             * 置上之后固件立刻开始查表（实测占空比六秒内爬到满）。
             * 不需要动 `0x07C5`，那一位全程是 0，也不影响 CPU/GPU 分表。
             */
            SupportsDuty: writable,
            role,
            // 温度点和转速点都能写进 EC，固件自己查表执行。
            FirmwareCurve: writable ? FanFirmwareCurveKinds.Xy : null,
            /*
             * **这条通道两条路都有**，所以用户可以选：
             * 固件表（设一次就一直有效）或者软件接管（我们每 0.1 秒写一次
             * 直接占空比那个寄存器，见 TryDriveDuty）。
             * 两条都实测生效过。
             */
            SupportsSoftwareCurve: writable,
            /*
             * **这个平台上"交给软件管"是整机一个开关。**
             * 模式位（0x0751 的那一位）一置上，两把风扇一起脱离固件自动控制，
             * 两把的表都不再生效。这一位由正在接管的风扇共同持有，见 SharedTakeoverHolders。
             */
            SoftwareTakeoverCoversAllFans: true,
            CurvePoints: UniwillFanTable.ZoneCount - 1,
            /*
             * 硬件死区：0 以上、20% 以下电机转不起来，DutyFromPercent 会把它抬到 20%。
             * 报出来，软件曲线引擎的死区规则（取中点，要么到 20、要么停）才生效，
             * 宿主的定速下限也照它定 —— 不报的话界面显示 5%，风扇实际在 20%。
             */
            MinimumOnPercent: (int)UniwillFanProfileTable.MinimumRunningPercent,
            SupportsFirmwareCurveReset: true);

    /// <summary>
    /// 软件曲线的执行器：**直接写占空比寄存器**，不动固件那张表。
    ///
    /// 和 <see cref="TrySetDuty(int, double, out string?)"/> 走的是两条路，这是故意的：
    /// 固定转速走固件表（写一次就一直有效，我们退了也还在），
    /// 而软件曲线每 0.1 秒就要写一次 —— 每秒十次去重排固件的 16 区表，
    /// 既慢又毫无必要。这条路是 TUXEDO 参考实现用的那条，实测立刻生效
    /// （占空比到 200、转速 4482）。
    ///
    /// 代价是**风扇归我们按着**：全速模式位一置上，固件就不再自动调速了，
    /// 所以退出时必须交还 —— 那件事由核心统一负责。
    /// </summary>
    private bool TryDriveDutyCore(int fanIndex, double percent, out FanCommandErrorKind errorKind,
        out string? failureReason)
    {
        if (!TryResolveDutyAddress(fanIndex, out var address, out errorKind, out failureReason))
        {
            return false;
        }
        if (!double.IsFinite(percent))
        {
            return Fail(FanCommandErrorKind.InvalidRequest, "占空比得是个数。", out errorKind, out failureReason);
        }

        /*
         * 先置上"由我们直接给定转速"，否则固件照它自己的逻辑走，写了也不算数。
         *
         * **这一位是整机共用的**：置上之后两把风扇一起脱离固件自动控制。
         * 置位之前先记下这把风扇持有它 —— 置位失败时这一位也可能已经变了，
         * 交还时要照样把它算进去。
         *
         * 需实机确认（REVIEW B-M2）：置位之后，没有被直接写占空比的那把风扇
         * 是跟着它自己的占空比寄存器（0x1804/0x1809）走，还是被拉满，
         * 本文件的旧注释两种说法都有。行为没有改，只是不再断言其中一种。
         *
         * 也正因为如此，这台机器上**默认走固件曲线**：那条路没有这个副作用。
         *
         * 另一把正跑着我们的临时定速表时，置位会让那张表悄悄失效 —— 先拒绝，一个字节都不写。
         */
        if (!EnsureNoOtherTemporaryTable(fanIndex, out errorKind, out failureReason)) return false;
        ClaimSharedMode(fanIndex);
        if (!transport.WriteBit(
                UniwillFanProfileTable.FanModeRegister,
                UniwillFanProfileTable.FullFanModeBit,
                set: true))
        {
            return Fail(FanCommandErrorKind.HardwareFailed, "切不到直接给定转速的模式。", out errorKind, out failureReason);
        }
        if (!transport.WriteWithRetry(
                address,
                UniwillFanProfileTable.DutyFromPercent(Math.Clamp(percent, 0, 100))))
        {
            return Fail(FanCommandErrorKind.HardwareFailed, "写占空比失败。", out errorKind, out failureReason);
        }
        return Succeed(out errorKind, out failureReason);
    }

    /// <summary>
    /// 从 EC 里把这个风扇现在跑的曲线读出来。
    ///
    /// 表的结构是每个风扇 16 个温区、每区三个字节（结束温度 / 起始温度 / 转速），
    /// 三张表各占 16 字节。表里放的是出厂那条曲线（CPU 从 48°C 起风、GPU 从 46°C）。
    ///
    /// **注意：读得到不等于它正在生效。** `0x07C6` 的"用自定义表"位本机默认是**关**的，
    /// 那时这张表只是躺在 EC RAM 里的默认数据，改它风扇完全不理。
    /// 要让它变成活的，见 <see cref="TryWriteCurve(int, IReadOnlyList{FanCurvePoint}, out string?)"/>。
    ///
    /// 曲线的口径是**上升阈值**：一个点 (T, P) 的意思是"温度升到 T 时转速变成 P"。
    /// 落到表上就是"第 k-1 区的结束温度 = T，第 k 区的转速 = P" ——
    /// 和写曲线用的是同一个口径，两者互为逆运算。
    ///
    /// **不能改用起始温度。** 起始温度是**下降**阈值，它和结束温度差的那两度是回差；
    /// 读的时候报下降沿、写的时候当上升沿用，读出来再写回去整条曲线就会错位一格。
    ///
    /// 第一个点固定在 0 度，报的是 0 区的转速 —— 也就是"第一个阈值以下风扇跑多少"。
    /// 出厂表这一格是 0（不转）。少了这个点，回写时那一段只能靠猜。
    ///
    /// 起始温度读到 0xFF 的那些区是没用上的，表到此为止。
    /// </summary>
    public IReadOnlyList<FanCurvePoint>? ReadCurve(int fanIndex)
    {
        if (fanIndex is not (0 or 1))
        {
            return null;
        }

        var (endBase, startBase, speedBase) = UniwillFanTable.AddressesFor(fanIndex);

        if (transport.Read(speedBase) is not { } zeroSpeed)
        {
            return null;
        }
        var points = new List<FanCurvePoint>
        {
            new(0, UniwillFanProfileTable.PercentFromDuty(zeroSpeed))
        };

        for (var zone = 1; zone < UniwillFanTable.ZoneCount; zone++)
        {
            if (transport.Read((ushort)(startBase + zone)) is not { } start)
            {
                return null;
            }
            // 没用上的区。表就到这儿，后面的是残留，不要读进曲线。
            if (start == UnusedZone)
            {
                break;
            }
            if (transport.Read((ushort)(endBase + zone - 1)) is not { } risesAt
                || transport.Read((ushort)(speedBase + zone)) is not { } speed)
            {
                return null;
            }
            // 上一区没有上界，说明这一区根本进不去 —— 表结构坏了，不猜。
            if (risesAt == UnusedZone)
            {
                break;
            }
            points.Add(new FanCurvePoint(
                risesAt,
                UniwillFanProfileTable.PercentFromDuty(speed)));
        }
        return points.Count > 1 ? points : null;
    }

    /// <summary>起始温度是这个值就是"这一区没用上"。</summary>
    private const byte UnusedZone = UniwillFanTable.UnusedTemperature;

    /// <summary>
    /// 把一条曲线写进固件那张表。
    ///
    /// **是原地改这张表，不是另建一张**，而且**必须先把它启用**
    /// （<see cref="ActivateTable"/>）—— `0x07C6` 的启用位本机默认是关的，
    /// 不置上的话表写得再对，风扇也完全不理。
    ///
    /// 不必去动 <see cref="UniwillFanTable.SeparateTablesRegister"/>（0x07C5），
    /// 也不必把表填成"0 区 + 哑区"那条水平线 —— 启用之后照样是固件在查表，
    /// 我们的进程退了曲线也还在。
    ///
    /// **转速先写，阈值后写。** 写的过程中表是新旧混着的：
    /// 先落转速的话，那一刻的行为要么已经是新曲线、要么是旧阈值配新转速，
    /// 都落在两条曲线之间；反过来先落阈值，可能出现"高温区还挂着旧的低转速"。
    ///
    /// 每个字节都回读核对（<see cref="UniwillWmiEcTransport.Write"/> 自带，
    /// 而且回读是连读到一致才算数）。**这一点是踩过坑的**：单次读 EC 会偶发
    /// 返回假值，拿它判断"写进去没有"，会把成功判成失败、还会看见
    /// 根本没发生过的变化。
    /// </summary>
    private bool TryWriteCurveCore(
        int fanIndex,
        IReadOnlyList<FanCurvePoint> curve,
        out FanCommandErrorKind errorKind,
        out string? failureReason)
    {
        ArgumentNullException.ThrowIfNull(curve);
        if (!TryResolveDutyAddress(fanIndex, out _, out errorKind, out failureReason))
        {
            return false;
        }

        if (curve.Any(point => !double.IsFinite(point.TemperatureCelsius) || !double.IsFinite(point.Percent)))
        {
            return Fail(FanCommandErrorKind.InvalidRequest, "固件表或曲线包含无效值，未继续调整。",
                out errorKind, out failureReason);
        }

        var ordered = curve
            .OrderBy(point => point.TemperatureCelsius)
            .ToArray();
        if (ordered.Length == 0 || ordered.Length > UniwillFanTable.ZoneCount - 1)
        {
            return Fail(FanCommandErrorKind.InvalidRequest, $"曲线要有 1 到 {UniwillFanTable.ZoneCount - 1} 个点。",
                out errorKind, out failureReason);
        }

        var temperatures = new List<byte>(ordered.Length);
        var duties = new List<byte>(ordered.Length);
        foreach (var point in ordered)
        {
            // 温度夹在表能表达的范围里。0xFF 是"没上界"的标记，不能当成温度用。
            var celsius = Math.Clamp(Math.Round(point.TemperatureCelsius), 0, 0xFE);
            // 重复或倒退的温度会让某一区的宽度变成 0，固件查表会跳过它。
            // 0 度那个点例外：它是"第一个阈值以下"的转速，本来就排在最前面。
            if (temperatures.Count > 0 && (byte)celsius <= temperatures[^1])
            {
                continue;
            }
            temperatures.Add((byte)celsius);
            duties.Add(UniwillFanProfileTable.DutyFromPercent(point.Percent));
        }
        if (temperatures.Count == 0)
        {
            return Fail(FanCommandErrorKind.InvalidRequest, "曲线里没有温度递增的点。", out errorKind, out failureReason);
        }

        var zones = UniwillFanTable.LayOut(temperatures, duties);
        var (endBase, startBase, speedBase) = UniwillFanTable.AddressesFor(fanIndex);

        // 固件曲线要清掉整机模式位才生效；另一把还持有它时先说清楚，不碰任何东西。
        if (!EnsureNoOtherHolder(fanIndex, out errorKind, out failureReason)) return false;
        if (!TryPrepareTable(fanIndex, curve: true, out errorKind, out failureReason)) return false;
        // **先让固件开始查表，再写内容。** 见 ActivateTable 上那段。
        if (!ActivateTable(fanIndex, out errorKind, out failureReason))
        {
            return false;
        }

        // 转速先落地，理由见上面那段。
        for (var zone = 0; zone < UniwillFanTable.ZoneCount; zone++)
        {
            if (!transport.WriteWithRetry((ushort)(speedBase + zone), zones[zone].Speed))
            {
                return Fail(FanCommandErrorKind.HardwareFailed, $"写转速失败（第 {zone} 区）。",
                    out errorKind, out failureReason);
            }
        }
        for (var zone = 0; zone < UniwillFanTable.ZoneCount; zone++)
        {
            if (!transport.WriteWithRetry((ushort)(startBase + zone), zones[zone].Start)
                || !transport.WriteWithRetry((ushort)(endBase + zone), zones[zone].End))
            {
                return Fail(FanCommandErrorKind.HardwareFailed, $"写温度阈值失败（第 {zone} 区）。",
                    out errorKind, out failureReason);
            }
        }

        return TryCompleteCurve(fanIndex, out errorKind, out failureReason);
    }

    /// <summary>
    /// 让固件真的去查这张表。**必须在写表之前调。**
    ///
    /// 顺序是实测定下来的，不能反：先写表再开使能位的话，固件会在这一位
    /// 被置上的那一刻**用它自己的默认值把表重载一遍**，我们刚写的全没了 ——
    /// 现象是占空比落回温度驱动的那个值，看着就像"写了没用"。
    /// 先开位、再写表就正常。
    ///
    /// **这一步不做，整张表就是白写的。** 实测：把 16 个转速区全写成 100%、
    /// 逐字节回读也对，可真实占空比（<c>0x075B</c>）一直跟着温度走，
    /// 风扇完全不理。把 <c>0x07C6</c> 的 bit2 置上之后，同样一张表，
    /// 占空比六秒内从 131 爬到 200，风扇拉满 —— 差的就是这一位。
    ///
    /// 两件事要一起做：
    ///
    /// <list type="number">
    /// <item>置上"用自定义表"位。本机默认是**关**的，不是开的。</item>
    /// <item>清掉全速模式位 —— 那一位置着时固件根本不查表。调用方已经确认
    ///   没有别的风扇持有它（<see cref="EnsureNoOtherHolder"/>），这把风扇自己的那一份在这里放下。</item>
    /// </list>
    ///
    /// **不去动 <see cref="UniwillFanTable.SeparateTablesRegister"/>（0x07C5）。**
    /// 实测它全程是 0，而 CPU 表和 GPU 表本来就各自生效 —— 它不影响分表。
    /// 旧实现坚持先置这一位、置不上就整个放弃，那是走错了路。
    /// </summary>
    private bool ActivateTable(int fanIndex, out FanCommandErrorKind errorKind, out string? failureReason)
    {
        if (!SetBit(
                UniwillFanTable.UseCustomTablesRegister,
                UniwillFanTable.UseCustomTablesBit,
                out errorKind,
                out failureReason))
        {
            return false;
        }
        // 清位成功后才放下这把的持有：清不掉时位还置着，它仍算持有者，别的风扇照旧被拒、回执也不会报 firmware。
        if (!ClearFullFanMode(out errorKind, out failureReason)) return false;
        lock (holdersGate) sharedModeHolders.Remove(fanIndex);
        return true;
    }

    /// <summary>
    /// 现在的状态。
    ///
    /// **"自动"要如实报**：模式寄存器读不到就是 null（读不到不是"自动"）；
    /// 模式位置着时两把都不是自动（整机开关）；模式位清着时，跑着我们临时定速表的那把
    /// 也不是自动 —— 定速就是写表实现的，模式位本来就是清的，只看模式位会把定速报成自动。
    /// 临时表的事实来自恢复基线文件，读不出来同样是 null。
    /// </summary>
    public IReadOnlyList<FanState> Read()
    {
        var mode = transport.Read(UniwillFanProfileTable.FanModeRegister);
        bool? sharedTakeover = mode is { } value ? (value & UniwillFanProfileTable.FullFanModeBit) != 0 : null;
        var temporaryTables = sharedTakeover == false ? TemporaryTableFans() : null;
        bool? Automatic(int fanIndex) => sharedTakeover switch
        {
            null => null,
            true => false,
            false => temporaryTables is null ? null : !temporaryTables.Contains(fanIndex)
        };

        return
        [
            ReadFan(
                0,
                "主风扇",
                UniwillFanProfileTable.MainFanRpmHigh,
                UniwillFanProfileTable.MainFanRpmLow,
                UniwillFanProfileTable.MainFanPwm,
                Automatic(0)),
            ReadFan(
                1,
                "副风扇",
                UniwillFanProfileTable.SecondFanRpmHigh,
                secondFanRpmLow,
                UniwillFanProfileTable.SecondFanPwm,
                Automatic(1))
        ];
    }

    /// <summary>
    /// 固定转速：**写一条处处相同的曲线**。
    ///
    /// 这条链路没有"设一个占空比"的入口，它只有那张查表。
    /// 但固定转速本来就是曲线的一个特例 —— 一条水平线。所以不必另辟一条路，
    /// 走已经验证过的写表路径即可，而且固件仍然在查表，我们退了也还在。
    ///
    /// 先前这里是硬拒绝，理由是"`0x07C5` 分表使能写不进去、而且只能置位不能清位，
    /// 这条路走不回来"。**那个理由已经不成立**，两点都不对：
    ///
    /// <list type="number">
    /// <item>`0x07C5` 实测读回来是 `0x00`，并没有卡在 1 上；而且根本不需要动它 ——
    ///   出厂的 CPU 表和 GPU 表内容本来就不同、各自生效，固件本来就在分开查。</item>
    /// <item>当时那个"写不进去"的判断多半来自**单次回读**。实测单次读 EC 会偶发
    ///   返回假值；换成连读到一致之后，先前"怎么都写不进去"的字节第一次写就成功了。</item>
    /// </list>
    ///
    /// 也不需要"0 区 + 哑区"那套把表填成水平线的花招：表就在跑，原地改就是了。
    ///
    /// 0 以上、20% 以下会被抬到 20%（硬件死区，Describe 报为 MinimumOnPercent），
    /// 字节量化是 0.5%；<paramref name="appliedPercent"/> 报实际下发的那个百分比。
    /// </summary>
    private bool TrySetDutyCore(int fanIndex, double percent, out double? appliedPercent,
        out FanCommandErrorKind errorKind, out string? failureReason)
    {
        appliedPercent = null;
        if (!TryResolveDutyAddress(fanIndex, out _, out errorKind, out failureReason))
        {
            return false;
        }
        if (!double.IsFinite(percent))
        {
            return Fail(FanCommandErrorKind.InvalidRequest, "占空比得是个数。", out errorKind, out failureReason);
        }

        var duty = UniwillFanProfileTable.DutyFromPercent(Math.Clamp(percent, 0, 100));
        var (_, _, speedBase) = UniwillFanTable.AddressesFor(fanIndex);

        // 定速表要清掉整机模式位才生效；另一把还持有它时先说清楚，不碰任何东西。
        if (!EnsureNoOtherHolder(fanIndex, out errorKind, out failureReason)) return false;
        if (!TryPrepareTable(fanIndex, curve: false, out errorKind, out failureReason)) return false;

        // **先让固件开始查表，再写内容。** 反过来的话它会在使能的那一刻重载默认表。
        if (!ActivateTable(fanIndex, out errorKind, out failureReason))
        {
            return false;
        }

        /*
         * **只改转速表，一个温度阈值都不碰。**
         *
         * 每一区都是同一个转速，那么不管温度落在哪一区，风扇都跑这个值 ——
         * 这就是固定转速，而且固件看到的是一张结构完全正常的表。
         *
         * 先前这里是"写一条两点的水平曲线 (0°C, x) 和 (254°C, x)"，**实测不行**：
         * 下发和逐字节回读都报成功，但几秒后读回来温度表又是出厂的那张，
         * 只剩 0 区的转速留下了。原因是那种写法既塞进了 254°C 这种固件不认的阈值，
         * 又把出厂表的 1–10 区整个抹成"没用上" —— 固件把它自己的表恢复了。
         *
         * 教训：**改这张表要顺着它原本的结构改，不要重排它。**
         */
        for (var zone = 0; zone < UniwillFanTable.ZoneCount; zone++)
        {
            if (!transport.WriteWithRetry((ushort)(speedBase + zone), duty))
            {
                return Fail(FanCommandErrorKind.HardwareFailed, $"写转速失败（第 {zone} 区）。",
                    out errorKind, out failureReason);
            }
        }

        appliedPercent = UniwillFanProfileTable.PercentFromDuty(duty);
        return Succeed(out errorKind, out failureReason);
    }

    private bool SetBit(ushort address, byte bit, out FanCommandErrorKind errorKind, out string? failureReason)
        => WriteBit(address, bit, set: true, out errorKind, out failureReason);

    private bool WriteBit(ushort address, byte bit, bool set, out FanCommandErrorKind errorKind,
        out string? failureReason)
    {
        if (transport.WriteBit(address, bit, set))
        {
            return Succeed(out errorKind, out failureReason);
        }
        return Fail(FanCommandErrorKind.HardwareFailed, $"改不了 0x{address:X4} 的第 0x{bit:X2} 位。",
            out errorKind, out failureReason);
    }

    private bool ClearFullFanMode(out FanCommandErrorKind errorKind, out string? failureReason)
        => WriteBit(
            UniwillFanProfileTable.FanModeRegister,
            UniwillFanProfileTable.FullFanModeBit,
            set: false,
            out errorKind,
            out failureReason);

    /// <summary>这把风扇开始持有整机模式位。在置位之前调，失败的尝试也算持有。</summary>
    private void ClaimSharedMode(int fanIndex)
    {
        lock (holdersGate) sharedModeHolders.Add(fanIndex);
    }

    /// <summary>
    /// 这把风扇放下它持有的那一份。**还有别的风扇持有时不清位**：清了的话，
    /// 那把正在软件驱动或全速的风扇会悄悄回到固件表，而引擎和回执都不知道。
    /// 没有任何持有者（包括进程重启后内存里没有记录）时才清位。
    /// </summary>
    private bool ReleaseSharedMode(int fanIndex, out FanCommandErrorKind errorKind, out string? failureReason)
    {
        lock (holdersGate)
        {
            if (sharedModeHolders.Any(holder => holder != fanIndex))
            {
                sharedModeHolders.Remove(fanIndex);
                return Succeed(out errorKind, out failureReason);
            }
        }
        // 最后一份：清位成功后才放下，清不掉就仍算持有（见 ActivateTable）。
        if (!ClearFullFanMode(out errorKind, out failureReason)) return false;
        lock (holdersGate) sharedModeHolders.Remove(fanIndex);
        return true;
    }

    /// <summary>
    /// 置整机模式位（软件驱动、全速）之前：另一把正跑着我们的临时定速表时拒绝，因为置位会让那张表悄悄失效。
    /// 临时表的事实读不出来时同样拒绝（StateUnavailable）—— 不知道就不冒险。
    /// </summary>
    private bool EnsureNoOtherTemporaryTable(int fanIndex, out FanCommandErrorKind errorKind, out string? failureReason)
    {
        if (TemporaryTableFans() is not { } temporary)
            return Fail(FanCommandErrorKind.StateUnavailable, BaselineUnavailable, out errorKind, out failureReason);
        var others = temporary.Where(fan => fan != fanIndex).Order().ToArray();
        if (others.Length == 0) return Succeed(out errorKind, out failureReason);
        return Fail(FanCommandErrorKind.SharedTakeoverHeld,
            $"风扇 {string.Join("、", others)} 正跑着临时定速；这台机器上软件接管和全速是整机一个开关，会让那把的定速悄悄失效，未做任何改动。先让那把交还（auto）再接管。",
            out errorKind, out failureReason);
    }

    /// <summary>会清位的写入（定速表、固件曲线）只在没有别的风扇持有整机模式位时进行。</summary>
    private bool EnsureNoOtherHolder(int fanIndex, out FanCommandErrorKind errorKind, out string? failureReason)
    {
        int[] others;
        lock (holdersGate) others = sharedModeHolders.Where(holder => holder != fanIndex).Order().ToArray();
        if (others.Length == 0) return Succeed(out errorKind, out failureReason);
        return Fail(FanCommandErrorKind.SharedTakeoverHeld,
            $"风扇 {string.Join("、", others)} 正由软件或全速接管整机模式位；这台机器上定速和固件曲线要先结束那一把的接管才能生效。",
            out errorKind, out failureReason);
    }

    /// <summary>
    /// 全速：置上整机模式位。
    ///
    /// 需实机确认（REVIEW B-M2）：旧注释说"两个风扇一起拉满"，而软件驱动那边的注释说
    /// 置位后各自跟着自己的占空比寄存器走 —— 若是后者，这里给出的是占空比寄存器里残留的值，
    /// 不一定是满速。行为没有改。
    /// </summary>
    private bool TrySetFullSpeedCore(int fanIndex, out FanCommandErrorKind errorKind, out string? failureReason)
    {
        if (!TryResolveDutyAddress(fanIndex, out _, out errorKind, out failureReason))
        {
            return false;
        }
        if (!EnsureNoOtherTemporaryTable(fanIndex, out errorKind, out failureReason)) return false;
        ClaimSharedMode(fanIndex);
        if (!transport.WriteBit(
                UniwillFanProfileTable.FanModeRegister,
                UniwillFanProfileTable.FullFanModeBit,
                set: true))
        {
            return Fail(FanCommandErrorKind.HardwareFailed, "切不到全速模式。", out errorKind, out failureReason);
        }
        return Succeed(out errorKind, out failureReason);
    }

    private bool TrySetAutomaticCore(int fanIndex, out FanCommandErrorKind errorKind, out string? failureReason)
    {
        if (!TryResolveDutyAddress(fanIndex, out _, out errorKind, out failureReason))
        {
            return false;
        }
        return TryRestoreOwnedTable(fanIndex, resetCurve: false, out errorKind, out failureReason);
    }

    private bool TryResolveDutyAddress(
        int fanIndex,
        out ushort dutyAddress,
        out FanCommandErrorKind errorKind,
        out string? failureReason)
    {
        dutyAddress = 0;
        if (!writable)
        {
            return Fail(FanCommandErrorKind.ReadOnly, "这台机器的固件没有声明支持风扇控制，只读。",
                out errorKind, out failureReason);
        }
        switch (fanIndex)
        {
            case 0:
                dutyAddress = UniwillFanProfileTable.MainFanDuty;
                break;
            case 1:
                dutyAddress = UniwillFanProfileTable.SecondFanDuty;
                break;
            default:
                return Fail(FanCommandErrorKind.NoSuchFan, "没有这个风扇。", out errorKind, out failureReason);
        }
        return Succeed(out errorKind, out failureReason);
    }

    private static bool Fail(FanCommandErrorKind kind, string reason, out FanCommandErrorKind errorKind,
        out string? failureReason)
    {
        errorKind = kind;
        failureReason = reason;
        return false;
    }

    private static bool Succeed(out FanCommandErrorKind errorKind, out string? failureReason)
    {
        errorKind = FanCommandErrorKind.Other;
        failureReason = null;
        return true;
    }

    private FanState ReadFan(
        int index,
        string name,
        ushort rpmHigh,
        ushort rpmLow,
        ushort pwmAddress,
        bool? automatic)
    {
        // 高低字节由 profile 分别指定；OEM 副风扇的低字节地址可以在高字节之前。
        var rpm = transport.ReadBigEndianWord(rpmHigh, rpmLow);
        var duty = transport.Read(pwmAddress);
        return new FanState(
            index,
            name,
            // 读不到和读到 0 是两回事；0 转也是个有效读数（风扇停了）。
            rpm,
            duty is null ? null : UniwillFanProfileTable.PercentFromDuty(duty.Value),
            duty,
            writable,
            // 与 Describe 一致：固定转速走现有固件表，仍由同一能力位准入。
            SupportsDuty: writable,
            automatic);
    }

    public void Dispose() => transport.Dispose();
}
