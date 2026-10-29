using System.Diagnostics;

namespace FanControlCore.Policy;

/// <summary>
/// 一个温度读数的来源。
///
/// **哪个风扇看哪些温度是可配的，不是写死的。** 一把风扇可能同时吹 CPU 和显卡，
/// 那它该看两者里更热的那个；纯显卡风扇只看显卡。写死成"CPU 风扇看 CPU"，
/// 在共用散热的笔记本上一定是错的。
/// </summary>
/// <param name="Name">给人看的名字，比如 cpu、gpu。</param>
/// <param name="ReadCelsius">读一次。读不到回 null —— 读不到和读到 0 度是两回事。</param>
public sealed record FanTemperatureSource(string Name, Func<double?> ReadCelsius, bool Required = false);

/// <summary>几个温度源怎么合成一个数。</summary>
public enum FanTemperatureAggregation
{
    /// <summary>取最热的那个。**默认就该是它**：散热要按最吃紧的那一路来。</summary>
    Maximum,
    Average,
    Minimum
}

/// <summary>
/// 软件曲线的一条设定：这把风扇看哪些温度、按哪条曲线、怎么合成。
/// </summary>
public sealed record SoftwareCurvePlan(
    int FanIndex,
    IReadOnlyList<FanCurvePoint> Curve,
    IReadOnlyList<FanTemperatureSource> Sources,
    FanTemperatureAggregation Aggregation = FanTemperatureAggregation.Maximum,
    /// <summary>
    /// 风扇加速度：转速每秒最多变多少个百分点。**决定灵敏度的就是它，只有它。**
    ///
    /// 调大就跟得紧（温度一起来转速立刻跟上，代价是听得出忽大忽小），
    /// 调小就平缓（安静，但温度冲上来时反应慢）。升和降用同一个值 ——
    /// 两个方向各一个参数的话，"灵敏度"就又变成两个旋钮说不清了。
    ///
    /// 默认 40%/秒：从停转到满速约两秒半，和风扇本身转起来的时间是一个量级。
    /// </summary>
    double PercentPerSecond = 40,
    /// <summary>
    /// 超过这个温度就无条件满速，不再看曲线。
    ///
    /// **这不是"替用户改曲线"，是最后一道保护。** 它在界面上是看得见、
    /// 可以调的一项；藏起来的兜底才是问题，明说出来的兜底不是。
    /// </summary>
    double CriticalCelsius = 95,
    /// <summary>The kind this plan reads, used by the host to subscribe temperatures.</summary>
    string TemperatureKind = FanTemperatureKinds.Any);

/// <summary>
/// 软件闭环曲线引擎。
///
/// **一份实现，所有只能设固定转速的通道共用。** 按文档的分类，
/// Acer / HP / Dell / ThinkPad / Alienware / Razer / hwmon 这一大片底层都只给
/// "设一个固定占空比"，曲线得有人在用户态跑 —— 那件事和品牌毫无关系，
/// 所以它在这里，不在各家后端里。各家后端只负责"把这个百分比写下去"。
///
/// 这个引擎要做的事，逐条都有理由：
///
/// <list type="bullet">
/// <item><b>加速度</b>：**唯一决定灵敏度的变量**。温度直接读、直接查曲线得到目标，
///   转速按这个速率朝目标走。调大就跟得紧，调小就平缓 —— 一个旋钮说完。</item>
/// <item><b>最小启动转速</b>：低于硬件死区就干脆停转，不写一个"转不起来的值"
///   让用户以为在转。</item>
/// <item><b>临界兜底</b>：超过临界温度无条件满速，不受加速度限制。</item>
/// <item><b>不重复下发</b>：目标没变就不写。每次写都要走一次固件往返 ——
///   除非这条通道声明了固件会夺回控制权，或者整机接管的另一把风扇刚交还过。</item>
/// <item><b>退出必恢复</b>：**这条不可协商。** 引擎停下来（正常停、异常停、
///   进程没了）都要把风扇交还固件自动控制，而不是留在最后那个手动值上。</item>
/// <item><b>瞎了或写不进就交还，但不删用户的计划</b>：温度断了超过时限、或者连续写失败，
///   只把风扇交还固件并标记暂停原因（<see cref="FanCurveSuspension"/>）；计划留着，
///   断温的那份在温度回来后自动继续，失败的那份在重新记录后继续。
///   从没驱动过的风扇不碰硬件。整机接管的通道上，这种安全交还结束整个接管：
///   其余在驱动的风扇一起交还、计划标 SharedHandBack，等没有断温或失败的计划后一起继续。</item>
/// <item><b>不悄悄停掉别人的定速</b>：整机接管会让另一把的临时定速表失效，后端拒绝时
///   （SharedTakeoverHeld）计划标同名暂停，重新记录后继续；整机接管的通道上这也结束整个接管，
///   其余在驱动的风扇一起交还，和断温、失败一样。</item>
/// <item><b>整机接管一起开始</b>：整机接管的通道上，所有在跑的风扇同一轮都能驱动才开始接管，
///   不让某一把在别人接管之后停在陈旧的占空比寄存器上。</item>
/// </list>
///
/// **锁只管状态，不管硬件。** 状态锁内只拍快照、算目标、提交结果；
/// 所有硬件和文件 I/O 都在锁外，由一把单独的硬件锁按次序串行。
/// 慢后端（EC 互斥等 5 秒、WMI 卡住）只拖住硬件操作，查询、记录计划和事实发布照常返回。
///
/// **不对温度做滤波。** 曾经照参考实现加过一层"13 个样本排序取中间 7 个再平均"，
/// 那是自作聪明：风扇本身就有机械惯性，转速不可能瞬间跟上，滤波等于在物理惯性
/// 之上再叠一层软件滞后 —— 温度真冲起来的时候，等滤波窗口跟上来已经晚了。
/// 而且滤波和加速度两个变量互相干扰，灵敏度到底由谁决定说不清楚。
/// 现在只有加速度一个变量，它就是灵敏度。
/// </summary>
public sealed class SoftwareCurveEngine : IDisposable
{
    /// <summary>
    /// 连续读不到温度多久就交还固件。
    ///
    /// 比温度存放处的时效（3 秒）再宽一点：那边过期只是"这一批不新鲜了"，
    /// 这里是"已经瞎了这么久，不能再按着风扇不放"。
    /// </summary>
    internal static readonly TimeSpan DefaultBlindTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Regression tests shorten the blind timeout; product code keeps the default.</summary>
    internal TimeSpan BlindTimeout { get; set; } = DefaultBlindTimeout;

    /// <summary>连续这么多次写不进（后端如实返回失败）就交还固件，并把计划标为 Failed。</summary>
    private const int FailureLimit = 5;

    /// <summary>
    /// 交还失败后的重试间隔上限。每失败一次间隔翻倍（从两个运行间隔起），封顶这么多 ——
    /// 交还往往要读基线文件、做好几次 EC 写，10 Hz 一直重试只是在持续打硬件。
    /// 显式的停止请求（命令、运行结束）不等退避，当场再试一次。
    /// </summary>
    private static readonly TimeSpan MaximumReleaseBackoff = TimeSpan.FromSeconds(5);

    private readonly Func<IFanBackend> resolveBackend;
    private IFanBackend Backend => resolveBackend();
    private readonly TimeSpan interval;
    private readonly TimeSpan reassertAfter;
    /// <summary>State lock. Never held while calling the backend, files or host callbacks other than temperature reads.</summary>
    private readonly object gate = new();
    /// <summary>Serializes this engine's hardware operations in order. Always taken before <see cref="gate"/>.</summary>
    private readonly object hardwareGate = new();
    private readonly Dictionary<int, SoftwareCurvePlan> plans = [];
    private readonly Dictionary<int, FanCurveSuspension> suspensions = [];
    private readonly Dictionary<int, FanRuntime> running = [];
    private readonly SemaphoreSlim changed = new(0, 1);
    private CancellationTokenSource? cancellation;
    private Task? loop;
    private bool disposed;
    internal Func<int, string?>? PrepareTakeover { get; set; }
    private readonly Dictionary<string, string> diagnostics = [];
    public IReadOnlyList<string> Diagnostics { get { lock (gate) return diagnostics.Values.ToArray(); } }
    public Action<string>? DiagnosticSink { get; set; }
    private string? inFlightOperation;
    public string? InFlightOperation => Volatile.Read(ref inFlightOperation);
    internal Action? BeforeIteration { get; set; }
    private bool runNeeded;
    private bool publishingFacts;
    private readonly Queue<(bool? RunNeeded, IReadOnlyList<string>? Kinds)> factChanges = new();
    private IReadOnlyList<string> temperatureKinds = Array.Empty<string>();
    public bool SoftwareCurveRunNeeded { get { lock (gate) return runNeeded; } }
    public event Action<bool>? SoftwareCurveRunNeededChanged;
    public IReadOnlyList<string> SoftwareCurveTemperatureKinds { get { lock (gate) return temperatureKinds; } }
    public event Action<IReadOnlyList<string>>? SoftwareCurveTemperatureKindsChanged;

    /// <summary>
    /// Plans a run can act on: every recorded plan except those waiting for a command (<see cref="FanCurveSuspension.Failed"/>
    /// waits for a new recording, <see cref="FanCurveSuspension.SharedTakeoverHeld"/> for the other fan's fixed duty to end).
    /// Blind and SharedHandBack plans stay active: they resume on their own from temperature facts.
    /// </summary>
    private IEnumerable<SoftwareCurvePlan> ActivePlans
        => plans.Values.Where(plan => !(suspensions.TryGetValue(plan.FanIndex, out var reason)
            && reason is FanCurveSuspension.Failed or FanCurveSuspension.SharedTakeoverHeld));

    /// <summary>
    /// Under gate, after <paramref name="cause"/> was suspended Blind, Failed or SharedTakeoverHeld. On a channel whose takeover covers all
    /// fans, handing back only the cause would leave it outside firmware control while another driven fan keeps the
    /// shared takeover; so every other driven fan is handed back too and its plan suspended as SharedHandBack.
    /// Returns the runtimes that now need a release.
    /// </summary>
    private List<FanRuntime> EndSharedTakeover(int cause, bool coversAll)
    {
        var handedBack = new List<FanRuntime>();
        if (!coversAll) return handedBack;
        foreach (var (index, other) in running)
        {
            if (index == cause || other.Restoring) continue;
            other.Restoring = true;
            handedBack.Add(other);
            if (plans.ContainsKey(index)) suspensions[index] = FanCurveSuspension.SharedHandBack;
            Report($"shared-handback:{index}",
                $"Fan {index} handed back to firmware together with fan {cause}: this channel's takeover covers all fans, so a safety hand-back ends it for every fan.");
        }
        return handedBack;
    }


    /// <summary>
    /// Called under gate after plan/runtime changes. Queue every transition in mutation order.
    /// A run is needed for active plans (including blind-suspended ones, which resume on a fresh temperature)
    /// and for pending hand-backs; the kinds are those active plans read. A failed plan needs neither until it is
    /// recorded again.
    /// </summary>
    private void UpdateFacts()
    {
        var active = ActivePlans.ToArray();
        var needed = active.Length > 0 || running.Count > 0;
        string[] orderedKinds = [FanTemperatureKinds.Cpu, FanTemperatureKinds.Gpu, FanTemperatureKinds.Any];
        var kinds = orderedKinds.Where(kind => active.Any(plan => plan.TemperatureKind == kind)).ToArray();
        var kindsChanged = !temperatureKinds.SequenceEqual(kinds);
        var neededChanged = needed != runNeeded;
        if (!neededChanged && !kindsChanged) return;
        runNeeded = needed;
        if (kindsChanged) temperatureKinds = Array.AsReadOnly(kinds);
        factChanges.Enqueue((neededChanged ? needed : null, kindsChanged ? temperatureKinds : null));
    }

    // Call only after leaving gate. One caller drains; concurrent/reentrant changes queue behind it.
    private void PublishFactChanges()
    {
        lock (gate)
        {
            if (publishingFacts || factChanges.Count == 0) return;
            publishingFacts = true;
        }
        while (true)
        {
            (bool? RunNeeded, IReadOnlyList<string>? Kinds) change;
            lock (gate)
            {
                if (!factChanges.TryDequeue(out change))
                {
                    publishingFacts = false;
                    return;
                }
            }
            if (change.RunNeeded is { } needed)
                Publish(SoftwareCurveRunNeededChanged, needed, "run-needed");
            if (change.Kinds is { } kinds)
                Publish(SoftwareCurveTemperatureKindsChanged, kinds, "temperature-kinds");
        }
    }

    private void Publish<T>(Action<T>? observers, T value, string fact)
    {
        if (observers is null) return;
        foreach (Action<T> observer in observers.GetInvocationList())
        {
            try { observer(value); }
            catch (Exception error)
            {
                // Host observers cannot interrupt hand-back or leave the notification queue stuck.
                lock (gate) Report($"{fact}-observer", $"Software curve {fact} callback failed: {error.Message}");
            }
        }
    }

    // Called under gate.
    private void Report(string key, string detail)
    {
        if (diagnostics.TryGetValue(key, out var previous) && previous == detail) return;
        diagnostics[key] = detail;
        if (DiagnosticSink is not { } observer) return;
        // Logging is not a hardware owner. A blocked host observer must not delay release.
        _ = Task.Run(() =>
        {
            try { observer(detail); }
            catch (Exception error)
            {
                lock (gate) diagnostics["observer"] = $"Fan diagnostic callback failed: {error.Message}";
            }
        });
    }

    public SoftwareCurveEngine(
        IFanBackend backend,
        TimeSpan? interval = null,
        TimeSpan? reassertAfter = null)
        : this(() => backend, interval, reassertAfter)
    {
        ArgumentNullException.ThrowIfNull(backend);
    }

    // FanCore may run while discovery is unavailable. No plan can be recorded until Open succeeds.
    internal SoftwareCurveEngine(
        Func<IFanBackend> resolveBackend,
        TimeSpan? interval = null,
        TimeSpan? reassertAfter = null)
    {
        this.resolveBackend = resolveBackend;
        /*
         * 0.1 秒一轮。
         *
         * **间隔必须小。** 温度可以在一秒内冲上去十几度。
         * 0.1 秒一轮才能及时看到宿主推来的最新读数，再按曲线目标和加速度调速。
         * 引擎不对温度做滤波；放大轮询间隔会增加读数等待，拖慢对温度上冲的响应。
         *
         * 这一轮本身很便宜：读温度是读宿主推来的最新值（不碰硬件），
         * 目标没变就不写。真正贵的只有"值变了要写下去"那一下。
         */
        this.interval = interval ?? TimeSpan.FromMilliseconds(100);
        // 固件会夺回控制权的通道，即使目标没变也要按期重下发。
        // 90 秒是 HP 那条链路上内核用的值：固件约 120 秒回退，留足余量。
        this.reassertAfter = reassertAfter ?? TimeSpan.FromSeconds(90);
    }

    /// <summary>
    /// 这个风扇现在有没有软件曲线在跑：运行入口在执行它的计划，计划没有暂停，也不在交还途中。
    /// </summary>
    public bool IsRunning(int fanIndex)
    {
        lock (gate)
        {
            return loop is { IsCompleted: false } && plans.ContainsKey(fanIndex)
                && !suspensions.ContainsKey(fanIndex)
                && (!running.TryGetValue(fanIndex, out var fan) || !fan.Restoring);
        }
    }

    public bool HasPlan(int fanIndex)
    {
        lock (gate) return plans.ContainsKey(fanIndex);
    }

    /// <summary>Why this fan's recorded plan is not being executed; null when it is not suspended or not recorded.</summary>
    public FanCurveSuspension? Suspension(int fanIndex)
    {
        lock (gate) return plans.ContainsKey(fanIndex) && suspensions.TryGetValue(fanIndex, out var reason) ? reason : null;
    }

    /// <summary>Every suspended recorded plan, by fan index in ascending order.</summary>
    public IReadOnlyList<(int FanIndex, FanCurveSuspension Suspension)> Suspensions
    {
        get
        {
            lock (gate)
                return suspensions.Where(pair => plans.ContainsKey(pair.Key)).OrderBy(pair => pair.Key)
                    .Select(pair => (pair.Key, pair.Value)).ToArray();
        }
    }

    internal bool HasRuntime(int fanIndex)
    {
        lock (gate) return running.ContainsKey(fanIndex);
    }

    public IReadOnlyList<FanCurvePoint>? RecordedCurve(int fanIndex)
    {
        lock (gate) return plans.TryGetValue(fanIndex, out var plan) ? plan.Curve.ToArray() : null;
    }

    /// <summary>
    /// 记录这把风扇的软件曲线，已经有的就换掉；不启动执行。记录也清掉这把风扇的暂停原因。
    ///
    /// 换曲线**不重置当前转速**：风扇此刻转到哪儿是物理事实，和换不换曲线无关。
    /// 重置的话，换完那一下会从头按加速度爬，白白慢一截。
    /// </summary>
    public bool TryRecordPlan(SoftwareCurvePlan plan, out string? failureReason)
        => TryRecordPlan(plan, out _, out failureReason);

    internal bool TryRecordPlan(SoftwareCurvePlan plan, out FanCommandErrorKind errorKind, out string? failureReason)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return TryRecordPlans([plan], out errorKind, out failureReason);
    }

    /// <summary>
    /// Record or replace several plans as one change (a control group switching to software): either every plan is
    /// recorded, or none is and nothing changes. Recording clears each fan's suspension.
    /// </summary>
    internal bool TryRecordPlans(IReadOnlyList<SoftwareCurvePlan> batch, out FanCommandErrorKind errorKind, out string? failureReason)
    {
        errorKind = FanCommandErrorKind.InvalidRequest;
        foreach (var plan in batch)
        {
            if (plan.Curve.Count == 0)
            {
                failureReason = "曲线上一个点都没有。";
                return false;
            }
            // Every target is written as a duty: a point outside 0 to 100 % would be refused by the channel at run time.
            if (plan.Curve.Any(point => point is null || !double.IsFinite(point.TemperatureCelsius) || !double.IsFinite(point.Percent)
                || point.Percent is < 0 or > 100))
            {
                failureReason = $"风扇 {plan.FanIndex} 的曲线点要是有限值，占空比在 0 到 100 之间。";
                return false;
            }
            if (plan.Sources.Count == 0)
            {
                failureReason = "没有指定看哪个温度。";
                return false;
            }
        }

        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (batch.FirstOrDefault(plan => running.TryGetValue(plan.FanIndex, out var existing) && existing.Restoring) is { } pending)
            {
                errorKind = FanCommandErrorKind.HandBackPending;
                failureReason = $"风扇 {pending.FanIndex} 仍在交还固件，请等待恢复完成。";
                return false;
            }
            foreach (var plan in batch)
            {
                plans[plan.FanIndex] = plan with { Curve = plan.Curve.ToArray(), Sources = plan.Sources.ToArray() };
                suspensions.Remove(plan.FanIndex);
                if (running.TryGetValue(plan.FanIndex, out var existing)) existing.Plan = plans[plan.FanIndex];
            }
            if (changed.CurrentCount == 0) changed.Release();
            UpdateFacts();
        }
        PublishFactChanges();
        errorKind = FanCommandErrorKind.Other;
        failureReason = null;
        return true;
    }

    /// <summary>
    /// 撤掉这把风扇的软件曲线，并**把它交还固件**。
    ///
    /// 交还是这件事的一部分，不是可选的后续 —— 只停循环不交还，
    /// 风扇会永远停在最后算出来的那个值上。从没接管过的风扇不碰硬件。
    /// </summary>
    public bool TryStop(int fanIndex, out string? failureReason) => TryStop(fanIndex, out _, out failureReason);

    internal bool TryStop(int fanIndex, out FanCommandErrorKind errorKind, out string? failureReason)
    {
        FanRuntime? runtime;
        lock (gate)
        {
            plans.Remove(fanIndex);
            suspensions.Remove(fanIndex);
            if (running.TryGetValue(fanIndex, out runtime)) runtime.Restoring = true;
            UpdateFacts();
        }
        PublishFactChanges();
        errorKind = FanCommandErrorKind.Other;
        failureReason = null;
        if (runtime is null) return true;
        var released = Release(fanIndex, runtime, out errorKind, out failureReason);
        PublishFactChanges();
        return released;
    }

    /// <summary>
    /// Hand one restoring runtime back. Holds the hardware lock for the whole attempt so it is ordered after any
    /// in-flight drive of the same fan. A runtime that never prepared a takeover is dropped without hardware access.
    /// Returns true when nothing remains to release.
    /// </summary>
    private bool Release(int fanIndex, FanRuntime runtime, out FanCommandErrorKind errorKind, out string? failureReason)
    {
        errorKind = FanCommandErrorKind.Other;
        failureReason = null;
        lock (hardwareGate)
        {
            lock (gate)
            {
                if (!running.TryGetValue(fanIndex, out var current) || current != runtime || !runtime.Restoring)
                    return true;
                if (!runtime.TakeoverPrepared)
                {
                    running.Remove(fanIndex);
                    UpdateFacts();
                    return true;
                }
            }
            bool released;
            Volatile.Write(ref inFlightOperation, $"curve release fan {fanIndex}");
            try { released = Backend.TryReleaseOverride(fanIndex, out errorKind, out failureReason); }
            catch (Exception error)
            {
                released = false;
                errorKind = FanCommandErrorKind.Other;
                failureReason = error.Message;
            }
            finally { Volatile.Write(ref inFlightOperation, null); }
            /*
             * A shared takeover with holder counting may have changed mode: the other fans it covers are rewritten on
             * their next tick. Not on covers-all channels without holder counting (Acer): their release puts the whole
             * machine back to automatic, and an immediate rewrite would take the just-released fan over again.
             */
            var coversAll = released && Backend.SharedTakeoverCountsHolders
                && DescriptionOf(Backend.Describe(), fanIndex)?.SoftwareTakeoverCoversAllFans == true;
            lock (gate)
            {
                if (released)
                {
                    if (running.TryGetValue(fanIndex, out var current) && current == runtime) running.Remove(fanIndex);
                    if (coversAll)
                        foreach (var other in running.Values)
                            if (!other.Restoring && other.TakeoverPrepared) other.ForceWrite = true;
                    UpdateFacts();
                }
                else
                {
                    runtime.ReleaseFailures++;
                    runtime.NextReleaseAttempt = Stopwatch.GetTimestamp() + ReleaseBackoff(runtime.ReleaseFailures);
                    Report($"release:{fanIndex}", $"Fan {fanIndex} release failed; restoration pending: {failureReason ?? "not confirmed"}");
                }
            }
            return released;
        }
    }

    /// <summary>Two run intervals after the first failure, doubling, capped at <see cref="MaximumReleaseBackoff"/>; in Stopwatch ticks.</summary>
    private long ReleaseBackoff(int failures)
    {
        var delay = TimeSpan.FromTicks(Math.Min(MaximumReleaseBackoff.Ticks,
            interval.Ticks * (1L << Math.Min(failures, 20))));
        return (long)(delay.TotalSeconds * Stopwatch.Frequency);
    }

    /// <summary>
    /// 全部撤掉并交还固件。
    ///
    /// **一个失败也要继续走完剩下的。** 因为一把风扇交不回去就把其余的
    /// 丢在手动值上，是把小问题变成大问题。
    /// </summary>
    public void StopAll()
    {
        int[] indexes;
        lock (gate)
        {
            indexes = [.. plans.Keys.Union(running.Keys).Order()];
        }
        TryStopFans(indexes, out _, out _);
    }

    /// <summary>
    /// Stop several fans as one change (a control group returning to firmware, or every fan at shutdown). One state-lock
    /// acquisition removes every plan and marks every runtime as being handed back, so no run tick can drive any of them
    /// again; only then are they released, in the given order, each ordered after any drive already in flight. A failed
    /// release stays pending and is retried by the run with backoff. Returns the first failure; the others are still attempted.
    /// </summary>
    internal bool TryStopFans(IReadOnlyList<int> fanIndexes, out FanCommandErrorKind errorKind, out string? failureReason)
    {
        var runtimes = new List<(int Index, FanRuntime Runtime)>();
        lock (gate)
        {
            foreach (var index in fanIndexes)
            {
                plans.Remove(index);
                suspensions.Remove(index);
                if (running.TryGetValue(index, out var runtime))
                {
                    runtime.Restoring = true;
                    runtimes.Add((index, runtime));
                }
            }
            UpdateFacts();
        }
        PublishFactChanges();
        AfterBatchStopMarked?.Invoke();
        errorKind = FanCommandErrorKind.Other;
        failureReason = null;
        var released = true;
        foreach (var (index, runtime) in runtimes)
        {
            if (Release(index, runtime, out var kind, out var reason)) continue;
            if (released) (errorKind, failureReason) = (kind, reason);
            released = false;
        }
        PublishFactChanges();
        return released;
    }

    /// <summary>Regression tests: runs after a batch stop marked its fans and before it releases any of them.</summary>
    internal Action? AfterBatchStopMarked { get; set; }

    /// <summary>Execute recorded plans until the host cancels. Starts no background task.</summary>
    public Task RunAsync(CancellationToken stop)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (loop is { IsCompleted: false })
                throw new InvalidOperationException("A software curve run is already active.");
            cancellation?.Dispose();
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(stop);
            loop = RunLoopAsync(cancellation.Token);
            return loop;
        }
    }

    /// <summary>Under gate: does a run have anything to do? Failed plans wait, without ticking, until recorded again.</summary>
    private bool HasWork() => running.Count > 0 || ActivePlans.Any();

    private async Task RunLoopAsync(CancellationToken stop)
    {
        // Assign the task before it can complete or access hardware.
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            while (true)
            {
                bool idle;
                lock (gate) idle = !HasWork();
                if (idle)
                {
                    await changed.WaitAsync(stop).ConfigureAwait(false);
                    continue;
                }
                using var timer = new PeriodicTimer(interval);
                while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false))
                {
                    Iterate(stop);
                    PublishFactChanges();
                    bool idleAfterTick;
                    lock (gate) idleAfterTick = !HasWork();
                    if (idleAfterTick) break;
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error)
        {
            lock (gate) Report("loop", $"Software curve loop failed; all fans are being handed back to firmware: {error.Message}");
            throw;
        }
        finally
        {
            // Retain plans and suspensions; a later run starts from its target after firmware hand-back.
            FanRuntime[] held;
            lock (gate)
            {
                held = running.Values.ToArray();
                foreach (var fan in held) fan.Restoring = true;
            }
            foreach (var fan in held) Release(fan.Plan.FanIndex, fan, out _, out _);
            PublishFactChanges();
        }
    }

    /// <summary>One tick: decide every fan under the state lock, then perform the hardware work outside it.</summary>
    private void Iterate(CancellationToken stop)
    {
        var descriptions = Backend.Describe();
        var drives = new List<(FanRuntime Runtime, int Percent)>();
        var releases = new List<FanRuntime>();
        lock (gate)
        {
            stop.ThrowIfCancellationRequested();
            BeforeIteration?.Invoke();
            var now = Stopwatch.GetTimestamp();
            bool CoversAll(int fanIndex) => DescriptionOf(descriptions, fanIndex)?.SoftwareTakeoverCoversAllFans == true;
            /*
             * A covers-all channel is one group: every fan of it with a recorded plan. While no member holds the takeover,
             * the group starts as a whole or not at all, decided here for all members before any suspension is cleared.
             * It is ready only when no member is being handed back (a pending release keeps that fan's hardware in our
             * hands) and every suspended member can resume now: Blind and SharedHandBack need a fresh temperature,
             * Failed and SharedTakeoverHeld wait for a new recording. A partial restart would set the shared switch and take
             * a not-ready member away from firmware with no runtime driving it or watching its temperature.
             */
            var takeoverHeld = running.Values.Any(fan => CoversAll(fan.Plan.FanIndex) && !fan.Restoring && fan.TakeoverPrepared);
            var groupPlans = plans.Values.Where(plan => CoversAll(plan.FanIndex)).ToArray();
            var groupReady = !running.Values.Any(fan => CoversAll(fan.Plan.FanIndex) && fan.Restoring)
                && groupPlans.All(plan => !suspensions.TryGetValue(plan.FanIndex, out var reason)
                    || reason is FanCurveSuspension.Blind or FanCurveSuspension.SharedHandBack && ReadTemperature(plan) is not null);
            foreach (var plan in plans.Values)
            {
                if (running.ContainsKey(plan.FanIndex)) continue;
                if (CoversAll(plan.FanIndex) && !takeoverHeld)
                {
                    if (!groupReady)
                    {
                        // Waits, visibly, for the rest of its group; keeps its own reason if it has one.
                        suspensions.TryAdd(plan.FanIndex, FanCurveSuspension.SharedHandBack);
                        continue;
                    }
                    suspensions.Remove(plan.FanIndex);
                }
                else if (suspensions.TryGetValue(plan.FanIndex, out var reason))
                {
                    // Failed waits for a new recording, SharedTakeoverHeld for re-recording; Blind for its temperature.
                    if (reason is FanCurveSuspension.Failed or FanCurveSuspension.SharedTakeoverHeld
                        || ReadTemperature(plan) is null) continue;
                    suspensions.Remove(plan.FanIndex);
                }
                running[plan.FanIndex] = new FanRuntime(plan);
            }
            foreach (var fan in running.Values)
            {
                if (fan.Restoring) continue;
                var description = DescriptionOf(descriptions, fan.Plan.FanIndex);
                fan.CoversAll = description?.SoftwareTakeoverCoversAllFans == true;
                if (Decide(fan, description, now) is { } wanted) drives.Add((fan, wanted));
            }
            // A blind decision may have ended the whole shared takeover: release first, and never drive a fan being released.
            foreach (var fan in running.Values)
                if (fan.Restoring && now >= fan.NextReleaseAttempt) releases.Add(fan);
            drives.RemoveAll(drive => drive.Runtime.Restoring);
            /*
             * A covers-all takeover starts for all its fans in one tick or not at all. The first drive takes every fan of
             * the channel away from firmware; a member that cannot be driven yet (no fresh temperature, suspended, or
             * still being handed back) would sit on a stale duty register meanwhile, with nothing watching it. So the
             * first drive needs every member of the group driven in this tick and no member being handed back. Once the
             * takeover runs, a member that loses its temperature goes blind and hands the whole takeover back.
             */
            if (!takeoverHeld && groupPlans.Length > 0
                && (running.Values.Any(fan => CoversAll(fan.Plan.FanIndex) && fan.Restoring)
                    || groupPlans.Any(plan => !drives.Any(drive => drive.Runtime.Plan.FanIndex == plan.FanIndex))))
                drives.RemoveAll(drive => CoversAll(drive.Runtime.Plan.FanIndex));
            UpdateFacts();
        }
        foreach (var fan in releases) Release(fan.Plan.FanIndex, fan, out _, out _);
        foreach (var (fan, percent) in drives) Drive(fan, percent);
    }

    /// <summary>
    /// Under gate. The duty this fan should be written now, or null when nothing is to be written.
    /// A blind timeout suspends the plan and marks the runtime for hand-back.
    /// </summary>
    private int? Decide(FanRuntime fan, FanDescription? description, long now)
    {
        var plan = fan.Plan;
        if (ReadTemperature(plan) is not { } temperature)
        {
            /*
             * 读不到温度。短暂读不到就保持上一次的转速等下一轮 ——
             * 那可能只是一次采样没赶上。
             *
             * **但不能一直这么等下去。** 温度源是宿主推过来的，推送停了
             * （宿主崩了、订阅掉了、独显睡了）这里就会一直读不到，而我们还按着风扇。
             * 连续读不到超过时限就**交还固件** —— 软件曲线最危险的失效方式
             * 就是抱着一个再也不更新的温度继续调速。
             *
             * 交还不等于删掉用户的计划：标记 Blind，计划和它要的温度种类都留着，
             * 宿主照常推温度，温度回来就接着跑。
             *
             * **整机接管的通道上，交还一把就要交还全部。** 另一把还持有整机接管时，固件不执行任何一把的表，
             * 只交还这一把等于把它留在最后那个软件值上、没人看着 —— 正是这条时限要防的事。
             * 所以其余在驱动的风扇一起交还，计划标 SharedHandBack，等这把恢复后一起接着跑。
             */
            fan.BlindSince ??= now;
            if (Stopwatch.GetElapsedTime(fan.BlindSince.Value, now) >= BlindTimeout)
            {
                suspensions[plan.FanIndex] = FanCurveSuspension.Blind;
                fan.Restoring = true;
                Report($"suspend:{plan.FanIndex}", $"Fan {plan.FanIndex} temperature unavailable for {BlindTimeout.TotalSeconds:0.#} s; handed back to firmware, plan kept until it returns.");
                EndSharedTakeover(plan.FanIndex, description?.SoftwareTakeoverCoversAllFans == true);
            }
            return null;
        }
        fan.BlindSince = null;

        int wanted;
        if (temperature >= plan.CriticalCelsius)
        {
            /*
             * 临界兜底：**直接满速，不受加速度限制**。
             *
             * 这一条是保护，不是调速 —— 都到临界温度了还按斜率慢慢爬上去，
             * 那这条兜底就没有意义了。
             */
            wanted = 100;
        }
        else
        {
            // 温度直接查曲线，中间不做任何平滑 —— 见类上那段。
            var target = PercentAt(plan.Curve, temperature);

            /*
             * 按加速度朝目标走。**这是唯一决定灵敏度的地方。**
             *
             * 还没写过就直接落到目标：开头那一下没有"上一次"可言，
             * 硬要从 0 爬上去的话，刚接管的几秒风扇是偏慢的。
             */
            wanted = fan.Applied is not { } last
                ? (int)Math.Round(target)
                : (int)Math.Round(Approach(last, target, plan.PercentPerSecond * interval.TotalSeconds));

            var minimumOn = description?.MinimumOnPercent ?? 0;
            // A channel that cannot write 0 (CanStop false) holds the floor instead of stopping.
            var targetSpins = target * 2 >= minimumOn || description?.CanStop == false;
            if (minimumOn > 0 && wanted < minimumOn && (wanted > 0 || targetSpins))
            {
                /*
                 * 落进硬件死区了。**要么给到转得起来的最低档，要么干脆停。**
                 * 写一个转不起来的值，界面显示"已应用 5%"而风扇是停的，那是骗人。
                 *
                 * **按曲线目标判，不按爬坡走到的位置判。** 目标落在最低档的一半以上，就是"该转"：
                 * 从停转起步直接到最低档，再从那里按加速度往上爬；往下走到最低档就停在那儿。
                 * 目标在一半以下才停。按爬坡位置判的话，40%/秒、0.1 秒一轮从 0 起步，
                 * 第一步只有 4%，取整回 0，下一轮又是 4% —— 风扇永远起不来，直到临界温度。
                 */
                wanted = targetSpins ? minimumOn : 0;
            }
        }

        var due = description?.RequiresPeriodicReassert == true
            && Stopwatch.GetElapsedTime(fan.LastWrite, now) >= reassertAfter;
        if (fan.Applied == wanted && !due && !fan.ForceWrite)
        {
            // 目标没变就不写：每次写都是一次固件往返，白写没有任何好处。
            fan.Failures = 0;
            return null;
        }
        return wanted;
    }

    /// <summary>Write one duty, outside the state lock. 用执行器那条路，不是"设固定转速"那条 —— 见 IFanBackend.TryDriveDuty。</summary>
    private void Drive(FanRuntime fan, int wanted)
    {
        var index = fan.Plan.FanIndex;
        lock (hardwareGate)
        {
            bool prepare;
            lock (gate)
            {
                // A stop or replacement may have happened since the decision; never drive a fan being handed back.
                if (!running.TryGetValue(index, out var current) || current != fan || fan.Restoring) return;
                prepare = !fan.TakeoverPrepared;
            }
            var prepared = !prepare;
            string? failure = null;
            var driven = false;
            var refusal = FanCommandErrorKind.Other;
            string? refusalReason = null;
            Volatile.Write(ref inFlightOperation, $"curve tick fan {index}");
            try
            {
                if (prepare)
                {
                    failure = PrepareTakeover?.Invoke(index);
                    prepared = failure is null;
                }
                if (prepared) driven = Backend.TryDriveDuty(index, wanted, out refusal, out refusalReason);
            }
            catch (Exception error) { failure = error.Message; }
            finally { Volatile.Write(ref inFlightOperation, null); }

            var handBack = false;
            var others = new List<FanRuntime>();
            lock (gate)
            {
                // Record preparation even if a stop arrived meanwhile: its release must then touch the hardware.
                if (prepared) fan.TakeoverPrepared = true;
                if (failure is null && driven)
                {
                    fan.Applied = wanted;
                    fan.LastWrite = Stopwatch.GetTimestamp();
                    fan.Failures = 0;
                    fan.ForceWrite = false;
                }
                else if (failure is null && refusal == FanCommandErrorKind.SharedTakeoverHeld)
                {
                    /*
                     * Refused before any write: another fan runs a temporary fixed duty that this takeover would
                     * silently stop. Not a failed write; the plan waits, visibly, for that fixed duty to end.
                     */
                    Report($"held:{index}", $"Fan {index} software curve waits: {refusalReason ?? "another fan's fixed duty holds this channel"}");
                    if (plans.ContainsKey(index)) suspensions[index] = FanCurveSuspension.SharedTakeoverHeld;
                    fan.Restoring = true;
                    handBack = true;
                    /*
                     * On a covers-all channel another member may already hold the shared takeover (direct engine use:
                     * that member's own fixed table passes the backend check). Releasing only this fan would leave it
                     * outside firmware control with nothing driving it, so the takeover ends for every member, as for a
                     * Blind or Failed one; the group restarts only once this plan is recorded again.
                     */
                    others = EndSharedTakeover(index, fan.CoversAll);
                    UpdateFacts();
                }
                else if (failure is null)
                {
                    fan.Failures++;
                }
                if (!handBack && (failure is not null || fan.Failures >= FailureLimit))
                {
                    handBack = true;
                    Report(failure is not null ? $"tick:{index}" : $"failures:{index}",
                        failure is not null
                            ? $"Fan {index} curve tick failed; handing back to firmware: {failure}"
                            : $"Fan {index} rejected {FailureLimit} consecutive writes; handing back to firmware.");
                    if (plans.ContainsKey(index)) suspensions[index] = FanCurveSuspension.Failed;
                    fan.Restoring = true;
                    // A safety hand-back on a covers-all channel ends the takeover for every fan (see Decide).
                    others = EndSharedTakeover(index, fan.CoversAll);
                    UpdateFacts();
                }
            }
            if (handBack) Release(index, fan, out _, out _);
            foreach (var other in others) Release(other.Plan.FanIndex, other, out _, out _);
        }
    }

    /// <summary>朝目标走一步，一步最多这么多个百分点。</summary>
    private static double Approach(double current, double target, double step)
    {
        if (step <= 0)
        {
            return current;
        }
        var delta = target - current;
        return Math.Abs(delta) <= step ? target : current + Math.Sign(delta) * step;
    }

    private static FanDescription? DescriptionOf(IReadOnlyList<FanDescription> descriptions, int fanIndex)
    {
        foreach (var description in descriptions)
        {
            if (description.Index == fanIndex)
            {
                return description;
            }
        }
        return null;
    }

    private static double? ReadTemperature(SoftwareCurvePlan plan)
    {
        double? best = null;
        var sum = 0.0;
        var count = 0;
        foreach (var source in plan.Sources)
        {
            if (source.ReadCelsius() is not { } value || !double.IsFinite(value))
            {
                if (source.Required) return null;
                continue;
            }
            count++;
            sum += value;
            best = best is not { } current
                ? value
                : plan.Aggregation switch
                {
                    FanTemperatureAggregation.Minimum => Math.Min(current, value),
                    _ => Math.Max(current, value)
                };
        }
        if (count == 0)
        {
            return null;
        }
        return plan.Aggregation == FanTemperatureAggregation.Average ? sum / count : best;
    }

    /// <summary>
    /// 曲线在这个温度上要求多大转速。点之间线性插值，两端不外推。
    ///
    /// **不做平滑。** 平滑是界面上画线的事；这里要的是"用户在图上看到的那个点
    /// 对应多少转速"，替他圆滑一下只会让实际转速和他拖的点对不上。
    /// </summary>
    private static double PercentAt(IReadOnlyList<FanCurvePoint> curve, double celsius)
    {
        var ordered = curve.OrderBy(point => point.TemperatureCelsius).ToArray();
        if (celsius <= ordered[0].TemperatureCelsius)
        {
            return ordered[0].Percent;
        }
        var last = ordered[^1];
        if (celsius >= last.TemperatureCelsius)
        {
            return last.Percent;
        }
        for (var i = 1; i < ordered.Length; i++)
        {
            var upper = ordered[i];
            if (celsius > upper.TemperatureCelsius)
            {
                continue;
            }
            var lower = ordered[i - 1];
            var span = upper.TemperatureCelsius - lower.TemperatureCelsius;
            if (span <= 0)
            {
                return upper.Percent;
            }
            var ratio = (celsius - lower.TemperatureCelsius) / span;
            return lower.Percent + ((upper.Percent - lower.Percent) * ratio);
        }
        return last.Percent;
    }

    public void Dispose()
    {
        Task? run;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            cancellation?.Cancel();
            run = loop;
        }
        // Do not dispose a backend until its run has finished releasing it.
        try { run?.GetAwaiter().GetResult(); }
        catch (Exception) { /* The run caller observes faults; cleanup still proceeds. */ }
        StopAll();
        cancellation?.Dispose();
    }

    private sealed class FanRuntime(SoftwareCurvePlan plan)
    {
        public SoftwareCurvePlan Plan { get; set; } = plan;
        /// <summary>上一次真写下去的百分比。null 表示还没写过。</summary>
        public int? Applied { get; set; }
        /// <summary>Stopwatch timestamp of the last confirmed write; 0 before the first.</summary>
        public long LastWrite { get; set; }
        public int Failures { get; set; }
        public bool Restoring { get; set; }
        public bool TakeoverPrepared { get; set; }
        /// <summary>The next tick writes even if the target is unchanged (a shared takeover was released meanwhile).</summary>
        public bool ForceWrite { get; set; }
        /// <summary>This fan's channel takeover covers all fans (from the last decision's description).</summary>
        public bool CoversAll { get; set; }
        /// <summary>从什么时候开始一直读不到温度（Stopwatch 时间戳）。null 表示现在读得到。</summary>
        public long? BlindSince { get; set; }
        public int ReleaseFailures { get; set; }
        /// <summary>Stopwatch timestamp before which a tick does not retry a failed release.</summary>
        public long NextReleaseAttempt { get; set; }
    }
}
