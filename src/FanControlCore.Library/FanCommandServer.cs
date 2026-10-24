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
                using var document = JsonDocument.Parse(line);
                notification = document.RootElement.TryGetProperty("notification", out var flag) && flag.ValueKind == JsonValueKind.True
                    && document.RootElement.TryGetProperty("op", out var op) && op.GetString() == "temp";
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
        long id = 0;
        try
        {
            using var document = JsonDocument.Parse(line);
            var request = document.RootElement;
            id = request.TryGetProperty("id", out var idElement) ? idElement.GetInt64() : 0;
            var operation = request.TryGetProperty("op", out var opElement)
                ? opElement.GetString()
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
                     * 它也**不会自己触发调速**：调速在引擎自己的循环里，
                     * 推送只是把最新值放进去。两者分开，推送慢了或者停了，
                     * 引擎有自己的失效处理（读不到超过 5 秒就交还固件）。
                     */
                    var samples = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                    if (request.TryGetProperty("samples", out var pushed)
                        && pushed.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var sample in pushed.EnumerateObject())
                        {
                            if (sample.Value.ValueKind == JsonValueKind.Number)
                            {
                                samples[sample.Name] = sample.Value.GetDouble();
                            }
                        }
                    }
                    Dictionary<string, long>? observedAt = null;
                    if (request.TryGetProperty("observedAtUtcTicks", out var stamps) && stamps.ValueKind == JsonValueKind.Object)
                    {
                        observedAt = new(StringComparer.OrdinalIgnoreCase);
                        foreach (var stamp in stamps.EnumerateObject())
                            if (stamp.Value.TryGetInt64(out var ticks)) observedAt[stamp.Name] = ticks;
                    }
                    core.Temperatures.Push(samples, observedAt);
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
                        // 读不到就是 null，调用方看得出来。不给一条编的曲线。
                        ["curve"] = core.ReadCurve(fan)
                    });
                }

                case "set":
                {
                    var fan = (int)Number(request, "fan");
                    var percent = Number(request, "percent");
                    return core.TrySetDuty(fan, percent, out var reason)
                        ? Ok(id, new Dictionary<string, object?> { ["fans"] = core.Read() })
                        : Error(id, reason ?? "设不了。");
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
                                point.GetProperty("temperatureCelsius").GetDouble(),
                                point.GetProperty("percent").GetDouble()));
                        }
                    }
                    // 交给谁执行由调用方（最终是用户）选；不给就按核心的默认来。
                    var execution = request.TryGetProperty("execution", out var mode)
                        && mode.ValueKind == JsonValueKind.String
                            ? mode.GetString()
                            : null;
                    if (!core.TryWriteCurve(fan, points, execution, out var reason))
                    {
                        return Error(id, reason ?? "写不了曲线。");
                    }
                    // 固件回执来自实际表，软件回执来自活跃执行计划，不回显提交值。
                    // 两者对不上正是调用方要知道的事（阈值会被夹、回差会加上去）。
                    return Ok(id, new Dictionary<string, object?>
                    {
                        ["fan"] = fan,
                        // 实际走的是哪条。用户选了固件而这台机器只有软件时我们会拒绝，
                        // 所以这里回的一定就是他选的那条 —— 回出来是为了让界面对得上账。
                        ["execution"] = core.ExecutionOf(fan),
                        ["curve"] = core.ReadCurve(fan)
                    });
                }

                case "boost":
                {
                    var fan = (int)Number(request, "fan");
                    return core.TrySetFullSpeed(fan, out var reason)
                        ? Ok(id, new Dictionary<string, object?> { ["fans"] = core.Read() })
                        : Error(id, reason ?? "开不了全速。");
                }

                case "auto":
                {
                    var fan = (int)Number(request, "fan");
                    var resetFirmwareCurve = request.TryGetProperty("resetFirmwareCurve", out var resetCurve)
                        && resetCurve.ValueKind == JsonValueKind.True;
                    return core.TrySetAutomatic(fan, out var reason, resetFirmwareCurve)
                        ? Ok(id, new Dictionary<string, object?> { ["fans"] = core.Read(), ["firmwareCurveReset"] = resetFirmwareCurve })
                        : Error(id, reason ?? "交还不了。");
                }

                default:
                    return Error(id, $"不认识的操作：{operation}");
            }
        }
        catch (Exception error)
        {
            // 一条坏请求不该把整条通道打断。如实回过去，继续听下一条。
            return Error(id, error.Message);
        }
    }

    static double Number(JsonElement request, string name)
        => request.TryGetProperty(name, out var element)
            ? element.GetDouble()
            : throw new ArgumentException($"这条请求缺少 {name}。");

    static Dictionary<string, object?> Ok(long id, IReadOnlyDictionary<string, object?> payload)
    {
        var result = new Dictionary<string, object?> { ["id"] = id, ["ok"] = true };
        foreach (var (key, value) in payload)
        {
            result[key] = value;
        }
        return result;
    }

    static Dictionary<string, object?> Error(long id, string message)
        => new() { ["id"] = id, ["ok"] = false, ["error"] = message };
}
