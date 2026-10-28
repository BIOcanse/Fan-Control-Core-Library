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
    {
        if (active is null)
        {
            failureReason = unavailableReason ?? "没有可用的风扇通道。";
            return false;
        }
        if (!PrepareTemporaryTakeover(fanIndex, out failureReason)) return false;
        if ((engine?.HasPlan(fanIndex) == true || engine?.HasRuntime(fanIndex) == true) && !engine.TryStop(fanIndex, out failureReason)) return false;
        if (!active.TrySetDuty(fanIndex, percent, out failureReason))
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
    {
        if (active is null)
        {
            failureReason = unavailableReason ?? "没有可用的风扇通道。";
            return false;
        }
        if (!PrepareTemporaryTakeover(fanIndex, out failureReason)) return false;
        if ((engine?.HasPlan(fanIndex) == true || engine?.HasRuntime(fanIndex) == true) && !engine.TryStop(fanIndex, out failureReason)) return false;
        if (!active.TrySetFullSpeed(fanIndex, out failureReason))
        {
            return false;
        }
        lock (overridden) overridden.Add(fanIndex);
        return true;
    }

    public bool TrySetAutomatic(int fanIndex, out string? failureReason, bool resetFirmwareCurve = false)
    {
        if (active is null)
        {
            failureReason = unavailableReason ?? "没有可用的风扇通道。";
            return false;
        }
        if (!EnsureRecovery(out failureReason)) return false;
        if ((engine?.HasPlan(fanIndex) == true || engine?.HasRuntime(fanIndex) == true) && !engine.TryStop(fanIndex, out failureReason)) return false;
        if (!(resetFirmwareCurve ? active.TryResetFirmwareCurve(fanIndex, out failureReason)
            : active.TrySetAutomatic(fanIndex, out failureReason)))
        {
            return false;
        }
        journal?.Released(active, fanIndex);
        lock (overridden) overridden.Remove(fanIndex);
        return true;
    }

    /// <summary>宿主推过来的温度。软件曲线要用，固件曲线用不上。</summary>
    public FanTemperatureStore Temperatures { get; } = new();

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
    {
        if (active is null)
        {
            failureReason = unavailableReason ?? "没有可用的风扇通道。";
            return false;
        }

        if (!EnsureRecovery(out failureReason)) return false;
        var described = Describe().FirstOrDefault(fan => fan.Index == fanIndex);
        if (described is null)
        {
            failureReason = "没有这个风扇。";
            return false;
        }

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

        if (wanted == FanCurveExecutions.Firmware)
        {
            if (described.FirmwareCurve is null)
            {
                failureReason = "这条通道没有固件曲线，只能由软件接管。";
                return false;
            }
            // 固件接手了，我们之前挂的软件曲线就该撤掉，否则两边抢同一把风扇。
            if ((engine?.HasPlan(fanIndex) == true || engine?.HasRuntime(fanIndex) == true) && !engine.TryStop(fanIndex, out failureReason)) return false;
            if (!active.TryWriteCurve(fanIndex, curve, out failureReason))
            {
                if (active.HasPendingTemporaryRestore(fanIndex)) { lock (overridden) overridden.Add(fanIndex); }
                return false;
            }
            journal?.Released(active, fanIndex);
            lock (overridden) overridden.Remove(fanIndex);
            return true;
        }

        if (!described.SupportsSoftwareCurve)
        {
            failureReason = "这条通道给不了固定转速，软件曲线跑不起来。";
            return false;
        }

        /*
         * 软件曲线。温度源只有宿主推来的那些 —— 这个核心自己没有传感器，
         * 也不该去长一套：宿主本来就有完整的监控栈。
         *
         * 看哪些温度按**风扇角色**定：显卡风扇只看显卡，CPU 风扇只看 CPU，
         * 认不出角色的就看全部、取最热的那一个 ——
         * 共用散热的笔记本上，按最吃紧的那一路走才是对的。
         */
        var names = Temperatures.Names
            .Where(name => described.Role switch
            {
                FanRoles.Gpu => name.StartsWith("gpu", StringComparison.OrdinalIgnoreCase),
                FanRoles.Cpu => name.StartsWith("cpu", StringComparison.OrdinalIgnoreCase),
                _ => true
            })
            .ToArray();
        if (names.Length == 0 || (described.Role != FanRoles.Gpu
            && !names.Any(name => name.StartsWith("cpu", StringComparison.OrdinalIgnoreCase))))
        {
            // 宿主还没推过温度，或者推来的里面没有这把风扇该看的那一路。
            // **不拿别的温度顶上** —— 按错的温度调速比不调更糟。
            failureReason = "还没有收到这个风扇要看的温度，软件曲线跑不起来。";
            return false;
        }

        var plan = new SoftwareCurvePlan(
            fanIndex,
            curve,
            [.. names.Select(name => new FanTemperatureSource(
                name,
                () => Temperatures.Read(name),
                Required: described.Role != FanRoles.Gpu && name.StartsWith("cpu", StringComparison.OrdinalIgnoreCase)))]);
        if (!engine!.TryRecordPlan(plan, out failureReason))
        {
            return false;
        }
        // Only record here. The host-owned run prepares ownership before its first drive.
        return true;
    }

    /// <summary>
    /// 这个风扇的曲线现在归谁执行。没有在跑的曲线就是 null。
    ///
    /// **以实际状态为准，不是以用户选了什么为准。** 软件曲线在引擎里跑着才算软件；
    /// 引擎因为温度断了而交还固件之后，这里就不再报 software 了 ——
    /// 界面要能看出"我选的那条现在还在不在跑"。
    /// </summary>
    public string? ExecutionOf(int fanIndex)
    {
        if (engine?.IsRunning(fanIndex) == true)
        {
            return FanCurveExecutions.Software;
        }
        if (SoftwarePlanRecorded(fanIndex)) return null;
        return Describe().FirstOrDefault(fan => fan.Index == fanIndex)?.FirmwareCurve is not null
            ? FanCurveExecutions.Firmware
            : null;
    }

    public bool SoftwarePlanRecorded(int fanIndex) => engine?.HasPlan(fanIndex) == true;
    public bool SoftwareCurveRunNeeded => engine?.SoftwareCurveRunNeeded == true;
    public event Action<bool>? SoftwareCurveRunNeededChanged
    {
        add => GetEngine().SoftwareCurveRunNeededChanged += value;
        remove { if (engine is not null) engine.SoftwareCurveRunNeededChanged -= value; }
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
                PrepareTakeover = fan => PrepareTemporaryTakeover(fan, out var failure)
                    ? null : failure ?? "Temporary takeover preparation failed."
            };
    }
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
        // 先把软件曲线停掉再交还：不停的话它下一轮又会写回一个手动值。
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

    private bool EnsureRecovery(out string? failure)
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

    private bool PrepareTemporaryTakeover(int fanIndex, out string? failure)
    {
        if (!EnsureRecovery(out failure)) return false;
        var fan = active!.Describe().FirstOrDefault(fan => fan.Index == fanIndex);
        if (fan is null) { failure = "没有这个风扇。"; return false; }
        if (journal is null) return true; // Legacy hosts without durable state keep their existing ownership behavior.
        var affected = fan.SoftwareTakeoverCoversAllFans
            ? active.Describe().Where(fan => fan.Writable).Select(fan => fan.Index).ToArray()
            : new[] { fanIndex };
        if (journal is not null && !journal.TryRecord(active, affected, out failure)) return false;
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
