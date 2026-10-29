using System.Text.Json.Serialization;

namespace FanControlCore;

/// <summary>Why these fans can only switch between firmware and software control together.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FanControlGroupReason>))]
public enum FanControlGroupReason
{
    /// <summary>
    /// Uniwill / Tongfang: bit 0x40 of EC register 0x0751 takes every fan away from the firmware tables at once.
    /// </summary>
    UniwillSharedModeBit,
    /// <summary>Acer: custom fan mode is one machine-wide WMI mode; automatic returns every fan.</summary>
    AcerMachineMode
}

/// <summary>Who controls a group of fans that share one control switch.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FanGroupControl>))]
public enum FanGroupControl
{
    /// <summary>Firmware runs the fans: its own tables, or a firmware curve or fixed-duty table written per fan.</summary>
    Firmware,
    /// <summary>The software engine drives every fan of the group; each fan has its own software curve or fixed speed.</summary>
    Software
}

/// <summary>
/// Fans that share one control switch: switching between firmware and software control is only possible for the whole
/// group at once (<c>FanCore.TrySetGroupControl</c>). A fan with no shared relation is in no group.
/// </summary>
/// <param name="Id">Stable group key for requests, for example <c>uniwill-shared-mode</c>.</param>
/// <param name="Fans">Member fan indexes, ascending.</param>
/// <param name="Reason">The shared switch that makes them a group.</param>
/// <param name="FirmwareFixedDuty">
/// True when a per-fan fixed duty runs under firmware control (Uniwill writes it as that fan's firmware table, so
/// <c>set</c> works per fan while the group is under firmware control). False when a fixed duty is itself the
/// shared switch (Acer custom mode), so it needs the group under software control like a software curve.
/// </param>
public sealed record FanControlGroup(string Id, IReadOnlyList<int> Fans, FanControlGroupReason Reason,
    bool FirmwareFixedDuty);

/// <summary>
/// One member's setting when a group switches to software control: exactly one of a fixed
/// <paramref name="Percent"/> or a software <paramref name="Curve"/>.
/// </summary>
public sealed record FanGroupAssignment(int FanIndex, double? Percent = null, IReadOnlyList<FanCurvePoint>? Curve = null);
