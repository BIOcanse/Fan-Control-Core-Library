using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FanControlCore.Protocols;

public sealed partial class LenovoWmiFanBackend
{
    internal const string BaselineUnavailable = "无法读取或保存风扇曲线恢复基线，未继续调整。";
    internal const string RestoreUnconfirmed = "固件模式、表布局或回读不符，风扇曲线尚未恢复。";
    private static readonly JsonSerializerOptions BaselineJson = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private sealed record TableBaseline(
        [property: JsonRequired] int Version,
        [property: JsonRequired] string Identity,
        [property: JsonRequired] uint FanId,
        [property: JsonRequired] byte[] Table);

    private static bool IsValidTable(byte[] buffer)
    {
        if (buffer.Length < TableValueOffset + ((TablePointCount - 1) * TableValueStride) + 1
            || buffer.Length > 4096) return false;
        for (var point = 0; point < TablePointCount; point++)
            if (buffer[TableValueOffset + (point * TableValueStride)] > MaximumLevel) return false;
        return true;
    }

    private string? BaselinePath(uint fanId)
    {
        if (string.IsNullOrWhiteSpace(baselineDirectory) || string.IsNullOrWhiteSpace(hardware.Identity)) return null;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hardware.Identity + "\n" + fanId)));
        return Path.Combine(Path.GetFullPath(baselineDirectory), "lenovo-" + key + ".json");
    }

    private TableBaseline? LoadBaseline(string path, uint fanId)
    {
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 16_384) throw new InvalidDataException("Fan baseline is too large.");
        var saved = JsonSerializer.Deserialize<TableBaseline>(File.ReadAllText(path), BaselineJson);
        if (saved is null || saved.Version != 1 || saved.Identity != hardware.Identity || saved.FanId != fanId
            || saved.Table is null || !IsValidTable(saved.Table) || saved.Table[0] != CustomPowerMode)
            throw new InvalidDataException("Invalid fan baseline.");
        return saved;
    }

    private bool TryCaptureBaseline(uint fanId, byte[] original, out string? failureReason)
    {
        failureReason = BaselineUnavailable;
        try
        {
            if (BaselinePath(fanId) is not { } path) return false;
            if (LoadBaseline(path, fanId) is { } saved)
            {
                if (saved.Table.Length != original.Length || saved.Table[0] != original[0]) return false;
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temporary = path + ".tmp";
                using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, new TableBaseline(1, hardware.Identity!, fanId, [.. original]), BaselineJson);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, path, overwrite: false);
            }
            failureReason = null;
            return true;
        }
        catch (Exception error) when (IsBaselineError(error)) { return false; }
    }

    private bool TryRestoreTable(uint fanId, out string? failureReason)
    {
        failureReason = BaselineUnavailable;
        try
        {
            var path = BaselinePath(fanId);
            var saved = path is null ? null : LoadBaseline(path, fanId);
            if (saved is null) return false;
            failureReason = RestoreUnconfirmed;
            if (ReadTableBuffer(fanId) is not { } current || !IsValidTable(current)
                || current.Length != saved.Table.Length || current[0] != saved.Table[0]) return false;
            if (!current.SequenceEqual(saved.Table)
                && (!hardware.WriteTable(fanId, [.. saved.Table])
                    || ReadTableBuffer(fanId) is not { } restored || !restored.SequenceEqual(saved.Table))) return false;
            failureReason = null;
            return true;
        }
        catch (Exception error) when (IsBaselineError(error)) { return false; }
    }

    private bool TryRemoveBaseline(uint fanId, out string? failureReason)
    {
        failureReason = BaselineUnavailable;
        try
        {
            if (BaselinePath(fanId) is { } path) File.Delete(path);
            failureReason = null;
            return true;
        }
        catch (Exception error) when (IsBaselineError(error)) { return false; }
    }

    private static bool IsBaselineError(Exception error)
        => error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException or NotSupportedException;
}
