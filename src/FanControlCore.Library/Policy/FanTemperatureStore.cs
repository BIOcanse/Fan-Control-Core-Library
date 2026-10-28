namespace FanControlCore.Policy;

/// <summary>
/// 宿主推过来的温度。
///
/// **这个核心自己没有传感器**，它只会读写风扇。软件曲线要的温度由宿主订阅并按期推进来，
/// 所以这里是个纯粹的"最新值"存放处：写进来就原子替换，读出来就是当时那个值，
/// 读不触发任何采样，也不等待。
///
/// **带时效。** 推送一旦停了（宿主崩了、被杀了、订阅掉了），这里的值就会过期；
/// 过期之后必须让曲线引擎把风扇交还固件，而不是抱着一个越来越旧的温度继续调速 ——
/// 那正是"软件曲线"最危险的失效方式。
/// </summary>
public sealed class FanTemperatureStore
{
    /// <summary>
    /// 多久没有新样本就算过期。
    ///
    /// 订阅间隔是 0.1 秒，这里给到 3 秒 —— 中间漏几十次是正常的
    /// （EC 那条通道偶尔会被一次慢操作占住），但连续三秒收不到，
    /// 那就是推送侧真的不在了。
    /// </summary>
    public static readonly TimeSpan MaximumAge = TimeSpan.FromSeconds(3);

    private readonly object gate = new();
    private readonly Dictionary<string, FanTemperatureSample> values = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>宿主推来一批温度。整批原子替换，不做增量合并；无效的样本丢掉。</summary>
    public void Push(IReadOnlyList<FanTemperatureSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        lock (gate)
        {
            values.Clear();
            var now = DateTime.UtcNow.Ticks;
            foreach (var sample in samples)
            {
                if (!double.IsFinite(sample.Celsius) || sample.ObservedAtUtcTicks <= 0
                    || sample.ObservedAtUtcTicks > now) continue;
                values[sample.Name] = sample;
            }
        }
    }

    /// <summary>
    /// 读一个温度。没有这一项、或者整批已经过期，都回 null。
    ///
    /// **过期当成读不到，不当成"还是上次那个值"** —— 后者会让引擎拿着一个
    /// 三分钟前的温度继续调速，而且看不出有什么不对。
    /// </summary>
    public double? Read(string name)
    {
        lock (gate)
        {
            return values.TryGetValue(name, out var sample) && IsFreshAt(sample, DateTime.UtcNow.Ticks)
                ? sample.Celsius : null;
        }
    }

    // Read a whole kind under the same store gate, including the CPU requirement for "any".
    internal double? ReadKind(string kind)
    {
        lock (gate)
        {
            var now = DateTime.UtcNow.Ticks;
            double? hottest = null;
            var hasCpu = false;
            foreach (var sample in values.Values)
            {
                if (!IsFreshAt(sample, now)) continue;
                var cpu = sample.Name.StartsWith(FanTemperatureKinds.Cpu, StringComparison.OrdinalIgnoreCase);
                hasCpu |= cpu;
                if (kind == FanTemperatureKinds.Cpu && !cpu) continue;
                if (kind == FanTemperatureKinds.Gpu
                    && !sample.Name.StartsWith(FanTemperatureKinds.Gpu, StringComparison.OrdinalIgnoreCase)) continue;
                hottest = hottest is { } current ? Math.Max(current, sample.Celsius) : sample.Celsius;
            }
            return kind == FanTemperatureKinds.Any && !hasCpu ? null : hottest;
        }
    }

    private static bool IsFreshAt(FanTemperatureSample sample, long now)
        => now - sample.ObservedAtUtcTicks <= MaximumAge.Ticks;

    /// <summary>现在还有没有新鲜的温度。</summary>
    public bool IsFresh
    {
        get
        {
            lock (gate)
            {
                var now = DateTime.UtcNow.Ticks;
                return values.Values.Any(sample => IsFreshAt(sample, now));
            }
        }
    }

    /// <summary>有哪些温度源的名字。给宿主对账用。</summary>
    public IReadOnlyList<string> Names
    {
        get
        {
            lock (gate)
            {
                var now = DateTime.UtcNow.Ticks;
                return [.. values.Values.Where(sample => IsFreshAt(sample, now)).Select(sample => sample.Name)];
            }
        }
    }
}
