using System.Text.Json;
using FanControlCore.Discovery;

namespace FanControlCore;

/// <summary>Direct command boundary shared with the standalone JSON line host.</summary>
public sealed class FanControlSession : IDisposable
{
    private readonly FanCore core;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public FanControlSession(FanCoreOptions options) : this(new FanCore(options)) { }

    /// <summary>An explicit backend/core is useful for hosts with their own discovery and for isolated tests.</summary>
    public FanControlSession(FanCore core)
    {
        this.core = core;
        try { core.Open(); }
        catch { core.Dispose(); throw; }
    }

    public JsonElement Handle(JsonElement request)
        => JsonSerializer.SerializeToElement(FanCommandServer.Handle(core, request.GetRawText()), Json);

    public void SetDiagnosticSink(Action<string> sink) => core.DiagnosticSink = sink;
    public string? InFlightOperation => core.InFlightOperation;
    public void Dispose() => core.Dispose();
}
