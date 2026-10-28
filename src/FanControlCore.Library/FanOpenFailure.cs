using System.Text.Json.Serialization;

namespace FanControlCore;

/// <summary>Only explicit discovery evidence may identify a recognised failure.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FanOpenFailureKind>))]
public enum FanOpenFailureKind
{
    Other = 0,
    NoSupportedController,
    DriverMissing,
    AccessDenied
}

/// <summary>One failed candidate's identity, typed cause and original diagnostic text.</summary>
public sealed record FanOpenFailure(string Backend, FanOpenFailureKind Kind, string Reason);
