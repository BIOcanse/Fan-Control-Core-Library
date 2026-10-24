using System.Globalization;

namespace FanControlCore.Protocols;

/// <summary>
/// Linux 的 hwmon 通道。
///
/// **Linux 上这是第一优先，而且通常到此为止。** 内核已经把 RPM 和 PWM 导出成
/// 标准属性了，应用不需要知道这台机器是什么品牌 —— 厂商驱动该做的事
/// 内核已经做过一遍，我们再去用户态重写一遍只会多一个出错的地方。
///
/// 只读的 hwmon 也列出来：能报转速就有价值，只是不能写。
/// </summary>
public sealed class HwmonFanBackend : IFanBackend
{
    private const string HwmonRoot = "/sys/class/hwmon";

    /// <summary>`pwm*_enable` 的取值：1 是手动，2 及以上是固件/驱动的自动模式。</summary>
    private const string ManualMode = "1";
    private const string AutomaticMode = "2";

    private readonly List<HwmonFan> fans = [];

    public string Name => "hwmon";
    public string? RecoveryIdentity(int fanIndex)
    {
        if (fanIndex < 0 || fanIndex >= fans.Count || fans[fanIndex].DutyPath is not { } duty) return null;
        var device = new DirectoryInfo(Path.GetDirectoryName(duty)!);
        var physical = device.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? device.FullName;
        return Path.Combine(physical, Path.GetFileName(duty));
    }

    public bool TryOpen(out string? unavailableReason)
    {
        fans.Clear();
        if (!OperatingSystem.IsLinux())
        {
            unavailableReason = "hwmon 只有 Linux 上有。";
            return false;
        }
        if (!Directory.Exists(HwmonRoot))
        {
            unavailableReason = "这台机器上没有 /sys/class/hwmon。";
            return false;
        }

        foreach (var device in Directory.EnumerateDirectories(HwmonRoot))
        {
            var deviceName = ReadText(Path.Combine(device, "name")) ?? Path.GetFileName(device);
            // fan1_input、fan2_input……编号从 1 开始，不连续也有可能。
            for (var channel = 1; channel <= 16; channel++)
            {
                var rpmPath = Path.Combine(device, $"fan{channel}_input");
                var dutyPath = Path.Combine(device, $"pwm{channel}");
                var modePath = Path.Combine(device, $"pwm{channel}_enable");
                if (!File.Exists(rpmPath) && !File.Exists(dutyPath))
                {
                    continue;
                }
                fans.Add(new HwmonFan(
                    $"{deviceName} fan{channel}",
                    File.Exists(rpmPath) ? rpmPath : null,
                    File.Exists(dutyPath) ? dutyPath : null,
                    File.Exists(modePath) ? modePath : null));
            }
        }

        if (fans.Count == 0)
        {
            unavailableReason = "hwmon 下没有风扇。";
            return false;
        }
        unavailableReason = null;
        return true;
    }

    /// <summary>
    /// hwmon 下每个风扇能做什么，由扫描时找到的那几个文件决定 —— 只看路径，不读文件。
    /// pwm 可写就意味着任意占空比：它本来就是个 0-255 的数。
    /// </summary>
    public IReadOnlyList<FanDescription> Describe()
    {
        var descriptions = new List<FanDescription>(fans.Count);
        for (var index = 0; index < fans.Count; index++)
        {
            var fan = fans[index];
            var writable = fan.DutyPath is not null && CanWrite(fan.DutyPath);
            descriptions.Add(new FanDescription(
                index,
                fan.Name,
                writable,
                writable,
                // 内核把 pwm 导出成可写属性，那就是一个标准的固定占空比执行器，
                // 曲线由我们自己跑。写不了的就是只读。
                SupportsSoftwareCurve: writable));
        }
        return descriptions;
    }

    public IReadOnlyList<FanState> Read()
    {
        var states = new List<FanState>(fans.Count);
        for (var index = 0; index < fans.Count; index++)
        {
            var fan = fans[index];
            var raw = fan.DutyPath is null ? null : ReadNumber(fan.DutyPath);
            states.Add(new FanState(
                index,
                fan.Name,
                fan.RpmPath is null ? null : ReadNumber(fan.RpmPath),
                // hwmon 的 pwm 是 0-255，换算成百分比给调用方。
                raw is null ? null : Math.Round(raw.Value * 100 / 255, 1),
                raw is null ? null : (int)raw.Value,
                Writable: fan.DutyPath is not null && CanWrite(fan.DutyPath),
                // hwmon 的 pwm 就是个 0-255 的数，任意占空比本来就支持。
                SupportsDuty: fan.DutyPath is not null && CanWrite(fan.DutyPath),
                AutomaticControl: fan.ModePath is null
                    || ReadText(fan.ModePath)?.Trim() != ManualMode));
        }
        return states;
    }

    public bool TrySetDuty(int fanIndex, double percent, out string? failureReason)
    {
        if (!TryResolve(fanIndex, out var fan, out failureReason))
        {
            return false;
        }
        if (fan.DutyPath is null)
        {
            failureReason = "这个风扇是只读的。";
            return false;
        }

        // 先切手动，再写占空比 —— 反过来的话固件还在自动模式，写进去马上被覆盖。
        if (fan.ModePath is not null && !TryWrite(fan.ModePath, ManualMode, out failureReason))
        {
            return false;
        }
        var raw = (int)Math.Round(Math.Clamp(percent, 0, 100) * 255 / 100);
        return TryWrite(fan.DutyPath, raw.ToString(CultureInfo.InvariantCulture), out failureReason);
    }

    public bool TrySetFullSpeed(int fanIndex, out string? failureReason)
        => TrySetDuty(fanIndex, 100, out failureReason);

    public bool TrySetAutomatic(int fanIndex, out string? failureReason)
    {
        if (!TryResolve(fanIndex, out var fan, out failureReason))
        {
            return false;
        }
        if (fan.ModePath is null)
        {
            failureReason = "这个风扇没有自动模式开关。";
            return false;
        }
        return TryWrite(fan.ModePath, AutomaticMode, out failureReason);
    }

    private bool TryResolve(int fanIndex, out HwmonFan fan, out string? failureReason)
    {
        if (fanIndex < 0 || fanIndex >= fans.Count)
        {
            fan = default!;
            failureReason = "没有这个风扇。";
            return false;
        }
        fan = fans[fanIndex];
        failureReason = null;
        return true;
    }

    private static bool CanWrite(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Write);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryWrite(string path, string value, out string? failureReason)
    {
        try
        {
            File.WriteAllText(path, value);
            failureReason = null;
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            failureReason = "没有写这个 hwmon 属性的权限，需要 root。";
            return false;
        }
        catch (IOException error)
        {
            failureReason = error.Message;
            return false;
        }
    }

    private static string? ReadText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static double? ReadNumber(string path)
        => double.TryParse(
            ReadText(path)?.Trim(),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var value)
                ? value
                : null;

    public void Dispose()
    {
        // hwmon 没有要释放的句柄；恢复自动模式由核心统一做。
    }

    private readonly record struct HwmonFan(
        string Name,
        string? RpmPath,
        string? DutyPath,
        string? ModePath);
}
