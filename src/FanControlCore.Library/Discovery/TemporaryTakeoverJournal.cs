using System.Text.Json;
using System.Text.Json.Serialization;

namespace FanControlCore.Discovery;

/// <summary>Write-ahead temporary ownership in the host's existing state directory. No curve values.</summary>
internal sealed class TemporaryTakeoverJournal(FanCoreOptions options) : IDisposable
{
    private sealed record Entry(string Backend, string Identity);
    private sealed record State(
        [property: JsonRequired] int Version,
        [property: JsonRequired] string Machine,
        [property: JsonRequired] Entry[] Fans);
    private static readonly JsonSerializerOptions Json = new()
        { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private readonly object gate = new();
    private FileStream? ownership;
    private State? state;
    private string PathName => Path.Combine(Path.GetFullPath(options.StateDirectory!), "temporary-takeovers.json");

    private void Load()
    {
        if (state is not null) return;
        var machine = options.ReadMachineIdentity();
        if (string.IsNullOrWhiteSpace(machine)) throw new InvalidDataException("A stable machine identity is required for fan recovery.");
        Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
        ownership ??= new FileStream(PathName + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (!File.Exists(PathName)) { state = new(1, machine, []); return; }
        if (new FileInfo(PathName).Length > 65536) throw new InvalidDataException("Fan takeover state exceeds its bound.");
        var saved = JsonSerializer.Deserialize<State>(File.ReadAllText(PathName), Json);
        if (saved is null || saved.Version != 1 || saved.Machine != machine || saved.Fans is null
            || saved.Fans.Length > 256 || saved.Fans.Any(fan => fan is null
                || string.IsNullOrWhiteSpace(fan.Backend) || string.IsNullOrWhiteSpace(fan.Identity))
            || saved.Fans.Distinct().Count() != saved.Fans.Length)
            throw new InvalidDataException("Fan takeover state is invalid or belongs to a different machine.");
        state = saved;
    }

    private void Save(State next)
    {
        var temporary = PathName + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, next, Json);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, PathName, overwrite: true);
        state = next;
    }

    public bool TryRecover(IFanBackend backend, out string? failure)
    {
        lock (gate)
        {
            failure = null;
            try
            {
                // No previous ownership: discovery stays read-only and does not require an identity.
                if (!File.Exists(PathName)) return true;
                Load();
                var descriptions = backend.Describe();
                var targets = new List<(Entry Entry, int Index)>();
                foreach (var entry in state!.Fans)
                {
                    var matches = descriptions.Where(fan => backend.RecoveryIdentity(fan.Index) == entry.Identity
                        || LegacyIdentity(fan) == entry.Identity).ToArray();
                    if (entry.Backend != backend.Name || matches.Length != 1 || !matches[0].Writable)
                        throw new InvalidDataException("Pending fan takeover does not match the detected writable hardware.");
                    targets.Add((entry, matches[0].Index));
                }
                // Validate every target before the first restore. Never guess a fan from its old index.
                foreach (var target in targets)
                {
                    if (!backend.TryReleaseOverride(target.Index, out var reason))
                        throw new IOException(reason ?? "Temporary fan takeover restoration was not confirmed.");
                    Save(state with { Fans = state.Fans.Where(entry => entry != target.Entry).ToArray() });
                }
                return true;
            }
            catch (Exception error) { failure = "Fan takeover recovery blocked: " + error.Message; return false; }
        }
    }

    public bool TryRecord(IFanBackend backend, IEnumerable<int> fans, out string? failure)
    {
        lock (gate)
        {
            failure = null;
            try
            {
                Load();
                var next = state!.Fans.ToList();
                foreach (var index in fans)
                {
                    var identity = backend.RecoveryIdentity(index);
                    if (string.IsNullOrWhiteSpace(identity)
                        || backend.Describe().Count(fan => backend.RecoveryIdentity(fan.Index) == identity) != 1)
                        throw new InvalidDataException("A unique stable fan identity is required before takeover.");
                    var entry = new Entry(backend.Name, identity);
                    if (!next.Contains(entry)) next.Add(entry);
                }
                if (next.Count > 256) throw new InvalidDataException("Too many temporary fan takeovers.");
                if (!next.SequenceEqual(state.Fans)) Save(state with { Fans = next.ToArray() });
                return true;
            }
            catch (Exception error) { failure = "Fan takeover was not recorded: " + error.Message; return false; }
        }
    }

    /*
     * Compatibility, delete condition: remove LegacyIdentity once no host can still hold a journal written before
     * backends overrode RecoveryIdentity with a hardware channel key (0.1.0-dev.7). Such records
     * use the former default "name\nrole\nfirmwareCurve" for every backend; for backends that keep the default it is
     * the current identity anyway. Pending records are resolved by the first Open of a newer session, so one
     * successful session per installation retires them.
     */
    private static string LegacyIdentity(FanDescription fan) => fan.Name + "\n" + fan.Role + "\n" + fan.FirmwareCurve;

    public void Released(IFanBackend backend, int index)
    {
        lock (gate)
        {
            if (state is null) return;
            var identity = backend.RecoveryIdentity(index);
            var next = state.Fans.Where(entry => entry.Backend != backend.Name || entry.Identity != identity).ToArray();
            if (next.Length != state.Fans.Length) Save(state with { Fans = next });
        }
    }

    public void Dispose() => ownership?.Dispose();
}
