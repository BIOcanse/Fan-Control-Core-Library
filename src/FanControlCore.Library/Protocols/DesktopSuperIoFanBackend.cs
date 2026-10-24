using System.Reflection;
using System.Runtime.Versioning;

namespace FanControlCore.Protocols;

/// <summary>
/// 台式机主板上的风扇接头（CPU_FAN / SYS_FAN / PUMP），经由 Super I/O。
///
/// **台式机的一级路由键是控制器芯片，不是主板品牌。** 绝大多数台式主板的风扇
/// 都挂在一颗 Super I/O 上（Nuvoton NCT67xx / ITE IT87xx / Fintek / Winbond），
/// 品牌只在少数需要 bridge/quirk 的板子上才有意义。所以这里是一条**通用**通道，
/// 不是"华硕风扇后端""微星风扇后端"。
///
/// 这条通道**借道 LibreHardwareMonitor**，理由有三：
///
/// <list type="number">
/// <item>它已经内置 PawnIO 的 LpcIO，**不需要 WinRing0** —— 那类端口 I/O 驱动
///   在开着内存完整性的机器上根本加载不了。</item>
/// <item>Gigabyte 第二颗 ITE、MSI 的 NCT6687DR 特殊映射这些 overlay，它已经做了；
///   我们自己再逆一遍只会逆出一份更差的。</item>
/// <item>它的 <c>IControl</c> 自带"软件接管 / 恢复硬件默认"两个状态，
///   恢复那一半正是这件事里最不能出错的部分。</item>
/// </list>
///
/// **反射加载，不在编译期引用。** LHM 是可选组件（用户未必装），而且许可证
/// （MPL-2.0）和这个核心（Apache-2.0）不是一回事 —— 这和处理器那条路把
/// GPL 的 ZenStates 隔离在独立进程里是同一个道理。装了就用，没装就如实说缺什么。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DesktopSuperIoFanBackend : IFanBackend
{
    /// <summary>
    /// 宿主用这个环境变量告诉我们 LibreHardwareMonitor 装在哪儿。
    ///
    /// **不去猜路径。** 猜一个相对目录，装法一变就找不到，而且找不到的时候
    /// 报出来的原因会是"这台机器没有 Super I/O"，那是假的。
    /// </summary>
    internal const string LibraryPathVariable = "FANCONTROLCORE_LHM_PATH";

    private const string LibraryFileName = "LibreHardwareMonitorLib.dll";

    private readonly string? libraryPath;
    public DesktopSuperIoFanBackend() : this(Environment.GetEnvironmentVariable(LibraryPathVariable)) { }
    public DesktopSuperIoFanBackend(string? libraryPath) => this.libraryPath = libraryPath;
    private object? computer;
    private readonly List<Channel> channels = [];

    public string Name => "desktop-superio";
    public string? RecoveryIdentity(int fanIndex)
        => fanIndex >= 0 && fanIndex < channels.Count
            ? channels[fanIndex].Control.GetType().GetProperty("Identifier")?.GetValue(channels[fanIndex].Control)?.ToString()
            : null;

    public bool TryOpen(out string? unavailableReason)
    {
        if (!OperatingSystem.IsWindows())
        {
            unavailableReason = "这条通道只有 Windows 上有（Linux 上直接走 hwmon）。";
            return false;
        }
        if (ResolveLibrary() is not { } libraryPath)
        {
            unavailableReason = "需要 LibreHardwareMonitor 组件才能控制主板风扇接头。";
            return false;
        }

        try
        {
            var assembly = Assembly.LoadFrom(libraryPath);
            if (assembly.GetType("LibreHardwareMonitor.Hardware.Computer") is not { } computerType)
            {
                unavailableReason = "LibreHardwareMonitor 组件的版本不对，找不到入口类型。";
                return false;
            }

            var instance = Activator.CreateInstance(computerType);
            if (instance is null)
            {
                unavailableReason = "起不来 LibreHardwareMonitor。";
                return false;
            }
            /*
             * **只开主板这一块。** 别的（CPU、显卡、硬盘）我们各有各的路，
             * 在这里多开一块只是白占一次采集和一次驱动加载。
             */
            computerType.GetProperty("IsMotherboardEnabled")?.SetValue(instance, true);
            computerType.GetMethod("Open")?.Invoke(instance, null);
            computer = instance;

            CollectChannels(instance);
            if (channels.Count == 0)
            {
                Close();
                /*
                 * 认出主板但一个可写风扇通道都没有 —— 笔记本就是这样
                 * （风扇挂在整机 EC 上，不是标准 Super I/O）。
                 * 说清楚"没有接头"，别说成"没装组件"。
                 */
                unavailableReason = "这台机器上没有可控的主板风扇接头。";
                return false;
            }
            unavailableReason = null;
            return true;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Close();
            unavailableReason = $"打不开主板风扇通道：{error.Message}";
            return false;
        }
    }

    /// <summary>
    /// 走一遍主板下面的子硬件，把**可写**的风扇通道收起来。
    ///
    /// 一个通道要凑齐两样：一个能设软件值的 <c>Control</c> 传感器（往下写的口），
    /// 以及同一颗芯片上序号相同的 <c>Fan</c> 传感器（读转速的口）。
    /// 读不到转速也照收 —— 只读的转速不影响写，而且没有转速回读时，
    /// 用户至少还能看见这个接头存在。
    /// </summary>
    private void CollectChannels(object instance)
    {
        var hardware = instance.GetType().GetProperty("Hardware")?.GetValue(instance) as Array;
        if (hardware is null)
        {
            return;
        }
        // while + 显式栈，不递归。
        var pending = new Stack<object>();
        foreach (var item in hardware)
        {
            if (item is not null)
            {
                pending.Push(item);
            }
        }
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            var type = node.GetType();
            type.GetMethod("Update")?.Invoke(node, null);

            if (type.GetProperty("SubHardware")?.GetValue(node) is Array children)
            {
                foreach (var child in children)
                {
                    if (child is not null)
                    {
                        pending.Push(child);
                    }
                }
            }
            if (type.GetProperty("Sensors")?.GetValue(node) is not Array sensors)
            {
                continue;
            }

            var fans = new Dictionary<int, object>();
            var controls = new List<(int Index, object Sensor, object Control)>();
            foreach (var sensor in sensors)
            {
                if (sensor is null)
                {
                    continue;
                }
                var sensorType = sensor.GetType();
                var kind = sensorType.GetProperty("SensorType")?.GetValue(sensor)?.ToString();
                var index = sensorType.GetProperty("Index")?.GetValue(sensor) as int? ?? 0;
                if (kind == "Fan")
                {
                    fans[index] = sensor;
                }
                else if (kind == "Control"
                    && sensorType.GetProperty("Control")?.GetValue(sensor) is { } control)
                {
                    controls.Add((index, sensor, control));
                }
            }

            foreach (var (index, sensor, control) in controls)
            {
                var label = sensor.GetType().GetProperty("Name")?.GetValue(sensor) as string;
                channels.Add(new Channel(
                    channels.Count,
                    string.IsNullOrWhiteSpace(label) ? $"风扇接头 {index + 1}" : label!,
                    control,
                    fans.GetValueOrDefault(index),
                    node));
            }
        }
    }

    private string? ResolveLibrary()
    {
        var root = libraryPath;
        if (string.IsNullOrWhiteSpace(root))
        {
            return null;
        }
        // 给的可能是目录，也可能直接是那个 dll。两种都认。
        if (File.Exists(root))
        {
            return root;
        }
        var candidate = Path.Combine(root, LibraryFileName);
        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>
    /// 每个接头一个通道。
    ///
    /// **角色报不出来。** 主板接头叫 CPU_FAN、SYS_FAN1 是丝印上的名字，
    /// 用户插什么上去我们不知道 —— 猜一个"CPU 风扇"会让他按着错的那把调。
    ///
    /// Windows 这条路上**只有固定占空比**：LHM 给的是软件接管 + 恢复默认两档，
    /// 没有把 Nuvoton 的 Smart Fan 曲线暴露出来。所以曲线由统一的软件引擎跑。
    /// 各接头互不相干，接管一个不影响其他 —— 和笔记本那种整机模式位不一样。
    /// </summary>
    public IReadOnlyList<FanDescription> Describe()
        => [.. channels.Select(channel => new FanDescription(
            channel.Index,
            channel.Name,
            Writable: true,
            SupportsDuty: true,
            Role: FanRoles.Unknown,
            FirmwareCurve: null,
            SupportsSoftwareCurve: true,
            CurvePoints: null,
            MinimumOnPercent: 0,
            RequiresPeriodicReassert: false,
            SoftwareTakeoverCoversAllFans: false))];

    public IReadOnlyList<FanState> Read()
    {
        var states = new List<FanState>(channels.Count);
        foreach (var channel in channels)
        {
            channel.Node.GetType().GetMethod("Update")?.Invoke(channel.Node, null);
            var duty = Value(channel.Control.GetType()
                .GetProperty("Sensor")?.GetValue(channel.Control));
            var mode = channel.Control.GetType()
                .GetProperty("ControlMode")?.GetValue(channel.Control)?.ToString();
            states.Add(new FanState(
                channel.Index,
                channel.Name,
                Value(channel.Fan),
                duty,
                // Super I/O 那边就是按百分比给的，没有另一套原始量纲。
                duty is null ? null : (int)Math.Round(duty.Value),
                Writable: true,
                SupportsDuty: true,
                // "Default" 就是交还给主板固件自己管。
                AutomaticControl: mode is null or "Default" or "Undefined"));
        }
        return states;
    }

    public bool TrySetDuty(int fanIndex, double percent, out string? failureReason)
    {
        if (ChannelAt(fanIndex) is not { } channel)
        {
            failureReason = "没有这个风扇接头。";
            return false;
        }
        if (!double.IsFinite(percent))
        {
            failureReason = "占空比得是个数。";
            return false;
        }

        /*
         * 夹进这条通道自己声明的软件范围，而不是直接夹 0~100。
         * 有的芯片的软件可控范围不是满量程，越界写下去的行为没有定义。
         */
        var control = channel.Control;
        var type = control.GetType();
        var low = type.GetProperty("MinSoftwareValue")?.GetValue(control) as float? ?? 0f;
        var high = type.GetProperty("MaxSoftwareValue")?.GetValue(control) as float? ?? 100f;
        var wanted = (float)Math.Clamp(percent, low, high);
        try
        {
            var method = type.GetMethod("SetSoftware", [typeof(float)]);
            if (method is null) { failureReason = "风扇控制接口缺少 SetSoftware 方法。"; return false; }
            method.Invoke(control, [wanted]);
            failureReason = null;
            return true;
        }
        catch (TargetInvocationException error)
        {
            failureReason = error.InnerException?.Message ?? "写不进这个风扇接头。";
            return false;
        }
    }

    /// <summary>全速就是写满量程。这条通道没有单独的"全速位"。</summary>
    public bool TrySetFullSpeed(int fanIndex, out string? failureReason)
        => TrySetDuty(fanIndex, 100, out failureReason);

    public bool TrySetAutomatic(int fanIndex, out string? failureReason)
    {
        if (ChannelAt(fanIndex) is not { } channel)
        {
            failureReason = "没有这个风扇接头。";
            return false;
        }
        try
        {
            // 交还主板固件。**这一步不能出错**，它是所有手动控制的退路。
            var method = channel.Control.GetType().GetMethod("SetDefault", Type.EmptyTypes);
            if (method is null) { failureReason = "风扇控制接口缺少 SetDefault 方法。"; return false; }
            method.Invoke(channel.Control, null);
            failureReason = null;
            return true;
        }
        catch (TargetInvocationException error)
        {
            failureReason = error.InnerException?.Message ?? "交还主板固件失败。";
            return false;
        }
    }

    private Channel? ChannelAt(int fanIndex)
        => channels.FirstOrDefault(channel => channel.Index == fanIndex);

    private static double? Value(object? sensor)
        => sensor?.GetType().GetProperty("Value")?.GetValue(sensor) as float?;

    private void Close()
    {
        if (computer is null)
        {
            return;
        }
        try
        {
            computer.GetType().GetMethod("Close")?.Invoke(computer, null);
        }
        catch (TargetInvocationException)
        {
            // 关不掉也没别的办法了；下面照样把引用丢掉。
        }
        computer = null;
        channels.Clear();
    }

    public void Dispose() => Close();

    /// <summary>一个主板风扇接头：往哪儿写、从哪儿读转速、归哪块子硬件。</summary>
    private sealed record Channel(
        int Index,
        string Name,
        object Control,
        object? Fan,
        object Node);
}
