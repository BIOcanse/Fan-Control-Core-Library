using FanControlCore.Protocols;

namespace FanControlCore.Discovery;

/// <summary>这条路由适用于哪个平台。一级路由只看这个，不碰硬件。</summary>
public enum FanPlatform
{
    Windows,
    Linux
}

/// <summary>
/// 控制通道的类别。**枚举的次序就是优先级**，从上到下。
///
/// 这个次序不是偏好，是安全次序：内核和固件自己维护的接口排在前面，
/// 我们照着别人逆出来的寄存器表排在最后。
/// </summary>
public enum FanChannelCategory
{
    /// <summary>内核已经把风扇导出成标准属性。Linux 上有就用它，不必知道是什么品牌。</summary>
    Hwmon,

    /// <summary>
    /// 主板上的 Super I/O（Nuvoton / ITE / Fintek / Winbond）。台式机的主路径。
    ///
    /// **排在厂商固件之前**：台式机上根本没有"厂商风扇固件接口"这回事，
    /// 而笔记本上又枚举不到可写的 Super I/O 接头 —— 两边天然不重叠，
    /// 谁在前不影响结果。放前面是因为台式机上它才是那条正路。
    /// </summary>
    SuperIo,

    /// <summary>厂商固件自己暴露的 WMI / ACPI 方法。Windows 上的主路径。</summary>
    VendorFirmware,

    /// <summary>USB HID 设备。协议天然跨平台。</summary>
    Hid,

    /// <summary>直接读写 EC 寄存器。**只做兜底**，而且必须匹配固件指纹。</summary>
    RawEc,

    /// <summary>Dell 的 SMM 特殊路径。</summary>
    Smm
}

/// <summary>
/// 一条风扇通道路由。
///
/// **路由是数据，不是判断。** 一台机器落在哪条通道上，由两级查表决定：
///
/// <list type="number">
/// <item><b>一级路由</b>：按平台筛，再按 <see cref="FanChannelCategory"/> 排序。
///   这一级只看这台机器是 Windows 还是 Linux，不碰任何硬件。</item>
/// <item><b>二级路由</b>：同一类别里可能有好几家的协议，由各自的**识别依据**
///   （厂商接口的 GUID 在不在、固件指纹认不认）决定哪一家。
///   各家的识别依据互不重叠，所以一台机器在一个类别里最多命中一条 ——
///   同类别内谁先谁后不影响结果。</item>
/// </list>
///
/// 命中即选中，不再往后试。所以"这台机器走哪条通道"是一个确定的答案，
/// 不取决于代码里谁写在前面。
/// </summary>
/// <param name="Category">一级路由的次序。</param>
/// <param name="Create">命中之后用哪个后端。识别依据在后端自己的 <c>TryOpen</c> 里。</param>
public sealed record FanChannelRoute(
    FanChannelCategory Category,
    Func<IFanBackend> Create)
{
    /// <summary>
    /// **一级路由**：这个平台上有哪些候选通道，按类别次序排好。
    ///
    /// 平台是表的键而不是表里的一列 —— 作为一列的话，每一行都要另外声明
    /// "我只在 Windows 上能造出来"，而那句声明和真正的平台判断是两处，会漂移。
    /// 按平台分表，平台判断就只有这一处。
    ///
    /// **加一条通道就是往对应的表里加一行**，不动核心。每一行要回答清楚三件事：
    /// 哪个平台（在哪张表里）、哪一类通道（<see cref="Category"/>）、
    /// 识别依据是什么（在后端的 <c>TryOpen</c> 里）。识别依据必须是能把这台机器
    /// 和别家区分开的东西 —— 固件接口在不在、固件指纹认不认，
    /// **不能是"试着写一下看行不行"**。
    /// </summary>
    public static IReadOnlyList<FanChannelRoute> For(FanPlatform platform, FanCoreOptions? options = null)
    {
        var routes = new List<FanChannelRoute>();
        if (platform == FanPlatform.Linux && OperatingSystem.IsLinux())
        {
            // 内核已经导出可写 PWM 就直接用。
            // 识别依据：/sys/class/hwmon 下有没有风扇。
            routes.Add(new(FanChannelCategory.Hwmon, static () => new HwmonFanBackend()));
        }
        if (platform == FanPlatform.Windows && OperatingSystem.IsWindows())
        {
            routes.AddRange(WindowsRoutes.Create(options ?? FanCoreOptions.FromEnvironment()));
        }
        routes.Sort((left, right) => left.Category.CompareTo(right.Category));
        return routes;
    }

    /// <summary>
    /// Windows 那张表。单独放一个带平台标注的类里，是为了让**编译器也看见**
    /// 这些后端只在 Windows 上存在 —— 否则平台判断只写在注释和一个字段里，
    /// 编译器帮不上忙，而这正是跨平台构建会悄悄出问题的地方。
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static class WindowsRoutes
    {
        public static IReadOnlyList<FanChannelRoute> Create(FanCoreOptions options) =>
        [
            // 厂商固件接口。识别依据都是各家固件自己的 WMI 接口在不在 ——
            // GUID 是固件里写死的，各家互不重叠，所以最多命中一条。
            /*
             * 台式主板的风扇接头。**一级路由键是控制器芯片，不是主板品牌** ——
             * 华硕/微星/技嘉的台式板绝大多数都是那几颗 Super I/O，
             * 按品牌分只会把同一件事写好几遍。
             *
             * 识别依据：能不能枚举到可写的接头。笔记本上枚举不到（风扇在整机 EC 上），
             * 所以它和下面那几条笔记本通道天然不重叠。
             */
            new(FanChannelCategory.SuperIo, () => new DesktopSuperIoFanBackend(options.LibreHardwareMonitorPath)),

            new(FanChannelCategory.VendorFirmware, static () => new AsusWmiFanBackend()),
            new(FanChannelCategory.VendorFirmware, static () => new HpWmiFanBackend()),
            new(FanChannelCategory.VendorFirmware, () => new LenovoWmiFanBackend(options.StateDirectory)),
            new(FanChannelCategory.VendorFirmware, static () => new AcerWmiFanBackend()),

            // 同方公版：借道固件的 ACPI WMI 方法，但读写的是 EC 寄存器，
            // 所以归 Raw EC 那一类 —— 规矩也按那一类办：固件指纹认不出就只读。
            new(FanChannelCategory.RawEc, () => new UniwillWmiFanBackend(options.StateDirectory))
        ];
    }
}
