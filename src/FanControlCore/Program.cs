using FanControlCore;
using FanControlCore.Discovery;

// Packaging smoke check exits before any hardware discovery or session is opened.
if (args.Length == 1 && args[0] == "--runtime-check")
{
    Console.WriteLine("{\"runtimeReady\":true}");
    return;
}

// 标准输入一行一条请求，标准输出一行一条应答。
// 父进程关掉标准输入，这个进程就结束 —— 而且**走之前把风扇交还固件**。
using var core = new FanCore();
core.Open();
StdioHost.Run(core, Console.In, Console.Out);
