using FanControlCore.Policy;
using FanControlCore.Protocols;

namespace FanControlCore.Discovery;

/// <summary>
/// 风扇核心：挑通道、守规矩、退出时收摊。
///
/// **发现顺序是有讲究的，不是随便排的**（见 README 的通道表）：
/// 有固件接口就不绕到 Raw EC 去，因为固件那条路是厂商自己维护的，
/// 而 Raw EC 是我们照着别人逆出来的寄存器表在猜。
///
/// <list type="number">
/// <item>Linux：先扫 hwmon。内核已经导出可写 PWM 就直接用，不必知道是什么品牌。</item>
/// <item>Windows：先探 OEM 的 WMI / ACPI 接口。</item>
/// <item>都没有：才落到 Raw EC，而且必须匹配固件指纹。</item>
/// </list>
///
/// **同一时刻只有一个后端持有写权限。** 两个后端同时写风扇，
/// 结果是谁也不知道现在是谁说了算。
/// </summary>
public sealed class FanCore : IDisposable
{
    private readonly List<IFanBackend> candidates = [];
    private IFanBackend? active;
    private string? unavailableReason;
    private bool disposed;

    /// <summary>被我们固定过的风扇。退出时要把它们交还固件。</summary>
    private readonly HashSet<int> overridden = [];

    private readonly TemporaryTakeoverJournal? journal;
    private bool recoveryReady;
    public bool CrashRecoveryEnabled => journal is not null;
    public IReadOnlyList<string> Diagnostics => candidates.SelectMany(candidate => candidate.Diagnostics)
        .Concat(engine?.Diagnostics ?? [])
        .Concat(active is not null && !recoveryReady && unavailableReason is not null ? [unavailableReason] : Array.Empty<string>()).ToArray();

    public FanCore() : this(FanCoreOptions.FromEnvironment()) { }

    public FanCore(FanCoreOptions options)
    {
        journal = string.IsNullOrWhiteSpace(options.StateDirectory) ? null : new(options);
        // **一级路由**：按平台筛，按通道类别排。这一步只看这台机器是什么系统，
        // 不碰任何硬件 —— 碰硬件是二级路由（各后端的识别依据）的事。
        var platform = OperatingSystem.IsLinux() ? FanPlatform.Linux : FanPlatform.Windows;
        foreach (var route in FanChannelRoute.For(platform, options))
        {
            candidates.Add(route.Create());
        }
    }

    /// <summary>Explicit backend selection for embedding and isolated testing.</summary>
    public FanCore(IFanBackend backend, FanCoreOptions? options = null)
        : this(new[] { backend ?? throw new ArgumentNullException(nameof(backend)) }, options) { }

    /// <summary>Explicit ordered candidates for embedding and isolated discovery tests.</summary>
    public FanCore(IEnumerable<IFanBackend> backends, FanCoreOptions? options = null)
    {
        journal = string.IsNullOrWhiteSpace(options?.StateDirectory) ? null : new(options);
        ArgumentNullException.ThrowIfNull(backends);
        foreach (var backend in backends)
            candidates.Add(backend ?? throw new ArgumentException("A backend candidate cannot be null.", nameof(backends)));
    }

    /// <summary>选中的通道叫什么。还没选中就是 null。</summary>
    public string? ActiveBackendName => active?.Name;

    /// <summary>没选中任何通道时，为什么。</summary>
    public string? UnavailableReason => unavailableReason;
    public FanOpenFailureKind? OpenFailureKind { get; private set; }
    public IReadOnlyList<FanOpenFailure> OpenFailures { get; private set; } = [];

    /// <summary>
    /// **二级路由**：在一级路由排好的候选里，逐条问它的识别依据命中没有。
    ///
    /// 各家的识别依据互不重叠（固件接口的 GUID、固件指纹），所以一台机器
    /// 最多命中一条 —— 这里的先后只决定"先问谁"，不决定答案。
    /// 一条都不命中就如实把每条的理由摆出来，而不是笼统说一句用不了。
    /// </summary>
    public bool Open()
    {
        if (active is not null)
        {
            return true;
        }

        var failures = new List<FanOpenFailure>();
        foreach (var candidate in candidates)
        {
            bool opened;
            FanOpenFailureKind kind;
            string? reason;
            try { opened = candidate.TryOpen(out kind, out reason); }
            catch (Exception error)
            {
                opened = false;
                kind = FanOpenFailureKind.Other;
                reason = error.Message;
            }
            if (opened)
            {
                active = candidate;
                GetEngine();
                unavailableReason = null;
                OpenFailureKind = null;
                OpenFailures = [];
                EnsureRecovery(out _);
                return true;
            }
            if (!Enum.IsDefined(kind)) kind = FanOpenFailureKind.Other;
            failures.Add(new(candidate.Name, kind, reason ?? "不可用"));
        }

        OpenFailures = failures.AsReadOnly();
        OpenFailureKind = failures.Any(failure => failure.Kind == FanOpenFailureKind.Other) ? FanOpenFailureKind.Other
            : failures.Any(failure => failure.Kind == FanOpenFailureKind.AccessDenied) ? FanOpenFailureKind.AccessDenied
            : failures.Any(failure => failure.Kind == FanOpenFailureKind.DriverMissing) ? FanOpenFailureKind.DriverMissing
            : FanOpenFailureKind.NoSupportedController;
        unavailableReason = failures.Count == 0
            ? "这个平台上还没有可用的风扇通道。"
            : string.Join("；", failures.Select(failure => $"{failure.Backend}：{failure.Reason}"));
        return false;
    }

    /// <summary>返回已探测能力；显式刷新才重新探测，不改变设定或所有权。</summary>
    public IReadOnlyList<FanDescription> Describe(bool refresh = false)
    {
        if (refresh)
        {
            if (active is null) Open();
            else active.RefreshDetection();
        }
        return active?.Describe() ?? [];
    }

    public IReadOnlyList<FanState> Read()
        => active?.Read() ?? [];

    /// <summary>已记录的软件计划或固件曲线；没有可读曲线时返回 null。</summary>
    public IReadOnlyList<FanCurvePoint>? ReadCurve(int fanIndex)
        => engine?.RecordedCurve(fanIndex) ?? active?.ReadCurve(fanIndex);

    public bool TrySetDuty(int fanIndex, double percent, out string? failureReason)
        => TrySetDuty(fanIndex, percent, out _, out _, out failureReason);

    /// <summary>
    /// 定速，带类型化的失败原因；成功时 <paramref name="appliedPercent"/> 是通道实际下发的百分比
    /// （夹到最低启动档、按字节量化之后），通道不报就是 null。
    /// </summary>
    public bool TrySetDuty(int fanIndex, double percent, out double? appliedPercent,
        out FanCommandErrorKind errorKind, out string? failureReason)
    {
        appliedPercent = null;
        if (!TryDescribeWritable(fanIndex, out var fan, out errorKind, out failureReason)) return false;
        if (!fan.SupportsDuty)
            return Fail(FanCommandErrorKind.Unsupported, "这条通道给不了任意占空比。", out errorKind, out failureReason);
        if (!double.IsFinite(percent))
            return Fail(FanCommandErrorKind.InvalidRequest, "占空比得是个数。", out errorKind, out failureReason);
        if (!EnsureRecovery(out failureReason))
            return Fail(FanCommandErrorKind.RecoveryBlocked, failureReason, out errorKind, out failureReason);
        if (ControlGroupOf(fanIndex) is { } group)
        {
            /*
             * 控制组在软件控制下：定速就是这把风扇自己的一条平直软件计划，由运行入口写下去，
             * 和组里别的风扇的曲线一样受断温、失败和整组交还的规矩管。实际下发值看之后的读数。
             * 组在固件控制下：只有定速本身由固件执行（Uniwill 的固件表）时才能单独给一把。
             */
            if (IsSoftwareGroup(group)) return TryRecordFixed(fan, percent, out errorKind, out failureReason);
            if (!group.FirmwareFixedDuty)
                return GroupSwitchRequired(fan, group, FanGroupControl.Software, "定速", out errorKind, out failureReason);
        }
        if (!EnsureSharedTakeoverFree(fan, out errorKind, out failureReason)) return false;
        if (!PrepareTemporaryTakeover(fanIndex, out errorKind, out failureReason)) return false;
        if (!StopSoftwarePlan(fanIndex, out errorKind, out failureReason)) return false;
        if (!active!.TrySetDuty(fanIndex, percent, out appliedPercent, out errorKind, out failureReason))
        {
            if (active.HasPendingTemporaryRestore(fanIndex)) { lock (overridden) overridden.Add(fanIndex); }
            return false;
        }
        // 记下来，退出时要交还固件。
        lock (overridden) overridden.Add(fanIndex);
        return true;
    }

    /// <summary>全速。给不了任意占空比的通道也有这一档。</summary>
    public bool TrySetFullSpeed(int fanIndex, out string? failureReason)
        => TrySetFullSpeed(fanIndex, out _, out failureReason);

    public bool TrySetFullSpeed(int fanIndex, out FanCommandErrorKind errorKind, out string? failureReason)
    {
        if (!TryDescribeWritable(fanIndex, out var fan, out errorKind, out failureReason)) return false;
        if (!EnsureRecovery(out failureReason))
            return Fail(FanCommandErrorKind.RecoveryBlocked, failureReason, out errorKind, out failureReason);
        if (ControlGroupOf(fanIndex) is { } group)
        {
            // 全速在共用开关的通道上就是软件接管：组在软件控制下它是一条 100% 的定速计划，否则要整组先切。
            if (IsSoftwareGroup(group)) return TryRecordFixed(fan, 100, out errorKind, out failureReason);
            return GroupSwitchRequired(fan, group, FanGroupControl.Software, "全速", out errorKind, out failureReason);
        }
        if (!PrepareTemporaryTakeover(fanIndex, out errorKind, out failureReason)) return false;
        if (!StopSoftwarePlan(fanIndex, out errorKind, out failureReason)) return false;
        if (!active!.TrySetFullSpeed(fanIndex, out errorKind, out failureReason))
        {
            return false;
        }
        lock (overridden) overridden.Add(fanIndex);
        return true;
    }

    public bool TrySetAutomatic(int fanIndex, out string? failureReason, bool resetFirmwareCurve = false)
        => TrySetAutomatic(fanIndex, resetFirmwareCurve, out _, out failureReason);

    /// <summary>
    /// 交还固件，带类型化的失败原因。
    ///
    /// 控制组里的风扇在组处于软件控制时不能单独交还：回到固件永远是整组一起
    /// （<see cref="TrySetGroupControl"/>），否则拒绝为 <see cref="FanCommandErrorKind.ControlGroupSwitchRequired"/>。
    /// 组在固件控制下，这里交还的是这把风扇自己的临时定速表，或者撤销它的固件曲线。
    ///
    /// 共用开关计数持有者的通道（Uniwill）上，还有别的风扇持有整机开关时，这把风扇放下自己那一份也回不到固件
    /// （整组切到固件失败、开关还被另一把持有的那段时间）：放下的那一份照样生效，但如实拒绝为
    /// <see cref="FanCommandErrorKind.SharedTakeoverHeld"/>，不报成功。
    /// </summary>
    public bool TrySetAutomatic(int fanIndex, bool resetFirmwareCurve, out FanCommandErrorKind errorKind,
        out string? failureReason)
    {
        if (!TryDescribe(fanIndex, out var fan, out errorKind, out failureReason)) return false;
        if (!EnsureRecovery(out failureReason))
            return Fail(FanCommandErrorKind.RecoveryBlocked, failureReason, out errorKind, out failureReason);
        if (ControlGroupOf(fanIndex) is { } group && IsSoftwareGroup(group))
            return GroupSwitchRequired(fan, group, FanGroupControl.Firmware, "交还固件", out errorKind, out failureReason);
        if (!StopSoftwarePlan(fanIndex, out errorKind, out failureReason)) return false;
        var handed = TryHandBack(fanIndex, resetFirmwareCurve, out var released, out errorKind, out failureReason);
        if (released && fan.SoftwareTakeoverCoversAllFans && active!.SharedTakeoverCountsHolders
            && active.SharedTakeoverHolders.Count > 0)
            return Fail(FanCommandErrorKind.SharedTakeoverHeld,
                $"风扇 {fanIndex} 的交还已由通道确认（它那一份整机开关已放下），但风扇 " +
                $"{string.Join("、", active.SharedTakeoverHolders.Order())} 仍持有整机开关，这把还没回到固件控制；整组再切一次到固件可完成交还。",
                out errorKind, out failureReason);
        return handed;
    }

    /// <summary>
    /// The backend's own release of one fan (temporary table, override, or curve reset), with its bookkeeping.
    /// Never throws: a fault in the backend call is a failure of kind Other. <paramref name="released"/> says whether the
    /// backend confirmed the release; it can be true on a failure, when only removing the fan's crash-recovery record
    /// failed (StateUnavailable). That record then stays, and the next recovery releases the fan again, harmlessly.
    /// On a channel that counts holders a confirmed release may only have given up this fan's share of the shared
    /// switch, so the caller decides whether the fan is under firmware.
    /// </summary>
    private bool TryHandBack(int fanIndex, bool resetFirmwareCurve, out bool released, out FanCommandErrorKind errorKind,
        out string? failureReason)
    {
        try
        {
            released = resetFirmwareCurve ? active!.TryResetFirmwareCurve(fanIndex, out errorKind, out failureReason)
                : active!.TrySetAutomatic(fanIndex, out errorKind, out failureReason);
        }
        catch (Exception error)
        {
            released = false;
            return Fail(FanCommandErrorKind.Other, $"风扇 {fanIndex} 交还固件时出错：{error.Message}", out errorKind, out failureReason);
        }
        if (!released) return false;
        return TryForgetTakeover(fanIndex, "的交还已由通道确认", out errorKind, out failureReason);
    }

    /// <summary>
    /// After the backend confirmed that this fan is no longer a temporary takeover of ours (a hand-back, or a completed
    /// firmware curve): drop it from <c>overridden</c>, then from the crash-recovery journal. Never throws: a journal that
    /// cannot be saved is StateUnavailable, with the hardware change standing; the entry stays until a retry removes it or
    /// the next recovery releases the fan again, which keeps a completed curve and is otherwise harmless.
    /// </summary>
    private bool TryForgetTakeover(int fanIndex, string done, out FanCommandErrorKind errorKind, out string? failureReason)
    {
        lock (overridden) overridden.Remove(fanIndex);
        try { journal?.Released(active!, fanIndex); }
        catch (Exception error)
        {
            return Fail(FanCommandErrorKind.StateUnavailable,
                $"风扇 {fanIndex} {done}，但恢复记录没能更新：{error.Message}", out errorKind, out failureReason);
        }
        errorKind = FanCommandErrorKind.Other;
        failureReason = null;
        return true;
    }

    /// <summary>Fans that share one control switch on the open channel. Empty before discovery or without such a switch.</summary>
    public IReadOnlyList<FanControlGroup> ControlGroups => active?.ControlGroups ?? [];

    /// <summary>The control group this fan belongs to, or null.</summary>
    public FanControlGroup? ControlGroupOf(int fanIndex) => ControlGroups.FirstOrDefault(group => group.Fans.Contains(fanIndex));

    /// <summary>Who controls the group as last applied (<see cref="TrySetGroupControl"/>); null for an unknown group id.</summary>
    public FanGroupControl? GroupControl(string groupId)
        => ControlGroups.FirstOrDefault(group => group.Id == groupId) is { } group
            ? IsSoftwareGroup(group) ? FanGroupControl.Software : FanGroupControl.Firmware
            : null;

    private readonly object groupGate = new();
    /// <summary>Groups the host switched to software control. In memory: a new session starts every group under firmware.</summary>
    private readonly HashSet<string> softwareGroups = [];
    /// <summary>
    /// Members whose last switch to firmware is not confirmed: their hand-back failed, or (on a channel that counts
    /// holders) the shared switch was still held when that call ended, so they were not back under firmware. The next
    /// switch of their group to firmware hands them back again and lists them once they are. A member that has since
    /// started a temporary fixed-duty table of its own is firmware control again and leaves this set.
    /// </summary>
    private readonly HashSet<int> firmwareSwitchPending = [];
    private bool IsSoftwareGroup(FanControlGroup group) { lock (groupGate) return softwareGroups.Contains(group.Id); }

    private static bool GroupSwitchRequired(FanDescription fan, FanControlGroup group, FanGroupControl needed, string what,
        out FanCommandErrorKind errorKind, out string? failureReason)
        => Fail(FanCommandErrorKind.ControlGroupSwitchRequired,
            $"风扇 {fan.Index} 属于控制组 {group.Id}（{group.Reason}，风扇 {string.Join("、", group.Fans)} 共用一个控制开关）：" +
            $"{what}要整组先切到{(needed == FanGroupControl.Software ? "软件" : "固件")}控制，未做任何改动。",
            out errorKind, out failureReason);

    /// <summary>
    /// 切换一个控制组：整组一起在固件控制和软件控制之间切换，一次调用完成。
    ///
    /// <list type="bullet">
    /// <item><b>切到软件</b>：<paramref name="assignments"/> 必须给组里每一把风扇恰好一项（定速或软件曲线），
    ///   先全部核对，有一项不对就什么都不改。组在固件控制下时，先把成员的临时定速表交还固件
    ///   （否则它们会被整机开关悄悄作废），再一次记录全部计划。硬件接管在运行入口里发生，
    ///   而且整组同一轮一起开始（引擎的起步规则）。组已经在软件控制下时，这是一次整组替换。</item>
    /// <item><b>切到固件</b>：撤掉全部成员的计划并逐把交还；整组的开关在最后一把交还时放下。
    ///   一把交还失败不挡其余几把；组照样记为固件控制，失败的那把由运行入口继续重试交还，
    ///   再发一次切到固件也会对没确认回到固件的成员重试。不需要 <paramref name="assignments"/>。</item>
    /// </list>
    ///
    /// 库里不做"暂存"：宿主在界面上暂存切换，用户应用时才调这里。
    /// </summary>
    public bool TrySetGroupControl(string groupId, FanGroupControl control, IReadOnlyList<FanGroupAssignment>? assignments,
        out FanCommandErrorKind errorKind, out string? failureReason)
        => TrySetGroupControl(groupId, control, assignments, out _, out errorKind, out failureReason);

    /// <summary>
    /// As above; <paramref name="handedBack"/> lists the members this call put back under firmware control, also when it
    /// fails: on a switch to software the members whose temporary fixed-duty tables were handed back first, on a switch
    /// to firmware the members now under firmware. On a channel that counts holders of its shared switch (Uniwill) no
    /// member is under firmware while any fan still holds the switch, so such a failed call lists none. The members stay
    /// pending: usually the run's retry then clears the switch, and the host must send the switch to firmware again; that
    /// call (or any later one that ends with the switch released) lists every pending member, including those whose share
    /// an earlier call released. Hardware hand-back is not transactional and is not rolled back. Does not throw for
    /// hardware or journal faults: they are failures of kind Other or StateUnavailable with <paramref name="handedBack"/>
    /// as far as it got. The error kind and text describe one failure: the first real one in member order, else (switch
    /// still held) SharedTakeoverHeld, else the first release that was confirmed but could not be recorded; the state of
    /// each member is in <paramref name="handedBack"/> and the fans' readings.
    /// </summary>
    public bool TrySetGroupControl(string groupId, FanGroupControl control, IReadOnlyList<FanGroupAssignment>? assignments,
        out IReadOnlyList<int> handedBack, out FanCommandErrorKind errorKind, out string? failureReason)
    {
        var done = new List<int>();
        handedBack = done;
        if (active is null)
            return Fail(FanCommandErrorKind.Unavailable, unavailableReason ?? "没有可用的风扇通道。", out errorKind, out failureReason);
        if (ControlGroups.FirstOrDefault(entry => entry.Id == groupId) is not { } group)
            return Fail(FanCommandErrorKind.InvalidRequest, $"没有控制组 {groupId}。", out errorKind, out failureReason);
        try
        {
            if (!EnsureRecovery(out failureReason))
                return Fail(FanCommandErrorKind.RecoveryBlocked, failureReason, out errorKind, out failureReason);
            return control == FanGroupControl.Software
                ? TrySwitchGroupToSoftware(group, assignments ?? [], done, out errorKind, out failureReason)
                : TrySwitchGroupToFirmware(group, done, out errorKind, out failureReason);
        }
        catch (Exception error)
        {
            // Hand-backs catch their own faults; this keeps done for anything else (a transport fault during first-time
            // recovery, a fault while describing the fans).
            return Fail(FanCommandErrorKind.Other, $"控制组 {group.Id} 切换时出错：{error.Message}", out errorKind, out failureReason);
        }
    }

    private bool TrySwitchGroupToSoftware(FanControlGroup group, IReadOnlyList<FanGroupAssignment> assignments, List<int> done,
        out FanCommandErrorKind errorKind, out string? failureReason)
    {
        // Validate everything before changing anything.
        if (assignments.Count != group.Fans.Count || assignments.Select(entry => entry.FanIndex).Distinct().Count() != assignments.Count
            || assignments.Any(entry => !group.Fans.Contains(entry.FanIndex)))
            return Fail(FanCommandErrorKind.InvalidRequest,
                $"切到软件控制要给控制组 {group.Id} 的每一把风扇（{string.Join("、", group.Fans)}）各一项设定，不多不少。",
                out errorKind, out failureReason);
        var plans = new List<SoftwareCurvePlan>();
        foreach (var entry in assignments)
        {
            if (!TryDescribeWritable(entry.FanIndex, out var fan, out errorKind, out failureReason)) return false;
            if (!fan.SupportsSoftwareCurve)
                return Fail(FanCommandErrorKind.Unsupported, $"风扇 {fan.Index} 给不了软件控制。", out errorKind, out failureReason);
            if ((entry.Percent is null) == (entry.Curve is null))
                return Fail(FanCommandErrorKind.InvalidRequest, $"风扇 {fan.Index} 要恰好给一项：定速或曲线。", out errorKind, out failureReason);
            if (entry.Percent is { } fixedPercent)
            {
                if (!TryFixedPlan(fan, fixedPercent, out var fixedPlan, out errorKind, out failureReason)) return false;
                plans.Add(fixedPlan);
            }
            else
            {
                if (!TryCheckSoftwareCurve(fan, entry.Curve!, out errorKind, out failureReason)) return false;
                plans.Add(CurvePlan(fan, entry.Curve!));
            }
        }

        if (!IsSoftwareGroup(group))
        {
            /*
             * A member's temporary fixed-duty table would be silently stopped by the shared switch: hand it back first.
             * Not transactional: a table handed back stays handed back when a later member's hand-back fails and the call
             * is refused. handedBack names those fans, so the host can refresh what it shows as applied.
             */
            foreach (var index in group.Fans)
            {
                if (!active!.HasPendingTemporaryRestore(index)) continue;
                var handed = TryHandBack(index, resetFirmwareCurve: false, out var released, out errorKind, out failureReason);
                if (released) done.Add(index);
                if (!handed) return false;
            }
        }
        if (!GetEngine().TryRecordPlans(plans, out errorKind, out failureReason)) return false;
        lock (groupGate)
        {
            softwareGroups.Add(group.Id);
            // A later switch to firmware covers every member anyway.
            firmwareSwitchPending.ExceptWith(group.Fans);
        }
        return true;
    }

    private bool TrySwitchGroupToFirmware(FanControlGroup group, List<int> done, out FanCommandErrorKind errorKind,
        out string? failureReason)
    {
        errorKind = FanCommandErrorKind.Other;
        failureReason = null;
        // The host asked for firmware: record that first, so no per-fan request re-enters software meanwhile.
        bool wasSoftware;
        int[] pending;
        lock (groupGate)
        {
            wasSoftware = softwareGroups.Remove(group.Id);
            pending = group.Fans.Where(firmwareSwitchPending.Contains).ToArray();
        }
        // A pending member that has since started a temporary fixed-duty table of its own is firmware control again.
        var settled = pending.Where(index => active!.HasPendingTemporaryRestore(index)).ToArray();
        lock (groupGate) firmwareSwitchPending.ExceptWith(settled);
        /*
         * Already under firmware: only a member whose earlier hand-back is unconfirmed (still in the engine, or pending
         * from an earlier call) is handed back again; a firmware-executed fixed-duty table (FirmwareFixedDuty) is
         * firmware control and stays.
         */
        var members = group.Fans.Where(index => wasSoftware || engine?.HasPlan(index) == true || engine?.HasRuntime(index) == true
            || pending.Contains(index) && !settled.Contains(index)).ToArray();
        // The reported failure: the first real one in member order, ranked above a release that was confirmed but not recorded.
        (FanCommandErrorKind Kind, string? Reason)? failed = null, unrecorded = null;
        /*
         * One batch stop removes every member's plan and marks every runtime as being handed back in one state change,
         * so no run tick drives a member again (on Acer that would switch the whole machine back to custom mode) while
         * the others are released. Then each member is handed back in order through the backend, which is what decides
         * the result; a failure (also a fault) does not stop the rest. A failed engine release stays pending for the run
         * to retry.
         */
        try { engine?.TryStopFans(members, out _, out _); }
        catch (Exception error) { failed = (FanCommandErrorKind.Other, $"控制组 {group.Id} 停软件计划时出错：{error.Message}"); }
        var released = new List<int>();
        var unconfirmed = new List<int>();
        foreach (var index in members)
        {
            var handed = TryHandBack(index, resetFirmwareCurve: false, out var backendReleased, out var kind, out var reason);
            if (backendReleased) released.Add(index);
            if (handed) continue;
            unconfirmed.Add(index);
            if (backendReleased) unrecorded ??= (kind, reason);
            else failed ??= (kind, reason);
        }
        /*
         * On a channel that counts holders of its shared switch (Uniwill), a member's release only gives up its share
         * while another fan still holds it: the switch stays set and no member is under firmware. So such a call lists
         * nobody, and its members stay pending for the next switch to firmware that ends with the switch released.
         */
        var switchHeld = active!.SharedTakeoverCountsHolders && active.SharedTakeoverHolders.Count > 0;
        lock (groupGate)
        {
            if (switchHeld) firmwareSwitchPending.UnionWith(members);
            else
            {
                firmwareSwitchPending.ExceptWith(members.Except(unconfirmed));
                firmwareSwitchPending.UnionWith(unconfirmed);
            }
        }
        if (!switchHeld) done.AddRange(released);
        // With the switch still held, that is what matters next to a real failure: an unrecorded release is not under firmware.
        (FanCommandErrorKind Kind, string? Reason)? first = failed ?? (switchHeld
            ? (FanCommandErrorKind.SharedTakeoverHeld,
                $"风扇 {string.Join("、", active.SharedTakeoverHolders.Order())} 仍持有整机开关，组 {group.Id} 还没有回到固件控制。")
            : unrecorded);
        if (first is not { } failure) return true;
        (errorKind, failureReason) = failure;
        return false;
    }

    /// <summary>
    /// A software curve checked against what the fan accepts, as fixed speeds are (<see cref="TryFixedPlan"/>): at least
    /// one point, finite values, and every point's percent within 0 to 100, else InvalidRequest. A point below the fan's
    /// floor is valid; the run's dead zone decides what it writes: on a fan that cannot stop it holds the floor, otherwise
    /// a target of at least half the floor is held at the floor and a lower target stops the fan.
    /// </summary>
    private static bool TryCheckSoftwareCurve(FanDescription fan, IReadOnlyList<FanCurvePoint> curve,
        out FanCommandErrorKind errorKind, out string? failureReason)
    {
        if (curve.Count == 0)
            return Fail(FanCommandErrorKind.InvalidRequest, $"风扇 {fan.Index} 的曲线上一个点都没有。", out errorKind, out failureReason);
        foreach (var point in curve)
        {
            if (point is null || !double.IsFinite(point.TemperatureCelsius) || !double.IsFinite(point.Percent))
                return Fail(FanCommandErrorKind.InvalidRequest, $"风扇 {fan.Index} 的曲线有无效值。", out errorKind, out failureReason);
            if (point.Percent is < 0 or > 100)
                return Fail(FanCommandErrorKind.InvalidRequest,
                    $"风扇 {fan.Index} 的曲线点（{point.TemperatureCelsius:0.#} °C，{point.Percent:0.#} %）超出 0 到 100 的占空比。",
                    out errorKind, out failureReason);
        }
        errorKind = FanCommandErrorKind.Other;
        failureReason = null;
        return true;
    }

    /// <summary>
    /// A fixed speed as a flat plan, checked against what the fan accepts: 0 to 100 %; a value between 0 and the fan's
    /// floor (<see cref="FanDescription.MinimumOnPercent"/>) is raised to the floor, as the channel itself would; 0 on a
    /// fan that cannot stop (<see cref="FanDescription.CanStop"/>, Acer) is refused as Unsupported rather than turned
    /// into a different speed.
    /// </summary>
    private static bool TryFixedPlan(FanDescription fan, double percent, out SoftwareCurvePlan plan,
        out FanCommandErrorKind errorKind, out string? failureReason)
    {
        plan = null!;
        if (!double.IsFinite(percent) || percent is < 0 or > 100)
            return Fail(FanCommandErrorKind.InvalidRequest, $"风扇 {fan.Index} 的占空比要在 0 到 100 之间。", out errorKind, out failureReason);
        if (percent == 0 && !fan.CanStop)
            return Fail(FanCommandErrorKind.Unsupported,
                $"风扇 {fan.Index} 的通道停不了转：它只收 {fan.MinimumOnPercent} 到 100 的占空比。", out errorKind, out failureReason);
        plan = FixedPlan(fan.Index, percent > 0 && percent < fan.MinimumOnPercent ? fan.MinimumOnPercent : percent);
        errorKind = FanCommandErrorKind.Other;
        failureReason = null;
        return true;
    }

    private SoftwareCurvePlan CurvePlan(FanDescription described, IReadOnlyList<FanCurvePoint> curve)
    {
        var kind = described.Role switch
        {
            FanRoles.Cpu => FanTemperatureKinds.Cpu,
            FanRoles.Gpu => FanTemperatureKinds.Gpu,
            _ => FanTemperatureKinds.Any
        };
        return new SoftwareCurvePlan(
            described.Index,
            curve,
            [new FanTemperatureSource(kind, () => Temperatures.ReadKind(kind))],
            TemperatureKind: kind);
    }

    /// <summary>
    /// A fixed speed inside a software-controlled group: a flat plan on a constant source. It needs no temperature
    /// (its kind is not one hosts subscribe to), cannot go blind, and is written at once rather than ramped.
    /// </summary>
    private static SoftwareCurvePlan FixedPlan(int fanIndex, double percent)
        => new(fanIndex, [new FanCurvePoint(0, Math.Clamp(percent, 0, 100))], [new FanTemperatureSource(FixedSource, static () => 0)],
            PercentPerSecond: 1e9, TemperatureKind: FixedSource);

    private const string FixedSource = "fixed";

    private bool TryRecordFixed(FanDescription fan, double percent, out FanCommandErrorKind errorKind, out string? failureReason)
    {
        if (!fan.SupportsSoftwareCurve)
            return Fail(FanCommandErrorKind.Unsupported, $"风扇 {fan.Index} 给不了软件控制。", out errorKind, out failureReason);
        if (!TryFixedPlan(fan, percent, out var plan, out errorKind, out failureReason)) return false;
        return GetEngine().TryRecordPlan(plan, out errorKind, out failureReason);
    }

    /// <summary>The fan as described by the open channel, or a typed Unavailable / NoSuchFan failure.</summary>
    private bool TryDescribe(int fanIndex, out FanDescription fan, out FanCommandErrorKind errorKind,
        out string? failureReason)
    {
        fan = null!;
        if (active is null)
            return Fail(FanCommandErrorKind.Unavailable, unavailableReason ?? "没有可用的风扇通道。",
                out errorKind, out failureReason);
        if (active.Describe().FirstOrDefault(entry => entry.Index == fanIndex) is not { } described)
            return Fail(FanCommandErrorKind.NoSuchFan, "没有这个风扇。", out errorKind, out failureReason);
        fan = described;
        errorKind = FanCommandErrorKind.Other;
        failureReason = null;
        return true;
    }

    private bool TryDescribeWritable(int fanIndex, out FanDescription fan, out FanCommandErrorKind errorKind,
        out string? failureReason)
    {
        if (!TryDescribe(fanIndex, out fan, out errorKind, out failureReason)) return false;
        if (!fan.Writable)
            return Fail(FanCommandErrorKind.ReadOnly, "这把风扇只读：通道没有声明可写。", out errorKind, out failureReason);
        return true;
    }

    /// <summary>
    /// 整机接管的通道上，定速和固件曲线都由固件执行，要结束整机接管才生效。
    /// 另一把还持有接管时拒绝，**在改动任何计划或硬件之前** —— 否则要么悄悄关掉
    /// 那一把的软件曲线或全速，要么报"已应用"而这一把根本没生效。
    /// </summary>
    private bool EnsureSharedTakeoverFree(FanDescription fan, out FanCommandErrorKind errorKind,
        out string? failureReason)
    {
        errorKind = FanCommandErrorKind.Other;
        failureReason = null;
        if (!fan.SoftwareTakeoverCoversAllFans) return true;
        var others = active!.SharedTakeoverHolders.Where(holder => holder != fan.Index).Order().ToArray();
        if (others.Length == 0) return true;
        return Fail(FanCommandErrorKind.SharedTakeoverHeld,
            $"风扇 {string.Join("、", others)} 正由软件曲线或全速接管整机模式；这把风扇的定速或固件曲线要等那一把结束接管才能生效，未做任何改动。",
            out errorKind, out failureReason);
    }

    /// <summary>撤掉这把风扇的软件计划并交还它（若引擎接管过它）。</summary>
    private bool StopSoftwarePlan(int fanIndex, out FanCommandErrorKind errorKind, out string? failureReason)
    {
        errorKind = FanCommandErrorKind.Other;
        failureReason = null;
        return !(engine?.HasPlan(fanIndex) == true || engine?.HasRuntime(fanIndex) == true)
            || engine.TryStop(fanIndex, out errorKind, out failureReason);
    }

    private static bool Fail(FanCommandErrorKind kind, string? reason, out FanCommandErrorKind errorKind,
        out string? failureReason)
    {
        errorKind = kind;
        failureReason = reason;
        return false;
    }

    /// <summary>宿主推过来的温度。软件曲线要用，固件曲线用不上。</summary>
    public FanTemperatureStore Temperatures { get; } = new();

    /// <summary>Atomically replace the pushed temperature batch without JSON conversion.</summary>
    public void PushTemperatures(IReadOnlyList<FanTemperatureSample> samples) => Temperatures.Push(samples);

    /// <summary>
    /// 这台机器的软件曲线要不要宿主推温度。
    ///
    /// 走固件曲线的机器**一次都不用推** —— 曲线交给固件之后就不需要我们参与了。
    /// 宿主据此决定要不要起那条 0.1 秒的订阅，不做无用功。
    /// </summary>
    public bool NeedsTemperatures
        => Describe().Any(fan => fan.SupportsSoftwareCurve);

    /// <summary>
    /// 把一条曲线交给这个风扇。**走哪条路由这条通道的能力决定。**
    ///
    /// <list type="bullet">
    /// <item><b>固件曲线</b>（fw-xy / fw-table）：写进固件的表，之后固件自己执行。
    ///   写完**不记进 <c>overridden</c>、退出时也不撤销** —— 这正是它和定速的
    ///   根本区别：定速是我们在替固件管风扇，人走了必须还回去；曲线是改固件自己
    ///   查的那张表，写进去就该一直算数，重启也还在。退出时偷偷改回去，
    ///   等于用户设的东西悄悄失效。</item>
    /// <item><b>软件曲线</b>（sw-curve）：底层只能设固定转速，所以曲线由
    ///   <see cref="RunSoftwareCurveAsync"/> 执行。这里只记录计划，
    ///   运行入口在首次驱动前准备临时接管，返回时交还固件。</item>
    /// </list>
    ///
    /// 界面上"固件跑曲线 / 软件跑曲线"那个词条，说的就是这两者的区别。
    /// </summary>
    public bool TryWriteCurve(
        int fanIndex,
        IReadOnlyList<FanCurvePoint> curve,
        string? execution,
        out string? failureReason)
        => TryWriteCurve(fanIndex, curve, execution, out _, out failureReason);

    public bool TryWriteCurve(
        int fanIndex,
        IReadOnlyList<FanCurvePoint> curve,
        string? execution,
        out FanCommandErrorKind errorKind,
        out string? failureReason)
    {
        ArgumentNullException.ThrowIfNull(curve);
        if (!TryDescribeWritable(fanIndex, out var described, out errorKind, out failureReason)) return false;
        if (curve.Count == 0)
            return Fail(FanCommandErrorKind.InvalidRequest, "曲线上一个点都没有。", out errorKind, out failureReason);
        if (!EnsureRecovery(out failureReason))
            return Fail(FanCommandErrorKind.RecoveryBlocked, failureReason, out errorKind, out failureReason);

        /*
         * **交给谁执行是用户选的。** 没选就默认固件 —— 固件那条路写一次就一直有效，
         * 本程序关了、重启了都还在，那是更好的默认；软件那条得我们一直跑着。
         *
         * 选了这台机器给不了的那一条，**如实说，不悄悄换成另一条**：
         * 悄悄换掉的话，用户以为曲线会一直有效，实际关掉程序就没了。
         */
        var wanted = execution ?? (described.FirmwareCurve is not null
            ? FanCurveExecutions.Firmware
            : FanCurveExecutions.Software);

        var group = ControlGroupOf(fanIndex);
        if (wanted == FanCurveExecutions.Firmware)
        {
            if (described.FirmwareCurve is null)
            {
                return Fail(FanCommandErrorKind.Unsupported, "这条通道没有固件曲线，只能由软件接管。",
                    out errorKind, out failureReason);
            }
            // 组在软件控制下时固件不执行任何一把的表：先整组回到固件控制。
            if (group is not null && IsSoftwareGroup(group))
                return GroupSwitchRequired(described, group, FanGroupControl.Firmware, "固件曲线", out errorKind, out failureReason);
            if (!EnsureSharedTakeoverFree(described, out errorKind, out failureReason)) return false;
            // 固件接手了，我们之前挂的软件曲线就该撤掉，否则两边抢同一把风扇。
            if (!StopSoftwarePlan(fanIndex, out errorKind, out failureReason)) return false;
            if (!active!.TryWriteCurve(fanIndex, curve, out errorKind, out failureReason))
            {
                if (active.HasPendingTemporaryRestore(fanIndex)) { lock (overridden) overridden.Add(fanIndex); }
                return false;
            }
            // 曲线写成了：这把不再是临时接管。记账失败不抛，如实报 StateUnavailable，曲线照样生效。
            return TryForgetTakeover(fanIndex, "的固件曲线已写入并生效", out errorKind, out failureReason);
        }

        if (!described.SupportsSoftwareCurve)
        {
            return Fail(FanCommandErrorKind.Unsupported, "这条通道给不了固定转速，软件曲线跑不起来。",
                out errorKind, out failureReason);
        }
        // 和定速一样按这把风扇收得下的范围核对，超出的点在记录前拒绝，不留到运行时被硬件拒绝。
        if (!TryCheckSoftwareCurve(described, curve, out errorKind, out failureReason)) return false;
        // 共用开关的风扇要整组切到软件控制，才能单独换各自的曲线。
        if (group is not null && !IsSoftwareGroup(group))
            return GroupSwitchRequired(described, group, FanGroupControl.Software, "软件曲线", out errorKind, out failureReason);

        /*
         * 软件曲线。温度源只有宿主推来的那些 —— 这个核心自己没有传感器，
         * 也不该去长一套：宿主本来就有完整的监控栈。
         *
         * 看哪些温度按**风扇角色**定：显卡风扇只看显卡，CPU 风扇只看 CPU，
         * 认不出角色的就看全部、取最热的那一个 ——
         * 共用散热的笔记本上，按最吃紧的那一路走才是对的（见 CurvePlan）。
         */
        // Only record here. The host-owned run prepares ownership before its first drive.
        return GetEngine().TryRecordPlan(CurvePlan(described, curve), out errorKind, out failureReason);
    }

    /// <summary>
    /// 这个风扇的曲线现在归谁执行。没有在跑的曲线就是 null。
    ///
    /// **以实际状态为准，不是以用户选了什么为准。** 软件曲线在引擎里跑着、没有暂停才算软件；
    /// 引擎因为温度断了或写不进而交还固件之后，这里就不再报 software 了 ——
    /// 界面要能看出"我选的那条现在还在不在跑"，暂停原因见 <see cref="SoftwareCurveSuspension"/>。
    ///
    /// 固件曲线同理：整机接管的通道上有风扇持有整机模式时，固件不执行任何一把的表，
    /// 这时也不报 firmware。
    /// </summary>
    public string? ExecutionOf(int fanIndex)
    {
        if (engine?.IsRunning(fanIndex) == true)
        {
            return FanCurveExecutions.Software;
        }
        if (SoftwarePlanRecorded(fanIndex)) return null;
        if (Describe().FirstOrDefault(fan => fan.Index == fanIndex) is not { FirmwareCurve: not null } described)
            return null;
        if (described.SoftwareTakeoverCoversAllFans && active!.SharedTakeoverHolders.Count > 0) return null;
        return FanCurveExecutions.Firmware;
    }

    public bool SoftwarePlanRecorded(int fanIndex) => engine?.HasPlan(fanIndex) == true;

    /// <summary>
    /// 这把风扇已记录的软件计划为什么没在执行：Blind（温度断了，回来后自动继续）、
    /// Failed（写不进，重新记录后继续）。没有暂停或没有计划就是 null。
    /// </summary>
    public FanCurveSuspension? SoftwareCurveSuspension(int fanIndex) => engine?.Suspension(fanIndex);

    /// <summary>All suspended recorded plans, keyed by fan index.</summary>
    public IReadOnlyDictionary<int, FanCurveSuspension> SoftwareCurveSuspensions
        => engine?.Suspensions.ToDictionary(entry => entry.FanIndex, entry => entry.Suspension)
            ?? new Dictionary<int, FanCurveSuspension>();

    /// <summary>
    /// 上次会话留下的临时接管没能恢复时的原因；此时所有写入都被拒绝（<see cref="FanCommandErrorKind.RecoveryBlocked"/>），
    /// 直到之后某次显式操作重试恢复成功。没有受阻就是 null。
    /// </summary>
    public string? RecoveryBlockedReason
    {
        get
        {
            lock (recoveryGate)
                return active is not null && !recoveryReady ? unavailableReason ?? "Fan takeover recovery has not completed." : null;
        }
    }

    public bool SoftwareCurveRunNeeded => engine?.SoftwareCurveRunNeeded == true;
    public event Action<bool>? SoftwareCurveRunNeededChanged
    {
        add => GetEngine().SoftwareCurveRunNeededChanged += value;
        remove { if (engine is not null) engine.SoftwareCurveRunNeededChanged -= value; }
    }
    public IReadOnlyList<string> SoftwareCurveTemperatureKinds => engine?.SoftwareCurveTemperatureKinds ?? [];
    public event Action<IReadOnlyList<string>>? SoftwareCurveTemperatureKindsChanged
    {
        add => GetEngine().SoftwareCurveTemperatureKindsChanged += value;
        remove { if (engine is not null) engine.SoftwareCurveTemperatureKindsChanged -= value; }
    }

    /// <summary>Run software plans for the host-controlled lifetime. Only one run may be active.</summary>
    public Task RunSoftwareCurveAsync(CancellationToken stop)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return GetEngine().RunAsync(stop);
    }

    private readonly object engineGate = new();
    private SoftwareCurveEngine? engine;
    private SoftwareCurveEngine GetEngine()
    {
        lock (engineGate)
            return engine ??= new SoftwareCurveEngine(() => active
                ?? throw new InvalidOperationException("No available fan backend."))
            {
                DiagnosticSink = detail => DiagnosticSink?.Invoke(detail),
                PrepareTakeover = fan => PrepareTemporaryTakeover(fan, out _, out var failure)
                    ? null : failure ?? "Temporary takeover preparation failed."
            };
    }
    /// <summary>Regression tests shorten engine timeouts through this; hosts use the public run entry.</summary>
    internal SoftwareCurveEngine CurveEngine => GetEngine();
    public Action<string>? DiagnosticSink { get; set; }
    private string? lifecycleOperation;
    public string? InFlightOperation => engine?.InFlightOperation ?? Volatile.Read(ref lifecycleOperation);

    /// <summary>
    /// 退出即恢复。
    ///
    /// **这条不可协商**：一个把风扇停在 20% 然后退出的程序，会把机器烤了。
    /// 所以凡是被我们固定过的风扇，走之前一律交还固件的自动模式；
    /// 交不回去也要继续试下一个，不能因为一个失败就把其余的丢在那儿。
    /// </summary>
    public void RestoreAll()
    {
        // 先把软件曲线停掉再交还：不停的话它下一轮又会写回一个手动值。所有控制组随之回到固件控制。
        lock (groupGate)
        {
            softwareGroups.Clear();
            // What is still ours is in overridden, released below.
            firmwareSwitchPending.Clear();
        }
        engine?.StopAll();
        if (active is null)
        {
            return;
        }
        int[] indexes;
        lock (overridden) indexes = overridden.ToArray();
        foreach (var fanIndex in indexes)
        {
            try
            {
                lifecycleOperation = $"shutdown release fan {fanIndex} ({active.Name})";
                if (active.TryReleaseOverride(fanIndex, out _))
                {
                    journal?.Released(active, fanIndex);
                    lock (overridden) overridden.Remove(fanIndex);
                }
            }
            catch (Exception)
            {
                // Keep the failed ownership for retry, and still release the other fans.
            }
            finally { lifecycleOperation = null; }
        }
    }

    /// <summary>
    /// Serializes recovery: the engine's takeover preparation (under its hardware lock) and host commands both call
    /// <see cref="EnsureRecovery"/>. Nothing inside takes an engine lock, so the order hardware lock → this lock is
    /// the only one.
    /// </summary>
    private readonly object recoveryGate = new();

    private bool EnsureRecovery(out string? failure)
    {
        lock (recoveryGate) return EnsureRecoveryLocked(out failure);
    }

    private bool EnsureRecoveryLocked(out string? failure)
    {
        failure = null;
        if (recoveryReady || active is null) return true;
        if (journal is not null && !journal.TryRecover(active, out failure))
        {
            unavailableReason = failure;
            return false;
        }
        // Existing Uniwill state also carries incomplete/temporary table ownership across exits.
        // Completed persistent curves do not report pending temporary restoration.
        foreach (var fan in active.Describe())
        {
            if (!active.HasPendingTemporaryRestore(fan.Index)) continue;
            if (!active.TryReleaseOverride(fan.Index, out failure))
            {
                unavailableReason = failure ?? "Fan temporary baseline recovery was not confirmed.";
                return false;
            }
        }
        recoveryReady = true;
        unavailableReason = null;
        return true;
    }

    private bool PrepareTemporaryTakeover(int fanIndex, out FanCommandErrorKind errorKind, out string? failure)
    {
        if (!EnsureRecovery(out failure))
            return Fail(FanCommandErrorKind.RecoveryBlocked, failure, out errorKind, out failure);
        var fan = active!.Describe().FirstOrDefault(fan => fan.Index == fanIndex);
        if (fan is null) return Fail(FanCommandErrorKind.NoSuchFan, "没有这个风扇。", out errorKind, out failure);
        errorKind = FanCommandErrorKind.Other;
        if (journal is null) return true; // Legacy hosts without durable state keep their existing ownership behavior.
        var affected = fan.SoftwareTakeoverCoversAllFans
            ? active.Describe().Where(fan => fan.Writable).Select(fan => fan.Index).ToArray()
            : new[] { fanIndex };
        if (!journal.TryRecord(active, affected, out failure))
            return Fail(FanCommandErrorKind.StateUnavailable, failure, out errorKind, out failure);
        // Record in memory before the first attempt too: a failed native call may have partly written.
        lock (overridden) foreach (var index in affected) overridden.Add(index);
        failure = null;
        return true;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        engine?.Dispose();
        RestoreAll();
        try
        {
            foreach (var candidate in candidates)
            {
                lifecycleOperation = $"dispose backend {candidate.Name}";
                candidate.Dispose();
            }
        }
        finally { lifecycleOperation = null; journal?.Dispose(); }
    }
}
