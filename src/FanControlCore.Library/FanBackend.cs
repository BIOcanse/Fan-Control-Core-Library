namespace FanControlCore;

/// <summary>
/// 一个风扇。
///
/// <paramref name="DutyPercent"/> 和 <paramref name="Rpm"/> 都可能读不到 ——
/// **读不到和读到 0 是两回事**，所以用 null 表示读不到，不填一个 0 糊弄过去。
/// </summary>
public sealed record FanState(
    int Index,
    string Name,
    double? Rpm,
    double? DutyPercent,
    /// <summary>
    /// 占空比的原始字节。
    ///
    /// 百分比是按这个平台的满速值换算出来的，而那个满速值各家不一样；
    /// 把原始值也带上，调用方要核对时不必反推。
    /// </summary>
    int? DutyRaw,
    /// <summary>能不能写。只读的风扇照样列出来，用户要看得见它存在。</summary>
    bool Writable,
    /// <summary>
    /// 能不能给任意占空比。
    ///
    /// **有的通道只有"全速"和"自动"两档**（Uniwill 的全速模式位就是这样）。
    /// 那种通道上不该假装能设 70% —— 收到 70% 就给 100%，是在骗用户。
    /// </summary>
    bool SupportsDuty,
    /// <summary>现在是固件在自动控制，还是被我们固定住了。</summary>
    bool AutomaticControl);

/// <summary>
/// 一个风扇**能做什么**。
///
/// 和 <see cref="FanState"/> 的区别是**变不变**：这里的每一项都由通道和机型决定，
/// 开机之后不会变；转速和现在归谁管是"此刻多少"，那是 <see cref="FanState"/> 的事。
///
/// 分开是有代价原因的：问"能不能设曲线"不该去读一遍 EC。
/// EC 每读一个字节要十几次固件往返，而调用方光是画一次界面就会问好几遍 ——
/// 实测把这两件事混在一起之后，列一次可控对象要十几秒。
/// </summary>
public sealed record FanDescription(
    int Index,
    string Name,
    /// <summary>能不能写。只读的风扇照样列出来，用户要看得见它存在。</summary>
    bool Writable,
    /// <summary>
    /// 能不能给任意占空比。
    ///
    /// **有的通道只有"全速"和"自动"两档**（Uniwill 的全速模式位就是这样）。
    /// 那种通道上不该假装能设 70% —— 收到 70% 就给 100%，是在骗用户。
    /// </summary>
    bool SupportsDuty,
    /// <summary>
    /// 这个风扇吹的是什么，取值见 <see cref="FanRoles"/>。
    ///
    /// **由通道回答，不由调用方猜。** 只有通道知道自己这台机器上 0 号是 CPU 还是
    /// 显卡 —— 调用方拿到的只是一串下标，按下标猜迟早猜错，而且每个调用方
    /// 都得各猜一遍。认不出来就是 <see cref="FanRoles.Unknown"/>，不编。
    /// </summary>
    string Role = FanRoles.Unknown,
    /// <summary>
    /// 固件曲线是哪一种形态，取值见 <see cref="FanFirmwareCurveKinds"/>；
    /// 这条通道没有固件曲线就是 null。
    ///
    /// **和"能不能跑软件曲线"是两件独立的事**，所以是两个字段：
    /// 有的通道两条路都有（本机的 Uniwill 就是），有的只有其中一条。
    /// 合成一个标签的话，"两条都有"根本表达不出来，用户也就没得选。
    /// </summary>
    string? FirmwareCurve = null,
    /// <summary>
    /// 能不能由软件接管跑曲线。前提就是"能设固定转速"——
    /// 软件曲线无非是每隔 0.1 秒算一个转速再写下去。
    /// </summary>
    bool SupportsSoftwareCurve = false,
    /// <summary>
    /// 软件接管这把风扇，会不会把**这条通道上所有风扇**一起从固件手里接过来。
    ///
    /// 有的平台上"交给软件管"是一个整机开关，不是每把风扇一个：
    /// Uniwill 的模式位就是这样，置上之后两把风扇一起脱离固件自动控制，
    /// 各自跟着自己的占空比寄存器走 —— 于是没挂曲线的那把会停在当时那个
    /// 转速上不再自动调。
    ///
    /// **这件事必须让用户事先知道**，所以由通道如实报出来，
    /// 界面在他选"软件接管"之前就把话说清楚。报不出来就是 false，不猜。
    /// </summary>
    bool SoftwareTakeoverCoversAllFans = false,
    /// <summary>
    /// 固件曲线能写几个点。软件曲线和不支持曲线的通道为 null。
    ///
    /// 各家差很多：ASUS 8 点、Gigabyte 15 点、Uniwill 16 区、Lenovo 10 档。
    /// **界面上给几个可拖的点由它决定**，不该写死成十个。
    /// </summary>
    int? CurvePoints = null,
    /// <summary>
    /// 风扇转得起来的最低占空比，低于它只能停转。0 表示没有这个死区。
    ///
    /// **硬件死区，不是我们定的策略。** 有的平台 0 到最小启动档之间是一片空白，
    /// 写 5% 和写 0 的结果一样（风扇停）。不报出来的话，用户拖到 5% 会看到
    /// 风扇停转，而界面显示"已应用 5%"。
    /// </summary>
    int MinimumOnPercent = 0,
    /// <summary>
    /// 固件会周期性把控制权夺回去，必须定时重写才能维持。
    ///
    /// HP 那条链路是典型：固件约 120 秒回退到自动，所以内核每 90 秒重写一次。
    /// 声明了这一条的通道，软件曲线引擎即使目标值没变也要按期重下发。
    /// </summary>
    bool RequiresPeriodicReassert = false,
    /// <summary>
    /// 固件曲线**要满足什么条件才会被固件执行**。没有前置条件就是 null。
    ///
    /// 这和"写不写得进去"是两回事：表能写进去、回读也一致，固件却可能压根不拿它
    /// 去跑风扇。联想就是这样 —— 它的表按电源模式分开存，EC 只在自定义模式下执行。
    /// **写成功不等于生效。**
    ///
    /// 满足不了的时候由上层把这一项锁住、把这句话原样给用户看，
    /// 而**不替他去改那个前置**：那些模式往往连带改功耗策略，
    /// 背着用户动它是隐式副作用。
    ///
    /// 由通道在开通道那一刻判定（那时它已经读过一次表，知道当前是哪个模式），
    /// 所以用户自己切了模式之后，要重新检测硬件才会更新。
    /// </summary>
    string? FirmwareCurveRequires = null,
    /// <summary>模块实现了显式原表恢复；不表示当前一定有可用原表。</summary>
    bool SupportsFirmwareCurveReset = false);

/// <summary>风扇吹的是什么。</summary>
public static class FanRoles
{
    public const string Cpu = "cpu";
    public const string Gpu = "gpu";
    /// <summary>进风扇（笔记本上的内吹）。</summary>
    public const string Intake = "intake";
    public const string Case = "case";
    /// <summary>这条通道说不出来。**不猜**。</summary>
    public const string Unknown = "unknown";
}

/// <summary>固件曲线的形态 —— 固件那张表给多大自由度。</summary>
public static class FanFirmwareCurveKinds
{
    /// <summary>温度点和转速点都能写。最理想的一种。</summary>
    public const string Xy = "xy";

    /// <summary>
    /// 只有转速档可写，温度断点由固件定死或只读（Lenovo Legion 的 10 档表）。
    ///
    /// **不要把它伪装成能任意拖点的 XY 曲线** —— 用户拖了温度却不生效，比不给更糟。
    /// </summary>
    public const string Table = "table";
}

/// <summary>
/// 一条曲线交给谁执行。**这是用户的选择，不是我们替他定的。**
///
/// 两条路的承诺完全不同，所以必须让他自己选：
///
/// <list type="bullet">
/// <item><b>固件</b>：写进固件的表，之后固件自己查表。本程序关了、重启了都还在。
///   代价是受固件那张表的形状限制（几个点、温度断点能不能改）。</item>
/// <item><b>软件</b>：我们每 0.1 秒读温度、算转速、写下去。曲线想怎么画都行，
///   还能挑看哪路温度；代价是**程序不在就没了**，风扇会交还固件。</item>
/// </list>
///
/// 两条都有的机器上默认选固件 —— "设一次就一直有效"是更好的默认。
/// </summary>
public static class FanCurveExecutions
{
    public const string Firmware = "firmware";
    public const string Software = "software";
}

/// <summary>曲线上的一个点：到这个温度时给这么大转速。</summary>
public sealed record FanCurvePoint(double TemperatureCelsius, double Percent);

/// <summary>
/// 一条风扇控制通道。
///
/// **后端只回答"这台机器上这条通道能做什么"，不做策略。**
/// 用哪条通道、谁持有写权限、退出时怎么恢复，都是核心的事 ——
/// 两处各存一份策略，迟早会对不上。
/// </summary>
public interface IFanBackend : IDisposable
{
    /// <summary>这条通道的名字，出现在 describe 里，方便用户和我们自己定位问题。</summary>
    string Name { get; }
    IReadOnlyList<string> Diagnostics => [];

    /// <summary>Stable channel identity independent of enumeration order; ambiguous identities block recovery.</summary>
    string? RecoveryIdentity(int fanIndex)
    {
        var fan = Describe().FirstOrDefault(fan => fan.Index == fanIndex);
        return fan is null ? null : fan.Name + "\n" + fan.Role + "\n" + fan.FirmwareCurve;
    }

    /// <summary>
    /// 这台机器上这条通道能不能用。
    ///
    /// 用不了要说为什么 —— "认不出这台机器"和"没有权限"对用户是完全不同的两件事。
    /// </summary>
    bool TryOpen(out string? unavailableReason);

    /// <summary>Typed attempt. Existing integrations without typed evidence report Other.</summary>
    bool TryOpen(out FanOpenFailureKind failureKind, out string? unavailableReason)
    {
        failureKind = FanOpenFailureKind.Other;
        return TryOpen(out unavailableReason);
    }

    /// <summary>用户显式重新检测时重新读取通道能力，不改设定和所有权。</summary>
    void RefreshDetection() { }

    /// <summary>读取已探测的风扇能力，不触发检测。</summary>
    IReadOnlyList<FanDescription> Describe();

    /// <summary>现在有几个风扇、各自什么状态。</summary>
    IReadOnlyList<FanState> Read();

    /// <summary>
    /// 这个风扇现在跑的曲线。
    ///
    /// **是"现在是什么"，不是"能设成什么"**，所以它碰硬件，归在 <see cref="Read"/> 这一侧。
    /// 固件执行的曲线本来就存在机器里，读出来用户才有个起点可改 ——
    /// 不读就只能给他一条我们编的曲线，那条曲线和机器现在的行为毫无关系。
    ///
    /// 这条通道给不了曲线、或者这次读不到，就返回 null。
    /// </summary>
    IReadOnlyList<FanCurvePoint>? ReadCurve(int fanIndex) => null;

    /// <summary>
    /// 把一条曲线写进**固件**那张表，之后由固件自己执行。
    ///
    /// 只有 <see cref="FanDescription.FirmwareCurve"/> 不为 null 的通道做得到；
    /// 默认说不行。做不到要**说清楚为什么**，别只回一个 false ——
    /// 上层要把那句话原样给用户看。
    /// </summary>
    bool TryWriteCurve(
        int fanIndex,
        IReadOnlyList<FanCurvePoint> curve,
        out string? failureReason)
    {
        failureReason = "这条通道给不了固件风扇曲线。";
        return false;
    }

    /// <summary>
    /// 软件曲线的**执行器**：把这一刻算出来的占空比写下去。
    ///
    /// 默认就是 <see cref="TrySetDuty"/>。之所以留成一个可覆写的口子，
    /// 是因为**有的通道上"设固定转速"和"软件调速"不该走同一条路**：
    /// 本机的 Uniwill 就是 —— 固定转速走固件表（设一次就一直有效），
    /// 而软件曲线每 0.1 秒要写一次，走直接占空比那个寄存器才合适，
    /// 不然每秒十次重排固件的表，既慢又没必要。
    /// </summary>
    bool TryDriveDuty(int fanIndex, double percent, out string? failureReason)
        => TrySetDuty(fanIndex, percent, out failureReason);

    /// <summary>
    /// 把某个风扇固定在这个占空比。
    ///
    /// 只有 <see cref="FanState.SupportsDuty"/> 为真的通道才支持。
    /// 越界由后端按机型 profile 夹住 —— 但**夹了要说**，不能默默写一个别的值。
    /// </summary>
    bool TrySetDuty(int fanIndex, double percent, out string? failureReason);

    /// <summary>已保存的临时恢复责任，包括失败写入留下的恢复基线。</summary>
    bool HasPendingTemporaryRestore(int fanIndex) => false;

    /// <summary>
    /// 全速。
    ///
    /// 这是最低共同能力：几乎每条通道都有"一键强冷"，哪怕给不了任意占空比。
    /// </summary>
    bool TrySetFullSpeed(int fanIndex, out string? failureReason);

    /// <summary>把这个风扇交还给固件自动控制。</summary>
    bool TrySetAutomatic(int fanIndex, out string? failureReason);

    /// <summary>撤销持久固件曲线。需要基线的通道不能将缺失基线报告为恢复成功。</summary>
    bool TryResetFirmwareCurve(int fanIndex, out string? failureReason)
        => TrySetAutomatic(fanIndex, out failureReason);

    /// <summary>释放临时定速或软件接管，保留用户设定的持久固件曲线。</summary>
    bool TryReleaseOverride(int fanIndex, out string? failureReason)
        => TrySetAutomatic(fanIndex, out failureReason);
}
