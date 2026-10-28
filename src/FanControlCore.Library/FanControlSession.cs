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

    public void PushTemperatures(IReadOnlyList<FanTemperatureSample> samples) => core.PushTemperatures(samples);

    /// <summary>Execute recorded software plans until the host cancels; concurrent runs are rejected.</summary>
    public Task RunSoftwareCurveAsync(CancellationToken stop) => core.RunSoftwareCurveAsync(stop);
    public bool SoftwareCurveRunNeeded => core.SoftwareCurveRunNeeded;
    public event Action<bool>? SoftwareCurveRunNeededChanged
    {
        add => core.SoftwareCurveRunNeededChanged += value;
        remove => core.SoftwareCurveRunNeededChanged -= value;
    }
    public IReadOnlyList<string> SoftwareCurveTemperatureKinds => core.SoftwareCurveTemperatureKinds;
    public event Action<IReadOnlyList<string>>? SoftwareCurveTemperatureKindsChanged
    {
        add => core.SoftwareCurveTemperatureKindsChanged += value;
        remove => core.SoftwareCurveTemperatureKindsChanged -= value;
    }

    public void SetDiagnosticSink(Action<string> sink) => core.DiagnosticSink = sink;
    public FanOpenFailureKind? OpenFailureKind => core.OpenFailureKind;
    public IReadOnlyList<FanOpenFailure> OpenFailures => core.OpenFailures;
    public string? InFlightOperation => core.InFlightOperation;
    public void Dispose() => core.Dispose();
}
