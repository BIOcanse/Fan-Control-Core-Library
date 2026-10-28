using FanControlCore.Discovery;

namespace FanControlCore;

/// <summary>The executable owns curve execution for the lifetime of its stdio server.</summary>
internal static class StdioHost
{
    internal static void Run(FanCore core, TextReader input, TextWriter output)
    {
        using var stop = new CancellationTokenSource();
        var run = core.RunSoftwareCurveAsync(stop.Token);

        // Ctrl+C, process exit and the server's end can arrive together; restore once, later callers wait for it.
        var restoreGate = new object();
        var restored = false;
        void StopAndRestore()
        {
            lock (restoreGate)
            {
                if (restored) return;
                stop.Cancel();
                try { run.GetAwaiter().GetResult(); }
                catch (Exception error) { Console.Error.WriteLine($"Software curve run failed: {error.Message}"); }
                core.RestoreAll();
                restored = true;
            }
        }

        // 正常退出、Ctrl+C 尽力恢复；强制结束不能执行托管退出处理。
        EventHandler onExit = (_, _) => StopAndRestore();
        ConsoleCancelEventHandler onCancel = (_, arguments) =>
        {
            StopAndRestore();
            arguments.Cancel = false;
        };
        AppDomain.CurrentDomain.ProcessExit += onExit;
        Console.CancelKeyPress += onCancel;

        try { FanCommandServer.Run(core, input, output); }
        finally
        {
            StopAndRestore();
            AppDomain.CurrentDomain.ProcessExit -= onExit;
            Console.CancelKeyPress -= onCancel;
        }
    }
}
