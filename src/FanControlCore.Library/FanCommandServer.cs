using System.Text.Json;
using FanControlCore.Discovery;

namespace FanControlCore;

/// <summary>Existing JSON line protocol: one hardware command and independent temperature notifications.</summary>
public static class FanCommandServer
{
    public static void Run(FanCore core, TextReader input, TextWriter output)
    {
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Task? command = null;
        string? line;
        while ((line = input.ReadLine()) is not null)
        {
            // 有的调用方第一行会带 BOM，容错掉 —— 为一个字节的编码习惯让整条通道断掉不值得。
            line = line.Trim().TrimStart('﻿', '​');
            if (line.Length == 0)
            {
                continue;
            }
            // Keep consuming temperature notifications while one hardware command is running.
            bool notification;
            try
            {
                // Only a JSON object can be a notification; anything else is a request, answered as InvalidRequest.
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                notification = root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("notification", out var flag) && flag.ValueKind == JsonValueKind.True
                    && root.TryGetProperty("op", out var op) && op.ValueKind == JsonValueKind.String && op.GetString() == "temp";
            }
            catch (JsonException) { notification = false; }
            if (notification) { Handle(core, line); continue; }
            if (command is { IsCompleted: false })
            {
                command.GetAwaiter().GetResult();
            }
            var requestLine = line;
            command = Task.Run(() =>
            {
                output.WriteLine(JsonSerializer.Serialize(Handle(core, requestLine), jsonOptions));
                output.Flush();
            });
        }
        command?.GetAwaiter().GetResult();
    }

    public static Dictionary<string, object?> Handle(FanCore core, string line)
    {
        var reply = HandleRequest(core, line, out var operation);
        if (operation is "curve" or "setcurve" or "auto" or "set" or "boost" or "group")
        {
            reply["softwareCurveRunNeeded"] = core.SoftwareCurveRunNeeded;
            reply["softwareCurveTemperatureKinds"] = core.SoftwareCurveTemperatureKinds;
        }
        return reply;
    }

    private static Dictionary<string, object?> HandleRequest(FanCore core, string line, out string? operation)
    {
        operation = null;
        long id = 0;
        try
        {
            using var document = JsonDocument.Parse(line);
            var request = document.RootElement;
            if (request.ValueKind != JsonValueKind.Object) throw new RequestException("请求得是一个 JSON 对象。");
            if (request.TryGetProperty("id", out var idElement)
                && (idElement.ValueKind != JsonValueKind.Number || !idElement.TryGetInt64(out id)))
                throw new RequestException("id 得是个整数。");
            operation = request.TryGetProperty("op", out var opElement)
                ? opElement.ValueKind == JsonValueKind.String ? opElement.GetString()
                    : throw new RequestException("op 得是个字符串。")
                : null;

            switch (operation)
            {
                case "describe":
                    var fans = core.Describe(request.TryGetProperty("refresh", out var refresh) && refresh.ValueKind == JsonValueKind.True);
                    return Ok(id, new Dictionary<string, object?>
                    {
                        ["available"] = core.ActiveBackendName is not null,
                        ["backend"] = core.ActiveBackendName,
                        ["reason"] = core.UnavailableReason,
                        ["openFailureKind"] = core.OpenFailureKind,
                        ["openFailures"] = core.OpenFailures,
                        // Normal describe returns detected capabilities. Explicit refresh
                        // runs before serializing all fields, so availability is current too.
                        ["fans"] = fans,
                        /*
                         * 这台机器的软件曲线要不要宿主推温度。
                         *
                         * 走固件曲线的机器**一次都不用推** —— 宿主据此决定要不要起
                         * 那条 0.1 秒的订阅。不报这一条的话，宿主只能对所有机器都推，
                         * 白占一条订阅和十倍的往返。
                         */
                        ["needsTemperatures"] = core.NeedsTemperatures,
                        ["crashRecoveryEnabled"] = core.CrashRecoveryEnabled,
                        // An unrestored earlier takeover rejects every write until a later explicit retry succeeds.
                        ["recoveryBlocked"] = core.RecoveryBlockedReason is not null,
                        ["recoveryBlockedReason"] = core.RecoveryBlockedReason,
                        // Recorded software plans that are kept but not executed, by fan index: Blind or Failed.
                        ["softwareCurveSuspensions"] = core.SoftwareCurveSuspensions,
                        // Fans that switch between firmware and software control only together (see the group op).
                        ["controlGroups"] = ControlGroups(core),
                        ["diagnostics"] = core.Diagnostics,
                        ["temperatureNotifications"] = true
                    });

                case "read":
                    return Ok(id, new Dictionary<string, object?> { ["fans"] = core.Read() });

                case "temp":
                {
                    /*
                     * 宿主推来的温度。**整批替换，不合并**：这是"现在多少"，
                     * 不是一条会累积的流水。
                     *
                     * 这条 op 不碰硬件，所以它很便宜 —— 0.1 秒一次也无所谓。
                     * 它也**不会自己触发调速**：调速在宿主持有的运行入口里，
                     * 推送只是把最新值放进去。两者分开，推送慢了或者停了，
                     * 引擎有自己的失效处理（读不到超过 5 秒就交还固件）。
                     */
                    var samples = new List<FanTemperatureSample>();
                    Dictionary<string, long>? observedAt = null;
                    if (request.TryGetProperty("observedAtUtcTicks", out var stamps) && stamps.ValueKind == JsonValueKind.Object)
                    {
                        observedAt = new(StringComparer.OrdinalIgnoreCase);
                        foreach (var stamp in stamps.EnumerateObject())
                            if (stamp.Value.TryGetInt64(out var ticks)) observedAt[stamp.Name] = ticks;
                    }
                    var now = DateTime.UtcNow.Ticks;
                    if (request.TryGetProperty("samples", out var pushed)
                        && pushed.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var sample in pushed.EnumerateObject())
                        {
                            if (sample.Value.ValueKind == JsonValueKind.Number)
                            {
                                var ticks = observedAt is null ? now
                                    : observedAt.TryGetValue(sample.Name, out var observed) ? observed : 0;
                                samples.Add(new(sample.Name, sample.Value.GetDouble(), ticks));
                            }
                        }
                    }
                    core.PushTemperatures(samples);
                    return Ok(id, new Dictionary<string, object?>
                    {
                        ["accepted"] = samples.Count
                    });
                }

                case "curve":
                {
                    // **单独一条 op，不塞进 read。** 读曲线要把三张表都过一遍
                    // （几十次 EC 往返），而 read 是画一次界面就要问一遍的。
                    var fan = (int)Number(request, "fan");
                    return Ok(id, new Dictionary<string, object?>
                    {
                        ["fan"] = fan,
                        ["execution"] = core.ExecutionOf(fan),
                        ["softwarePlanRecorded"] = core.SoftwarePlanRecorded(fan),
                        ["softwareCurveSuspension"] = core.SoftwareCurveSuspension(fan),
                        // 读不到就是 null，调用方看得出来。不给一条编的曲线。
                        ["curve"] = core.ReadCurve(fan)
                    });
                }

                case "set":
                {
                    var fan = (int)Number(request, "fan");
                    var percent = Number(request, "percent");
                    // appliedPercent: what the channel actually wrote after its floor and quantization (null if it does not say).
                    return core.TrySetDuty(fan, percent, out var applied, out var kind, out var reason)
                        ? Ok(id, new Dictionary<string, object?> { ["fans"] = core.Read(), ["appliedPercent"] = applied })
                        : Error(id, reason ?? "设不了。", kind, core, fan);
                }

                case "setcurve":
                {
                    var fan = (int)Number(request, "fan");
                    var points = new List<FanCurvePoint>();
                    if (request.TryGetProperty("curve", out var curve)
                        && curve.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var point in curve.EnumerateArray())
                        {
                            points.Add(new FanCurvePoint(
                                Number(point, "temperatureCelsius"),
                                Number(point, "percent")));
                        }
                    }
                    // 交给谁执行由调用方（最终是用户）选；不给就按核心的默认来。
                    var execution = request.TryGetProperty("execution", out var mode)
                        && mode.ValueKind == JsonValueKind.String
                            ? mode.GetString()
                            : null;
                    if (!core.TryWriteCurve(fan, points, execution, out var kind, out var reason))
                    {
                        return Error(id, reason ?? "写不了曲线。", kind, core, fan);
                    }
                    // 固件回执来自实际表，软件回执来自已记录的计划，不回显提交值。
                    // 两者对不上正是调用方要知道的事（阈值会被夹、回差会加上去）。
                    return Ok(id, new Dictionary<string, object?>
                    {
                        ["fan"] = fan,
                        // Report current execution separately from the recorded software plan.
                        ["execution"] = core.ExecutionOf(fan),
                        ["softwarePlanRecorded"] = core.SoftwarePlanRecorded(fan),
                        ["softwareCurveSuspension"] = core.SoftwareCurveSuspension(fan),
                        ["curve"] = core.ReadCurve(fan)
                    });
                }

                case "boost":
                {
                    var fan = (int)Number(request, "fan");
                    return core.TrySetFullSpeed(fan, out var kind, out var reason)
                        ? Ok(id, new Dictionary<string, object?> { ["fans"] = core.Read() })
                        : Error(id, reason ?? "开不了全速。", kind, core, fan);
                }

                case "auto":
                {
                    var fan = (int)Number(request, "fan");
                    var resetFirmwareCurve = request.TryGetProperty("resetFirmwareCurve", out var resetCurve)
                        && resetCurve.ValueKind == JsonValueKind.True;
                    return core.TrySetAutomatic(fan, resetFirmwareCurve, out var kind, out var reason)
                        ? Ok(id, new Dictionary<string, object?> { ["fans"] = core.Read(), ["firmwareCurveReset"] = resetFirmwareCurve })
                        : Error(id, reason ?? "交还不了。", kind, core, fan);
                }

                case "group":
                {
                    /*
                     * 整组切换固件控制 / 软件控制，一次调用完成。宿主在界面上暂存，用户应用时才发这一条。
                     * 切到软件要给组里每一把风扇一项：{"fan":0,"percent":40} 或 {"fan":1,"curve":[...]}。
                     */
                    var groupId = request.TryGetProperty("group", out var groupElement) && groupElement.ValueKind == JsonValueKind.String
                        ? groupElement.GetString()! : throw new RequestException("这条请求缺少 group。");
                    // From here on every rejection, including a malformed rest of the request, names this group.
                    try { return HandleGroup(core, request, id, groupId); }
                    catch (RequestException error) { return GroupError(id, error.Message, FanCommandErrorKind.InvalidRequest, groupId); }
                    catch (Exception error)
                    {
                        // FanCore reports hand-back faults with handedBack itself, and nothing after the switch throws (fans are read
                        // guarded), so this is a failure before the switch ran: the group is named and handedBack is exactly empty.
                        return GroupError(id, error.Message, FanCommandErrorKind.Other, groupId);
                    }
                }

                default:
                    return Error(id, $"不认识的操作：{operation}", FanCommandErrorKind.InvalidRequest);
            }
        }
        catch (Exception error) when (error is JsonException or RequestException)
        {
            return Error(id, error.Message, FanCommandErrorKind.InvalidRequest);
        }
        catch (Exception error)
        {
            // 一条坏请求不该把整条通道打断。如实回过去，继续听下一条。
            return Error(id, error.Message, FanCommandErrorKind.Other);
        }
    }

    /// <summary>
    /// The group op after its id is known. Every reply names the group and lists <c>handedBack</c>: members this call
    /// put back under firmware control, also on a rejection (hand-back is not rolled back; the rule for a shared switch
    /// that counts holders is on FanCore.TrySetGroupControl).
    /// A rejection after any hardware step also carries the resulting <c>fans</c>, so the host can refresh what it shows as applied;
    /// <c>fans</c> is null when the fans cannot be read after the switch, whose result stands either way.
    /// </summary>
    private static Dictionary<string, object?> HandleGroup(FanCore core, JsonElement request, long id, string groupId)
    {
        // describe prints the member names (Software / Firmware); matching ignores case, so "software" / "firmware" work too.
        var control = (request.TryGetProperty("control", out var controlElement) && controlElement.ValueKind == JsonValueKind.String
                ? controlElement.GetString()?.ToLowerInvariant() : null) switch
            {
                "software" => FanGroupControl.Software,
                "firmware" => FanGroupControl.Firmware,
                _ => throw new RequestException("control 得是 software 或 firmware。")
            };
        var assignments = new List<FanGroupAssignment>();
        if (request.TryGetProperty("fans", out var members))
        {
            if (members.ValueKind != JsonValueKind.Array) throw new RequestException("fans 得是个数组。");
            foreach (var member in members.EnumerateArray())
            {
                var memberFan = Number(member, "fan");
                if (memberFan != Math.Floor(memberFan) || memberFan is < int.MinValue or > int.MaxValue)
                    throw new RequestException("fan 得是个整数。");
                double? percent = member.TryGetProperty("percent", out _) ? Number(member, "percent") : null;
                List<FanCurvePoint>? points = null;
                if (member.TryGetProperty("curve", out var memberCurve))
                {
                    if (memberCurve.ValueKind != JsonValueKind.Array) throw new RequestException("curve 得是个数组。");
                    points = memberCurve.EnumerateArray()
                        .Select(point => new FanCurvePoint(Number(point, "temperatureCelsius"), Number(point, "percent"))).ToList();
                }
                assignments.Add(new((int)memberFan, percent, points));
            }
        }
        if (core.TrySetGroupControl(groupId, control, assignments, out var handedBack, out var kind, out var reason))
        {
            var applied = new Dictionary<string, object?> { ["controlGroups"] = ControlGroups(core), ["handedBack"] = handedBack };
            applied["fans"] = ReadOrNull(core);
            return Ok(id, applied);
        }
        var reply = GroupError(id, reason ?? "切换不了控制组。", kind, groupId);
        reply["handedBack"] = handedBack;
        if (core.ActiveBackendName is not null) reply["fans"] = ReadOrNull(core);
        return reply;
    }

    /// <summary>The fans after a group switch, or null when they cannot be read: the switch result stands either way.</summary>
    private static IReadOnlyList<FanState>? ReadOrNull(FanCore core)
    {
        try { return core.Read(); }
        catch (Exception) { return null; }
    }

    /// <summary>A request field is missing or has the wrong JSON type. Reported as InvalidRequest.</summary>
    private sealed class RequestException(string message) : Exception(message);

    static double Number(JsonElement request, string name)
    {
        if (request.ValueKind != JsonValueKind.Object || !request.TryGetProperty(name, out var element))
            throw new RequestException($"这条请求缺少 {name}。");
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetDouble(out var value))
            throw new RequestException($"{name} 得是个数。");
        return value;
    }

    static Dictionary<string, object?> Ok(long id, IReadOnlyDictionary<string, object?> payload)
    {
        var result = new Dictionary<string, object?> { ["id"] = id, ["ok"] = true };
        foreach (var (key, value) in payload)
        {
            result[key] = value;
        }
        return result;
    }

    /// <summary>Every rejected command carries its original text and its typed kind (see <see cref="FanCommandErrorKind"/>).</summary>
    static Dictionary<string, object?> Error(long id, string message, FanCommandErrorKind kind)
        => new() { ["id"] = id, ["ok"] = false, ["error"] = message, ["errorKind"] = kind };

    /// <summary>A per-fan rejection; a ControlGroupSwitchRequired one also names the group to switch.</summary>
    static Dictionary<string, object?> Error(long id, string message, FanCommandErrorKind kind, FanCore core, int fan)
    {
        var reply = Error(id, message, kind);
        if (kind == FanCommandErrorKind.ControlGroupSwitchRequired) reply["controlGroup"] = core.ControlGroupOf(fan)?.Id;
        return reply;
    }

    /// <summary>
    /// A rejected group switch names the group it was for and lists <c>handedBack</c>: empty here, which is exact for a
    /// rejection before the switch ran; <see cref="HandleGroup"/> replaces it with the switch's own list.
    /// </summary>
    static Dictionary<string, object?> GroupError(long id, string message, FanCommandErrorKind kind, string groupId)
    {
        var reply = Error(id, message, kind);
        reply["controlGroup"] = groupId;
        reply["handedBack"] = Array.Empty<int>();
        return reply;
    }

    /// <summary>Each group with its members, reason and the control mode last applied to it.</summary>
    static List<Dictionary<string, object?>> ControlGroups(FanCore core)
        => core.ControlGroups.Select(group => new Dictionary<string, object?>
        {
            ["id"] = group.Id,
            ["fans"] = group.Fans,
            ["reason"] = group.Reason,
            ["firmwareFixedDuty"] = group.FirmwareFixedDuty,
            ["control"] = core.GroupControl(group.Id)
        }).ToList();
}
