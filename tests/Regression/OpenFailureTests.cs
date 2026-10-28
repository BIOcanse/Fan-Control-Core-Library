using System.Text.Json;
using FanControlCore;
using FanControlCore.Discovery;

internal static class OpenFailureTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception("Open failure: " + message); }
    private static JsonElement Describe(FanControlSession session, bool refresh = false)
    {
        using var request = JsonDocument.Parse(refresh ? """{"op":"describe","refresh":true}""" : """{"op":"describe"}""");
        return session.Handle(request.RootElement);
    }

    internal static void Run()
    {
        foreach (var kind in Enum.GetValues<FanOpenFailureKind>())
        {
            var backend = new Backend("fixture-" + kind, kind);
            using var core = new FanCore(backend);
            Check(!core.Open(), "fake open fails");
            Check(core.OpenFailureKind == kind, "core exposes " + kind);
            Check(core.OpenFailures.Single() == new FanOpenFailure(backend.Name, kind, "fixture failure"), "candidate kind and text retained");
            using var session = new FanControlSession(core);
            Check(session.OpenFailureKind == kind && session.OpenFailures.SequenceEqual(core.OpenFailures), "session exposes typed discovery failures");
            var reply = Describe(session);
            Check(!reply.GetProperty("available").GetBoolean(), "describe remains unavailable");
            Check(reply.GetProperty("openFailureKind").GetString() == kind.ToString(), "describe carries kind name");
            var candidate = reply.GetProperty("openFailures")[0];
            Check(candidate.GetProperty("backend").GetString() == backend.Name, "describe carries backend name");
            Check(candidate.GetProperty("kind").GetString() == kind.ToString(), "describe carries candidate kind");
            Check(candidate.GetProperty("reason").GetString() == "fixture failure", "describe carries original text");
            Check(reply.GetProperty("reason").GetString() == backend.Name + "：fixture failure", "existing combined reason retained");
        }

        foreach (var (first, second, expected) in new[]
        {
            (FanOpenFailureKind.AccessDenied, FanOpenFailureKind.Other, FanOpenFailureKind.Other),
            (FanOpenFailureKind.Other, FanOpenFailureKind.AccessDenied, FanOpenFailureKind.Other),
            (FanOpenFailureKind.AccessDenied, FanOpenFailureKind.NoSupportedController, FanOpenFailureKind.AccessDenied),
            (FanOpenFailureKind.DriverMissing, FanOpenFailureKind.AccessDenied, FanOpenFailureKind.AccessDenied),
            (FanOpenFailureKind.NoSupportedController, FanOpenFailureKind.DriverMissing, FanOpenFailureKind.DriverMissing),
            (FanOpenFailureKind.NoSupportedController, FanOpenFailureKind.NoSupportedController, FanOpenFailureKind.NoSupportedController)
        })
        {
            using var core = new FanCore(new IFanBackend[] { new Backend("first", first), new Backend("second", second) });
            Check(!core.Open() && core.OpenFailureKind == expected, $"combine {first} and {second} as {expected}");
            Check(core.OpenFailures.Count == 2 && core.OpenFailures[0].Kind == first && core.OpenFailures[1].Kind == second, "all candidate failures retain order and individual kinds");
        }

        {
            using var core = new FanCore(new IFanBackend[]
            {
                new Backend("throws", FanOpenFailureKind.AccessDenied) { Error = new UnauthorizedAccessException("injected open exception") },
                new Backend("next", FanOpenFailureKind.NoSupportedController)
            });
            Check(!core.Open() && core.OpenFailureKind == FanOpenFailureKind.Other, "exception escaping backend open is Other, including access exceptions");
            Check(core.OpenFailures[0].Reason == "injected open exception" && core.OpenFailures[0].Kind == FanOpenFailureKind.Other, "exception message retained");
            Check(core.OpenFailures.Count == 2, "throwing candidate does not skip later attempts");
        }
        {
            using var core = new FanCore(new Backend("future-kind", (FanOpenFailureKind)12345));
            Check(!core.Open() && core.OpenFailureKind == FanOpenFailureKind.Other && core.OpenFailures[0].Kind == FanOpenFailureKind.Other, "unknown enum values cannot become recognised failures");
        }
        {
            using var core = new FanCore(new UntypedBackend());
            Check(!core.Open() && core.OpenFailureKind == FanOpenFailureKind.Other, "untyped backend failures remain Other regardless of text");
        }
        {
            var backend = new Backend("retry", FanOpenFailureKind.DriverMissing);
            using var core = new FanCore(backend);
            using var session = new FanControlSession(core);
            Check(session.OpenFailureKind == FanOpenFailureKind.DriverMissing, "retry fixture fails first");
            backend.Available = true;
            var reply = Describe(session, refresh: true);
            Check(reply.GetProperty("available").GetBoolean() && core.OpenFailureKind is null && session.OpenFailureKind is null, "successful retry clears overall failure");
            Check(core.OpenFailures.Count == 0 && reply.GetProperty("openFailures").GetArrayLength() == 0 && reply.GetProperty("openFailureKind").ValueKind == JsonValueKind.Null, "successful retry clears candidate failures and JSON kind");
        }
        Console.WriteLine("Open failure kinds: all kinds, precedence, candidate receipts, exceptions, unknown values and successful retry passed.");
    }

    private class UntypedBackend : IFanBackend
    {
        public virtual string Name => "untyped-fixture";
        public virtual bool TryOpen(out string? reason) { reason = "Access denied; missing driver; unsupported controller"; return false; }
        public IReadOnlyList<FanDescription> Describe() => [];
        public IReadOnlyList<FanState> Read() => [];
        public bool TrySetDuty(int fan, double percent, out string? reason) { reason = "unused"; return false; }
        public bool TrySetFullSpeed(int fan, out string? reason) { reason = "unused"; return false; }
        public bool TrySetAutomatic(int fan, out string? reason) { reason = "unused"; return false; }
        public void Dispose() { }
    }

    private sealed class Backend(string name, FanOpenFailureKind kind) : UntypedBackend, IFanBackend
    {
        public override string Name => name;
        public bool Available;
        public Exception? Error;
        public override bool TryOpen(out string? reason) => TryOpen(out _, out reason);
        public bool TryOpen(out FanOpenFailureKind failureKind, out string? reason)
        {
            if (Error is not null) throw Error;
            failureKind = kind;
            reason = Available ? null : "fixture failure";
            return Available;
        }
    }
}
