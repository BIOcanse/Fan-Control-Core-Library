namespace FanControlCore;

/// <summary>A host-observed temperature with its original UTC observation time.</summary>
public readonly record struct FanTemperatureSample(string Name, double Celsius, long ObservedAtUtcTicks);

/// <summary>Temperature needs of recorded software plans.</summary>
public static class FanTemperatureKinds
{
    public const string Cpu = "cpu";
    public const string Gpu = "gpu";
    public const string Any = "any";
}
