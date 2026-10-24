using System.Management;
using System.Runtime.Versioning;

namespace FanControlCore.Transport;

/// <summary>
/// Windows 上调一个 WMI 类方法的通道。
///
/// 各家 OEM 在 Windows 上的风扇接口长得都是同一个样子：
/// <c>root\WMI</c>（偶尔是别的 scope）下有一个厂商类，类上有一两个方法，
/// 参数塞进去、结果读出来。**不一样的只有三件事**：调哪个类的哪个方法、
/// 参数怎么放、返回里哪一位是转速。那三件事属于各家的协议，
/// 连接、调用、异常、设备失效之后重新解析这些是共同的，都在这里。
///
/// 这一层**不认识风扇**。它不知道什么是转速、什么是占空比 ——
/// 那样各家的语义就会渗进来，最后变成一个谁都看不懂的公共分母。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WmiMethodChannel : IDisposable
{
    /// <summary>
    /// 厂商方法失效之后要不要重新找设备。
    ///
    /// 驱动被停用、设备被重新枚举之后，缓存的那个 <see cref="ManagementObject"/>
    /// 就是个死引用。发现它不行了就丢掉，下次重新解析 —— 而不是从此报"没有这条通道"。
    /// </summary>
    private readonly object gate = new();
    private readonly string scope;
    private readonly string? declaredClassName;
    private readonly string? classGuid;
    private readonly string? instanceName;
    private string? resolvedClassName;
    private ManagementObject? device;
    private bool probed;
    private bool disposed;

    public WmiMethodChannel(string scope, string className, string? instanceName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(className);
        this.scope = scope;
        declaredClassName = className;
        this.instanceName = instanceName;
    }

    private WmiMethodChannel(string scope, string classGuid, bool byGuid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(classGuid);
        this.scope = scope;
        this.classGuid = classGuid.Trim('{', '}');
        _ = byGuid;
    }

    /// <summary>
    /// 按 **GUID** 找一条厂商接口。
    ///
    /// 上游的实现（内核驱动、各家的开源工具）一律用 GUID 指认一个厂商接口，
    /// 因为那是固件里写死的东西。Windows 上那个 GUID 落成 <c>root\WMI</c> 里的一个类，
    /// **类名只是本地别名**，各机型、各驱动版本都可能不一样 ——
    /// 按类名找就等于在猜一个我们没有机器去核对的名字。
    ///
    /// 所以按 GUID 找：遍历一次这个 scope 的类定义，读每个类的 WMI <c>guid</c> 限定符。
    /// 遍历只在第一次用到时做一次。
    /// </summary>
    public static WmiMethodChannel ForGuid(string scope, string classGuid)
        => new(scope, classGuid, byGuid: true);

    /// <summary>这台机器上有没有这个类的实例。没有就是没有这条通道。</summary>
    public bool IsAvailable => Resolve() is not null;

    /// <summary>
    /// 调一次方法。
    ///
    /// 返回 null 表示**这次调用没成功**（没有这条通道、方法不认、权限不够）。
    /// 调用方拿到 null 不该当成"值是 0" —— 这两件事在风扇上差得很远。
    /// 出参怎么解读是各家协议的事，这里只把它原样交出去。
    /// </summary>
    public IReadOnlyDictionary<string, object?>? Invoke(
        string methodName,
        IReadOnlyDictionary<string, object?>? arguments = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(methodName);
        lock (gate)
        {
            if (Resolve() is not { } target)
            {
                return null;
            }
            try
            {
                using var parameters = target.GetMethodParameters(methodName);
                if (arguments is not null)
                {
                    foreach (var (name, value) in arguments)
                    {
                        parameters[name] = value;
                    }
                }
                using var result = target.InvokeMethod(methodName, parameters, null);
                if (result is null)
                {
                    return null;
                }
                var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in result.Properties)
                {
                    values[property.Name] = property.Value;
                }
                return values;
            }
            catch (Exception error) when (error is ManagementException
                or UnauthorizedAccessException
                or InvalidOperationException)
            {
                // 接口可能刚刚消失（驱动被停用、设备重新枚举）。丢掉引用，下次重新解析。
                Forget();
                return null;
            }
        }
    }

    /// <summary>
    /// 按**位置**调一次方法，拿回第一个出参。
    ///
    /// 各家 OEM 的 MOF 里参数名五花八门（<c>Arg0</c>、<c>Device_ID</c>、
    /// <c>Data</c>……），而这些接口清一色是"按顺序塞几个整数进去、拿一个整数出来"。
    /// 按名字填就得为每台机器猜一遍名字，猜错要到真机上才发现 —— 而我们手上
    /// 没有那些机器。所以这里按 WMI 自己记录的参数序号（<c>ID</c> 限定符）填，
    /// 名字是什么都不用管。
    /// </summary>
    public ulong? InvokeOrdered(string methodName, params ulong[] arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(methodName);
        ArgumentNullException.ThrowIfNull(arguments);
        lock (gate)
        {
            if (Resolve() is not { } target)
            {
                return null;
            }
            try
            {
                using var parameters = target.GetMethodParameters(methodName);
                var ordered = InParametersByPosition(parameters);
                if (ordered.Count < arguments.Length)
                {
                    // 方法收的参数比我们要给的少：协议对不上，宁可不调。
                    return null;
                }
                for (var index = 0; index < arguments.Length; index++)
                {
                    parameters[ordered[index]] = Fit(parameters, ordered[index], arguments[index]);
                }
                using var result = target.InvokeMethod(methodName, parameters, null);
                return FirstUnsignedValue(result);
            }
            catch (Exception error) when (error is ManagementException
                or UnauthorizedAccessException
                or InvalidOperationException)
            {
                Forget();
                return null;
            }
        }
    }

    /// <summary>
    /// 按 **WMI 方法 ID** 调一次方法。
    ///
    /// 有的厂商（Acer 就是）的接口在上游一律用方法 ID 指认
    /// —— 那是固件里写死的编号；Windows 上这些方法的**名字是本地别名**，
    /// 各驱动版本可能不一样。所以按 MOF 里的 <c>WmiMethodId</c> 限定符找那个方法。
    /// </summary>
    public ulong? InvokeOrderedByMethodId(int methodId, params ulong[] arguments)
        => MethodNameById(methodId) is { } name ? InvokeOrdered(name, arguments) : null;

    /// <summary>
    /// 按位置调一次方法，拿回第一个**缓冲区**出参。
    ///
    /// 有的厂商（联想的风扇表就是）不回一个整数，而是回一段字节 ——
    /// 表的内容在里面。名字照旧不猜：出参里第一个数组就是它。
    /// </summary>
    public byte[]? InvokeOrderedForBuffer(string methodName, params ulong[] arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(methodName);
        ArgumentNullException.ThrowIfNull(arguments);
        lock (gate)
        {
            if (Resolve() is not { } target)
            {
                return null;
            }
            try
            {
                using var parameters = target.GetMethodParameters(methodName);
                var ordered = InParametersByPosition(parameters);
                if (ordered.Count < arguments.Length)
                {
                    return null;
                }
                for (var index = 0; index < arguments.Length; index++)
                {
                    parameters[ordered[index]] = Fit(parameters, ordered[index], arguments[index]);
                }
                using var result = target.InvokeMethod(methodName, parameters, null);
                return FirstBufferValue(result);
            }
            catch (Exception error) when (error is ManagementException
                or UnauthorizedAccessException
                or InvalidOperationException)
            {
                Forget();
                return null;
            }
        }
    }

    /// <summary>
    /// 按位置调一次方法，其中一个入参是**缓冲区**。
    ///
    /// 缓冲区塞进第一个数组型入参，其余标量按位置填进剩下的。这样
    /// "只收一段缓冲"和"收几个标量再加一段缓冲"两种形状都成立 ——
    /// 各家的方法签名不一样，而我们手上没有那些机器去一一验证。
    ///
    /// 返回的是**调用本身有没有走通**；写进去算不算数不由它回答，那要回读核对。
    /// </summary>
    public bool InvokeOrderedWithBuffer(
        string methodName,
        byte[] payload,
        params ulong[] arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(methodName);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(arguments);
        lock (gate)
        {
            if (Resolve() is not { } target)
            {
                return false;
            }
            try
            {
                using var parameters = target.GetMethodParameters(methodName);
                string? bufferName = null;
                var scalars = new List<string>();
                foreach (var name in InParametersByPosition(parameters))
                {
                    if (bufferName is null && parameters.Properties[name].IsArray)
                    {
                        bufferName = name;
                        continue;
                    }
                    scalars.Add(name);
                }
                if (bufferName is null || scalars.Count < arguments.Length)
                {
                    // 没有缓冲区入参，或者标量位子不够：协议对不上，宁可不调。
                    return false;
                }
                for (var index = 0; index < arguments.Length; index++)
                {
                    parameters[scalars[index]] = Fit(parameters, scalars[index], arguments[index]);
                }
                parameters[bufferName] = payload;
                using var result = target.InvokeMethod(methodName, parameters, null);
                return result is not null;
            }
            catch (Exception error) when (error is ManagementException
                or UnauthorizedAccessException
                or InvalidOperationException)
            {
                Forget();
                return false;
            }
        }
    }

    /// <summary>出参里第一个字节数组。没有就是这个方法不回缓冲区。</summary>
    private static byte[]? FirstBufferValue(ManagementBaseObject? result)
    {
        if (result is null)
        {
            return null;
        }
        foreach (var property in result.Properties)
        {
            switch (property.Value)
            {
                case byte[] bytes:
                    return bytes;
                // 有的驱动把 uint8[] 报成更宽的整数数组。照样收下，逐个截回字节。
                case ushort[] words:
                    return [.. words.Select(static value => (byte)value)];
                case uint[] longs:
                    return [.. longs.Select(static value => (byte)value)];
            }
        }
        return null;
    }

    /// <summary>这个类里 <c>WmiMethodId</c> 等于这个编号的方法叫什么。找不到就是没有。</summary>
    private string? MethodNameById(int methodId)
    {
        lock (gate)
        {
            // 先把类名定下来：按 GUID 找的通道要等这一步之后才知道自己是哪个类。
            if (Resolve() is null || resolvedClassName is null)
            {
                return null;
            }
            try
            {
                using var definition = new ManagementClass(
                    new ManagementScope(scope),
                    new ManagementPath(resolvedClassName),
                    null);
                foreach (var method in definition.Methods)
                {
                    try
                    {
                        if (method.Qualifiers["WmiMethodId"]?.Value is { } id
                            && Convert.ToInt32(
                                id,
                                System.Globalization.CultureInfo.InvariantCulture) == methodId)
                        {
                            return method.Name;
                        }
                    }
                    catch (Exception error) when (error is ManagementException
                        or FormatException
                        or InvalidCastException
                        or OverflowException)
                    {
                        // 这个方法没有编号限定符，看下一个。
                    }
                }
            }
            catch (Exception error) when (error is ManagementException
                or UnauthorizedAccessException)
            {
                // 读不了类定义。当作没有这个方法。
            }
            return null;
        }
    }

    /// <summary>
    /// 造一个这个 scope 里的对象实例，用来当**嵌套入参**。
    ///
    /// 有的厂商（HP 就是）不是"塞几个整数进去"，而是塞一个结构体：
    /// 方法只收一个参数，那个参数本身是另一个 WMI 类的实例。
    /// 造实例这件事和调方法是一回事的两半，所以放在同一层。
    /// </summary>
    public ManagementObject? CreateInstance(string className)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(className);
        try
        {
            using var definition = new ManagementClass(
                new ManagementScope(scope),
                new ManagementPath(className),
                null);
            return definition.CreateInstance();
        }
        catch (Exception error) when (error is ManagementException
            or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// 调一个**只收一个结构体**的方法，拿回它的结构体出参。
    ///
    /// 同样不按名字找参数：方法只有一个入参，按序号取第一个就是它。
    /// 出参里哪个字段是什么，由各家的协议自己去读。
    /// </summary>
    public ManagementBaseObject? InvokeEmbedded(
        string methodName,
        ManagementBaseObject input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(methodName);
        ArgumentNullException.ThrowIfNull(input);
        lock (gate)
        {
            if (Resolve() is not { } target)
            {
                return null;
            }
            try
            {
                using var parameters = target.GetMethodParameters(methodName);
                var ordered = InParametersByPosition(parameters);
                if (ordered.Count == 0)
                {
                    return null;
                }
                parameters[ordered[0]] = input;
                using var result = target.InvokeMethod(methodName, parameters, null);
                return FirstEmbeddedValue(result);
            }
            catch (Exception error) when (error is ManagementException
                or UnauthorizedAccessException
                or InvalidOperationException)
            {
                Forget();
                return null;
            }
        }
    }

    private static ManagementBaseObject? FirstEmbeddedValue(ManagementBaseObject? result)
    {
        if (result is null)
        {
            return null;
        }
        foreach (var property in result.Properties)
        {
            if (property.Value is ManagementBaseObject embedded)
            {
                return embedded;
            }
        }
        return null;
    }

    /// <summary>
    /// 把一个整数装进这个参数**声明的类型**里。
    ///
    /// 各家的参数宽度不一样：ASUS 收 uint32，联想的风扇号收的是一个字节。
    /// 一律按 uint32 塞进去，宽度对不上的那家会在真机上被 WMI 拒掉 ——
    /// 而那正是我们没有机器去发现的那类错误。所以按 WMI 自己声明的类型转。
    /// 装不下就原样交出去，让 WMI 去报错，不在这里悄悄截断。
    /// </summary>
    private static object Fit(ManagementBaseObject parameters, string name, ulong value)
    {
        try
        {
            return parameters.Properties[name].Type switch
            {
                CimType.UInt8 when value <= byte.MaxValue => (byte)value,
                CimType.SInt8 when value <= (ulong)sbyte.MaxValue => (sbyte)value,
                CimType.UInt16 when value <= ushort.MaxValue => (ushort)value,
                CimType.SInt16 when value <= (ulong)short.MaxValue => (short)value,
                CimType.UInt32 when value <= uint.MaxValue => (uint)value,
                CimType.SInt32 when value <= int.MaxValue => (int)value,
                CimType.SInt64 when value <= long.MaxValue => (long)value,
                CimType.UInt64 => value,
                _ when value <= uint.MaxValue => (uint)value,
                _ => value
            };
        }
        catch (ManagementException)
        {
            return value;
        }
    }

    /// <summary>入参按 WMI 记录的序号排好。序号缺失的排在后面，顺序稳定。</summary>
    private static List<string> InParametersByPosition(ManagementBaseObject parameters)
    {
        var ordered = new List<(int Position, string Name)>();
        foreach (var property in parameters.Properties)
        {
            var position = int.MaxValue;
            try
            {
                if (property.Qualifiers["ID"]?.Value is { } id)
                {
                    position = Convert.ToInt32(id, System.Globalization.CultureInfo.InvariantCulture);
                }
            }
            catch (ManagementException)
            {
                // 没有 ID 限定符。保持 int.MaxValue，排在后面。
            }
            ordered.Add((position, property.Name));
        }
        ordered.Sort((left, right) => left.Position != right.Position
            ? left.Position.CompareTo(right.Position)
            : string.CompareOrdinal(left.Name, right.Name));
        return ordered.ConvertAll((entry) => entry.Name);
    }

    private static ulong? FirstUnsignedValue(ManagementBaseObject? result)
    {
        if (result is null)
        {
            return null;
        }
        foreach (var property in result.Properties)
        {
            if (property.Value is null)
            {
                continue;
            }
            try
            {
                return Convert.ToUInt64(
                    property.Value,
                    System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception error) when (error is FormatException
                or InvalidCastException
                or OverflowException)
            {
                // 不是个整数，看下一个。
            }
        }
        return null;
    }

    private ManagementObject? Resolve()
    {
        lock (gate)
        {
            if (probed)
            {
                return device;
            }
            probed = true;
            resolvedClassName = declaredClassName ?? FindClassByGuid();
            if (resolvedClassName is null)
            {
                return null;
            }
            try
            {
                var query = instanceName is null
                    ? $"SELECT * FROM {resolvedClassName}"
                    : $"SELECT * FROM {resolvedClassName} WHERE InstanceName = "
                        + $"'{instanceName.Replace(@"\", @"\\")}'";
                using var searcher = new ManagementObjectSearcher(scope, query);
                foreach (var found in searcher.Get())
                {
                    device = (ManagementObject)found;
                    return device;
                }
            }
            catch (Exception error) when (error is ManagementException
                or UnauthorizedAccessException)
            {
                // 没有这条通道，或者没有管理员权限。两者都归"用不了"。
            }
            return device;
        }
    }

    /// <summary>
    /// 一个 scope 里 GUID 到类名的对照表，**整个进程只建一次**。
    ///
    /// 建一次要把这个 scope 的类定义全过一遍，本机实测 892 个类、1.5 秒。
    /// 每条厂商通道各扫一遍就是好几秒，而这台机器上它们绝大多数都不在 ——
    /// 为找不到的东西花几秒，是用户能直接感觉到的那种慢。
    /// </summary>
    private static readonly Dictionary<string, IReadOnlyDictionary<string, string>> GuidMaps =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly object GuidMapGate = new();

    /// <summary>
    /// 在这个 scope 里找 WMI <c>guid</c> 限定符等于 <see cref="classGuid"/> 的那个类。
    /// 找不到就是这台机器上没有这个厂商接口。
    /// </summary>
    private string? FindClassByGuid()
        => classGuid is not null
            && GuidMapOf(scope).TryGetValue(classGuid, out var found)
                ? found
                : null;

    private static IReadOnlyDictionary<string, string> GuidMapOf(string scope)
    {
        lock (GuidMapGate)
        {
            if (GuidMaps.TryGetValue(scope, out var cached))
            {
                return cached;
            }
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    new ManagementScope(scope),
                    new WqlObjectQuery("SELECT * FROM meta_class"),
                    new System.Management.EnumerationOptions { EnumerateDeep = false });
                foreach (var found in searcher.Get())
                {
                    using var definition = found;
                    try
                    {
                        if (definition.Qualifiers["guid"]?.Value is string guid)
                        {
                            map[guid.Trim('{', '}')] = definition.ClassPath.ClassName;
                        }
                    }
                    catch (ManagementException)
                    {
                        // 这个类没有 guid 限定符 —— 不是厂商的 WMI 数据块，跳过。
                    }
                }
            }
            catch (Exception error) when (error is ManagementException
                or UnauthorizedAccessException)
            {
                // 列不了类：没有权限，或者这个 scope 根本不在。空表就是"一个都没找到"。
            }
            GuidMaps[scope] = map;
            return map;
        }
    }

    private void Forget()
    {
        lock (gate)
        {
            device?.Dispose();
            device = null;
            probed = false;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        Forget();
    }
}
