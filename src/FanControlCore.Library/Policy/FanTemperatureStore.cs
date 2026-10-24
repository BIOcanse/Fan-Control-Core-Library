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
    private readonly Dictionary<string, double> values = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> observedAt = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>宿主推来一批温度。整批原子替换，不做增量合并。</summary>
    public void Push(IReadOnlyDictionary<string, double> samples, IReadOnlyDictionary<string, long>? observedAtUtcTicks = null)
    {
        ArgumentNullException.ThrowIfNull(samples);
        lock (gate)
        {
            values.Clear();
            observedAt.Clear();
            foreach (var (name, celsius) in samples)
            {
                if (double.IsFinite(celsius))
                {
                    var ticks = observedAtUtcTicks is null ? DateTime.UtcNow.Ticks
                        : observedAtUtcTicks.TryGetValue(name, out var stamp) ? stamp : 0;
                    if (ticks <= 0 || ticks > DateTime.UtcNow.Ticks) continue;
                    values[name] = celsius;
                    observedAt[name] = new DateTime(ticks, DateTimeKind.Utc);
                }
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
            if (!observedAt.TryGetValue(name, out var stamp) || DateTime.UtcNow - stamp > MaximumAge)
            {
                return null;
            }
            return values.TryGetValue(name, out var value) ? value : null;
        }
    }

    /// <summary>现在还有没有新鲜的温度。</summary>
    public bool IsFresh
    {
        get
        {
            lock (gate)
            {
                return observedAt.Values.Any(stamp => DateTime.UtcNow - stamp <= MaximumAge);
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
                return [.. values.Keys.Where(name => DateTime.UtcNow - observedAt[name] <= MaximumAge)];
            }
        }
    }
}
