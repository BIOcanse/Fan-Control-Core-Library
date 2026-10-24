using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FanControlCore.Profiles;

namespace FanControlCore.Protocols;

public sealed partial class UniwillWmiFanBackend
{
    internal const string BaselineUnavailable = "无法读取或保存风扇曲线恢复基线，未继续调整。";
    internal const string RestoreUnconfirmed = "固件模式、表布局或回读不符，风扇曲线尚未恢复。";
    private const int TableBytes = UniwillFanTable.ZoneCount * 3;
    private static readonly JsonSerializerOptions BaselineJson = new()
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    private sealed record FanTables([property: JsonRequired] byte[]? CurveOriginal,
        [property: JsonRequired] byte[]? DutyOriginal, [property: JsonRequired] bool CurveApplied);
    private sealed record TableBaselines([property: JsonRequired] int Version,
        [property: JsonRequired] string Identity, [property: JsonRequired] bool CustomTableEnabled,
        [property: JsonRequired] Dictionary<int, FanTables> Fans);

    public bool TryDriveDuty(int fanIndex, double percent, out string? failureReason)
    { lock (tableGate) return TryDriveDutyCore(fanIndex, percent, out failureReason); }
    public bool TrySetDuty(int fanIndex, double percent, out string? failureReason)
    { lock (tableGate) return TrySetDutyCore(fanIndex, percent, out failureReason); }
    public bool TrySetFullSpeed(int fanIndex, out string? failureReason)
    { lock (tableGate) return TrySetFullSpeedCore(fanIndex, out failureReason); }
    public bool TryWriteCurve(int fanIndex, IReadOnlyList<FanCurvePoint> curve, out string? failureReason)
    { lock (tableGate) return TryWriteCurveCore(fanIndex, curve, out failureReason); }
    public bool TrySetAutomatic(int fanIndex, out string? failureReason)
    { lock (tableGate) return TrySetAutomaticCore(fanIndex, out failureReason); }
    public bool TryReleaseOverride(int fanIndex, out string? failureReason)
        => TrySetAutomatic(fanIndex, out failureReason);
    public bool HasPendingTemporaryRestore(int fanIndex)
    {
        lock (tableGate)
        {
            try
            {
                return BaselinePath() is { } path && LoadBaseline(path)?.Fans.GetValueOrDefault(fanIndex) is { } fan
                    && (fan.DutyOriginal is not null || fan.CurveOriginal is not null && !fan.CurveApplied);
            }
            catch (Exception error) when (IsBaselineError(error)) { return false; }
        }
    }
    public bool TryResetFirmwareCurve(int fanIndex, out string? failureReason)
    {
        lock (tableGate)
        {
            if (!TryResolveDutyAddress(fanIndex, out _, out failureReason)) return false;
            return TryRestoreOwnedTable(fanIndex, resetCurve: true, out failureReason);
        }
    }

    private string? BaselinePath()
    {
        if (string.IsNullOrWhiteSpace(baselineDirectory) || string.IsNullOrWhiteSpace(identity.Value)) return null;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.Value)));
        return Path.Combine(Path.GetFullPath(baselineDirectory), "uniwill-fans-" + key + ".json");
    }

    private TableBaselines? LoadBaseline(string path)
    {
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 16_384) throw new InvalidDataException("Fan baseline is too large.");
        var saved = JsonSerializer.Deserialize<TableBaselines>(File.ReadAllText(path), BaselineJson);
        if (saved is null || saved.Version != 1 || saved.Identity != identity.Value || saved.Fans is null
            || saved.Fans.Count is < 1 or > 2 || saved.Fans.Any(pair => pair.Key is not (0 or 1)
                || pair.Value is null || pair.Value.CurveOriginal is null && pair.Value.DutyOriginal is null
                || pair.Value.CurveApplied && pair.Value.CurveOriginal is null
                || pair.Value.CurveOriginal is { Length: not TableBytes }
                || pair.Value.DutyOriginal is { Length: not TableBytes }))
            throw new InvalidDataException("Invalid fan baseline.");
        return saved;
    }

    private static void SaveBaseline(string path, TableBaselines saved)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, saved, BaselineJson);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }

    private byte[]? ReadRawTable(int fanIndex)
    {
        var (end, start, speed) = UniwillFanTable.AddressesFor(fanIndex);
        byte[] table = new byte[TableBytes];
        for (var zone = 0; zone < UniwillFanTable.ZoneCount; zone++)
        {
            if (transport.Read((ushort)(end + zone)) is not { } endValue
                || transport.Read((ushort)(start + zone)) is not { } startValue
                || transport.Read((ushort)(speed + zone)) is not { } speedValue) return null;
            table[zone] = endValue;
            table[UniwillFanTable.ZoneCount + zone] = startValue;
            table[UniwillFanTable.ZoneCount * 2 + zone] = speedValue;
        }
        return table;
    }

    private bool TryPrepareTable(int fanIndex, bool curve, out string? failureReason)
    {
        failureReason = BaselineUnavailable;
        try
        {
            if (BaselinePath() is not { } path) return false;
            var saved = LoadBaseline(path);
            if (saved is null)
            {
                if (transport.Read(UniwillFanTable.UseCustomTablesRegister) is not { } enabled) return false;
                saved = new(1, identity.Value!, (enabled & UniwillFanTable.UseCustomTablesBit) != 0, []);
            }
            saved.Fans.TryGetValue(fanIndex, out var fan);
            fan ??= new(null, null, false);
            if (curve && fan.CurveOriginal is null)
            {
                var original = fan.DutyOriginal ?? ReadRawTable(fanIndex);
                if (original is null) return false;
                fan = fan with { CurveOriginal = original };
            }
            if (fan.DutyOriginal is null && (!curve || fan.CurveApplied))
            {
                var original = !fan.CurveApplied && fan.CurveOriginal is not null
                    ? fan.CurveOriginal : ReadRawTable(fanIndex);
                if (original is null) return false;
                fan = fan with { DutyOriginal = original };
            }
            var fans = new Dictionary<int, FanTables>(saved.Fans) { [fanIndex] = fan };
            SaveBaseline(path, saved with { Fans = fans });
            failureReason = null;
            return true;
        }
        catch (Exception error) when (IsBaselineError(error)) { return false; }
    }

    private bool TryCompleteCurve(int fanIndex, out string? failureReason)
    {
        failureReason = BaselineUnavailable;
        try
        {
            if (BaselinePath() is not { } path || LoadBaseline(path) is not { } saved
                || !saved.Fans.TryGetValue(fanIndex, out var fan) || fan.CurveOriginal is null) return false;
            if (fan.DutyOriginal is not null || !fan.CurveApplied)
            {
                var fans = new Dictionary<int, FanTables>(saved.Fans)
                { [fanIndex] = fan with { DutyOriginal = null, CurveApplied = true } };
                SaveBaseline(path, saved with { Fans = fans });
            }
            failureReason = null;
            return true;
        }
        catch (Exception error) when (IsBaselineError(error)) { return false; }
    }

    private bool TryRestoreOwnedTable(int fanIndex, bool resetCurve, out string? failureReason)
    {
        failureReason = BaselineUnavailable;
        try
        {
            var path = BaselinePath();
            var saved = path is null ? null : LoadBaseline(path);
            FanTables? fan = null;
            saved?.Fans.TryGetValue(fanIndex, out fan);
            var original = resetCurve ? fan?.CurveOriginal : fan?.DutyOriginal
                ?? (fan is { CurveApplied: false } ? fan.CurveOriginal : null);
            if (resetCurve && original is null) return false;
            if (original is null) return ClearFullFanMode(out failureReason);
            failureReason = RestoreUnconfirmed;
            var (end, start, speed) = UniwillFanTable.AddressesFor(fanIndex);
            // Restore complete raw bytes, without percentage conversion or synthesized defaults.
            for (var zone = 0; zone < UniwillFanTable.ZoneCount; zone++)
                if (!transport.WriteWithRetry((ushort)(speed + zone), original[UniwillFanTable.ZoneCount * 2 + zone])) return false;
            for (var zone = 0; zone < UniwillFanTable.ZoneCount; zone++)
                if (!transport.WriteWithRetry((ushort)(start + zone), original[UniwillFanTable.ZoneCount + zone])
                    || !transport.WriteWithRetry((ushort)(end + zone), original[zone])) return false;
            if (ReadRawTable(fanIndex) is not { } restored || !restored.SequenceEqual(original)
                || !ClearFullFanMode(out failureReason)) return false;
            failureReason = RestoreUnconfirmed;
            var fans = new Dictionary<int, FanTables>(saved!.Fans);
            var next = resetCurve || !fan!.CurveApplied ? new FanTables(null, null, false)
                : fan with { DutyOriginal = null };
            if (next.CurveOriginal is null && next.DutyOriginal is null) fans.Remove(fanIndex);
            else fans[fanIndex] = next;
            if (fans.Count > 0) SaveBaseline(path!, saved with { Fans = fans });
            else
            {
                if (!transport.WriteBit(UniwillFanTable.UseCustomTablesRegister, UniwillFanTable.UseCustomTablesBit,
                    saved.CustomTableEnabled)
                    || transport.Read(UniwillFanTable.UseCustomTablesRegister) is not { } enabled
                    || ((enabled & UniwillFanTable.UseCustomTablesBit) != 0) != saved.CustomTableEnabled) return false;
                File.Delete(path!);
            }
            failureReason = null;
            return true;
        }
        catch (Exception error) when (IsBaselineError(error)) { return false; }
    }

    private static bool IsBaselineError(Exception error)
        => error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException;
}
