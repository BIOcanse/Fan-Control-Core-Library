namespace FanControlCore.Transport;

/// <summary>
/// EC 通道的**机器级**互斥。
///
/// EC 是一块硬件，一台机器上只有一份。它的每次访问都是"下发一条命令、再取回结果"
/// 两步，中间被另一个进程插进来，取回的就是别人的结果 —— 表现出来是写入回读对不上、
/// 读数忽然变成另一个寄存器的值，而两边都觉得自己没做错。
///
/// 进程内的 lock 挡不住这个：同一台机器上还有别的程序在用同一条 ACPI WMI 接口
/// （厂商控制中心、监控软件，以及**用这个核心的宿主程序自己**）。
/// 所以锁要跨进程，名字按**通道**取 —— 谁用这条通道谁就用这把锁。
///
/// 拿不到全局名字时沿用会话内互斥；此限制随 describe diagnostics 明确报告。
/// </summary>
public sealed class EcChannelLock : IDisposable
{
    /// <summary>
    /// 等一把锁最多多久。一次 EC 事务是毫秒级的，等到这个数说明对方卡死了；
    /// 无限等会把整个核心挂住，那比这次操作失败更糟。
    /// </summary>
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    public string? Diagnostic { get; }
    private readonly Mutex mutex;
    private bool disposed;

    public EcChannelLock(string channelName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelName);
        var global = Create($@"Global\{channelName}");
        if (global is null) Diagnostic = "Global EC mutex unavailable; using the existing session-local EC mutex. Cross-session serialization is unavailable.";
        mutex = global ?? new Mutex(false, $@"Local\{channelName}");
    }

    /// <summary>
    /// 独占这条通道做一件事。超时就不做 —— 宁可如实失败，不和别人抢着写 EC。
    /// </summary>
    public bool Hold<TResult>(Func<TResult> transaction, out TResult result)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        result = default!;
        var acquired = false;
        try
        {
            try
            {
                acquired = mutex.WaitOne(WaitTimeout);
            }
            catch (AbandonedMutexException)
            {
                // 上一个持有者没释放就退出了。锁归我们，通道状态由各自的回读负责核对。
                acquired = true;
            }
            if (!acquired)
            {
                return false;
            }
            result = transaction();
            return true;
        }
        finally
        {
            if (acquired)
            {
                mutex.ReleaseMutex();
            }
        }
    }

    private static Mutex? Create(string name)
    {
        try
        {
            return new Mutex(false, name);
        }
        catch (Exception error) when (error is UnauthorizedAccessException
            or System.IO.IOException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        mutex.Dispose();
    }
}
