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
///   除非这条通道声明了固件会夺回控制权。</item>
/// <item><b>退出必恢复</b>：**这条不可协商。** 引擎停下来（正常停、异常停、
///   进程没了）都要把风扇交还固件自动控制，而不是留在最后那个手动值上。</item>
/// </list>
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
    private static readonly TimeSpan BlindTimeout = TimeSpan.FromSeconds(5);

    private readonly Func<IFanBackend> resolveBackend;
    private IFanBackend Backend => resolveBackend();
    private readonly TimeSpan interval;
    private readonly TimeSpan reassertAfter;
    private readonly object gate = new();
    private readonly Dictionary<int, SoftwareCurvePlan> plans = [];
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

    // Called under gate after plan/runtime changes. Queue every transition in mutation order.
    private void UpdateFacts()
    {
        var needed = plans.Count > 0 || running.Count > 0;
        string[] orderedKinds = [FanTemperatureKinds.Cpu, FanTemperatureKinds.Gpu, FanTemperatureKinds.Any];
        var kinds = orderedKinds.Where(kind => plans.Values.Any(plan => plan.TemperatureKind == kind)).ToArray();
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

    /// <summary>这个风扇现在有没有软件曲线在跑。</summary>
    public bool IsRunning(int fanIndex)
    {
        lock (gate)
        {
            return loop is { IsCompleted: false } && plans.ContainsKey(fanIndex)
                && (!running.TryGetValue(fanIndex, out var fan) || !fan.Restoring);
        }
    }

    public bool HasPlan(int fanIndex)
    {
        lock (gate) return plans.ContainsKey(fanIndex);
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
    /// 记录这把风扇的软件曲线，已经有的就换掉；不启动执行。
    ///
    /// 换曲线**不重置当前转速**：风扇此刻转到哪儿是物理事实，和换不换曲线无关。
    /// 重置的话，换完那一下会从头按加速度爬，白白慢一截。
    /// </summary>
    public bool TryRecordPlan(SoftwareCurvePlan plan, out string? failureReason)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Curve.Count == 0)
        {
            failureReason = "曲线上一个点都没有。";
            return false;
        }
        if (plan.Sources.Count == 0)
        {
            failureReason = "没有指定看哪个温度。";
            return false;
        }

        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (running.TryGetValue(plan.FanIndex, out var existing) && existing.Restoring)
            {
                failureReason = "风扇仍在交还固件，请等待恢复完成。";
                return false;
            }
            plans[plan.FanIndex] = plan with { Curve = plan.Curve.ToArray(), Sources = plan.Sources.ToArray() };
            if (existing is not null) existing.Plan = plans[plan.FanIndex];
            if (changed.CurrentCount == 0) changed.Release();
            UpdateFacts();
        }
        PublishFactChanges();
        failureReason = null;
        return true;
    }

    /// <summary>
    /// 撤掉这把风扇的软件曲线，并**把它交还固件**。
    ///
    /// 交还是这件事的一部分，不是可选的后续 —— 只停循环不交还，
    /// 风扇会永远停在最后算出来的那个值上。
    /// </summary>
    public bool TryStop(int fanIndex, out string? failureReason)
    {
        bool restored;
        lock (gate) restored = TryStopHeld(fanIndex, out failureReason);
        PublishFactChanges();
        return restored;
    }

    private bool TryStopHeld(int fanIndex, out string? failureReason)
    {
        plans.Remove(fanIndex);
        failureReason = null;
        var restored = true;
        if (running.TryGetValue(fanIndex, out var fan))
        {
            fan.Restoring = true;
            restored = TryReleaseHeld(fanIndex, out failureReason);
        }
        UpdateFacts();
        return restored;
    }

    private bool TryReleaseHeld(int fanIndex, out string? failureReason)
    {
        inFlightOperation = $"curve release fan {fanIndex}";
        try
        {
            if (!Backend.TryReleaseOverride(fanIndex, out failureReason))
            {
                Report($"release:{fanIndex}", $"Fan {fanIndex} release failed; restoration pending: {failureReason ?? "not confirmed"}");
                return false;
            }
            running.Remove(fanIndex);
            UpdateFacts();
            return true;
        }
        catch (Exception error)
        {
            failureReason = error.Message;
            Report($"release:{fanIndex}", $"Fan {fanIndex} release failed; restoration pending: {error.Message}");
            return false;
        }
        finally { inFlightOperation = null; }
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
            indexes = [.. plans.Keys.Union(running.Keys)];
        }
        foreach (var index in indexes)
        {
            TryStop(index, out _);
        }
    }

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

    private async Task RunLoopAsync(CancellationToken stop)
    {
        // Assign the task before it can complete or access hardware.
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            while (true)
            {
                bool idle;
                lock (gate) idle = plans.Count == 0 && running.Count == 0;
                if (idle)
                {
                    await changed.WaitAsync(stop).ConfigureAwait(false);
                    continue;
                }
                using var timer = new PeriodicTimer(interval);
                while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false))
                {
                    bool idleAfterTick;
                    lock (gate)
                    {
                        stop.ThrowIfCancellationRequested();
                        BeforeIteration?.Invoke();
                        foreach (var plan in plans.Values)
                            if (!running.ContainsKey(plan.FanIndex)) running[plan.FanIndex] = new FanRuntime(plan);
                        foreach (var fan in running.Values.ToArray())
                        {
                            var index = fan.Plan.FanIndex;
                            if (fan.Restoring) { TryReleaseHeld(index, out _); continue; }
                            try
                            {
                                inFlightOperation = $"curve tick fan {index}";
                                Tick(fan);
                            }
                            catch (Exception error)
                            {
                                Report($"tick:{index}", $"Fan {index} curve tick failed; handing back to firmware: {error.Message}");
                                TryStopHeld(index, out _);
                            }
                            finally { inFlightOperation = null; }
                            if (fan.Failures >= 5) TryStopHeld(index, out _);
                        }
                        idleAfterTick = plans.Count == 0 && running.Count == 0;
                    }
                    PublishFactChanges();
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
            lock (gate)
            {
                // Retain plans; a later run starts from its target after firmware hand-back.
                foreach (var fan in running.Values.ToArray())
                {
                    fan.Restoring = true;
                    TryReleaseHeld(fan.Plan.FanIndex, out _);
                }
            }
            PublishFactChanges();
        }
    }

    private void Tick(FanRuntime fan)
    {
        var plan = fan.Plan;
        if (ReadTemperature(plan) is not { } temperature)
        {
            /*
             * 读不到温度。短暂读不到就保持上一次的转速等下一轮 ——
             * 那可能只是一次采样没赶上。
             *
             * **但不能一直这么等下去。** 温度源是宿主推过来的，推送停了
             * （宿主崩了、订阅掉了）这里就会一直读不到，而我们还按着风扇。
             * 连续读不到超过这么久就**交还固件** —— 软件曲线最危险的失效方式
             * 就是抱着一个再也不更新的温度继续调速。
             */
            fan.BlindSince ??= DateTime.UtcNow;
            if (DateTime.UtcNow - fan.BlindSince.Value >= BlindTimeout)
            {
                TryStopHeld(plan.FanIndex, out _);
            }
            return;
        }
        fan.BlindSince = null;

        if (temperature >= plan.CriticalCelsius)
        {
            /*
             * 临界兜底：**直接满速，不受加速度限制**。
             *
             * 这一条是保护，不是调速 —— 都到临界温度了还按斜率慢慢爬上去，
             * 那这条兜底就没有意义了。
             */
            Write(fan, 100);
            return;
        }

        // 温度直接查曲线，中间不做任何平滑 —— 见类上那段。
        var target = PercentAt(plan.Curve, temperature);

        /*
         * 按加速度朝目标走。**这是唯一决定灵敏度的地方。**
         *
         * 还没写过就直接落到目标：开头那一下没有"上一次"可言，
         * 硬要从 0 爬上去的话，刚接管的几秒风扇是偏慢的。
         */
        var wanted = fan.Applied is not { } last
            ? (int)Math.Round(target)
            : (int)Math.Round(Approach(last, target, plan.PercentPerSecond * interval.TotalSeconds));

        Write(fan, wanted);
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

    /// <summary>把这个百分比写下去，顺带处理死区、去重和重下发。</summary>
    private void Write(FanRuntime fan, int wanted)
    {
        var description = DescriptionOf(fan.Plan.FanIndex);
        var minimumOn = description?.MinimumOnPercent ?? 0;
        if (wanted > 0 && wanted < minimumOn)
        {
            /*
             * 落进硬件死区了。**要么给到转得起来的最低档，要么干脆停。**
             * 取中点作分界：偏上就抬到最低启动档，偏下就停 ——
             * 写一个转不起来的值，界面显示"已应用 5%"而风扇是停的，那是骗人。
             */
            wanted = wanted * 2 >= minimumOn ? minimumOn : 0;
        }

        var due = description?.RequiresPeriodicReassert == true
            && DateTime.UtcNow - fan.LastWrite >= reassertAfter;
        if (fan.Applied == wanted && !due)
        {
            // 目标没变就不写：每次写都是一次固件往返，白写没有任何好处。
            fan.Failures = 0;
            return;
        }

        // 用执行器那条路，不是"设固定转速"那条 —— 见 IFanBackend.TryDriveDuty。
        if (!fan.TakeoverPrepared)
        {
            if (PrepareTakeover?.Invoke(fan.Plan.FanIndex) is { } failure)
                throw new InvalidOperationException(failure);
            fan.TakeoverPrepared = true;
        }
        if (Backend.TryDriveDuty(fan.Plan.FanIndex, wanted, out _))
        {
            fan.Applied = wanted;
            fan.LastWrite = DateTime.UtcNow;
            fan.Failures = 0;
        }
        else
        {
            fan.Failures++;
        }
    }

    private FanDescription? DescriptionOf(int fanIndex)
    {
        foreach (var description in Backend.Describe())
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
        public DateTime LastWrite { get; set; } = DateTime.MinValue;
        public int Failures { get; set; }
        public bool Restoring { get; set; }
        public bool TakeoverPrepared { get; set; }
        /// <summary>从什么时候开始一直读不到温度。null 表示现在读得到。</summary>
        public DateTime? BlindSince { get; set; }
    }
}
