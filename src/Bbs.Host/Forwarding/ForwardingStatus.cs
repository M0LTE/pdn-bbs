using System.Collections.Concurrent;
using Bbs.Core;

namespace Bbs.Host.Forwarding;

/// <summary>
/// The outcome of a partner's last forwarding dial — the runtime health the
/// <see cref="ForwardingScheduler"/> publishes so the webmail dashboard can show a flapping link as
/// failing (with the reason) instead of a calm "waiting". This is in-memory, per-process state: it
/// reflects attempts since the node started, not durable history (the <c>forwards</c> table is the
/// record of what actually went). Keyed by normalised partner callsign; thread-safe.
/// </summary>
public sealed class ForwardingStatus
{
    private readonly ConcurrentDictionary<string, PartnerForwardingState> _byCall =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _time;

    /// <summary>Creates the registry, stamping outcomes from <paramref name="time"/>.</summary>
    public ForwardingStatus(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
    }

    /// <summary>Records a cycle that reached the partner and ran — clears the failure streak.</summary>
    public void RecordSuccess(string call)
    {
        ArgumentNullException.ThrowIfNull(call);
        _byCall[Callsigns.Normalize(call)] = new PartnerForwardingState(_time.GetUtcNow(), Ok: true, Error: null, ConsecutiveFailures: 0);
    }

    /// <summary>
    /// Records a failed dial with its reason, incrementing this partner's consecutive-failure streak
    /// (atomically — the scheduler loop and a UI read can race). The first failure after a success is 1.
    /// </summary>
    public void RecordFailure(string call, string error)
    {
        ArgumentNullException.ThrowIfNull(call);
        DateTimeOffset now = _time.GetUtcNow();
        _byCall.AddOrUpdate(
            Callsigns.Normalize(call),
            _ => new PartnerForwardingState(now, Ok: false, error, ConsecutiveFailures: 1),
            (_, prev) => new PartnerForwardingState(now, Ok: false, error,
                ConsecutiveFailures: (prev.Ok ? 0 : prev.ConsecutiveFailures) + 1));
    }

    /// <summary>The last recorded outcome for a partner, or null if it has not been dialled yet.</summary>
    public PartnerForwardingState? Get(string call)
    {
        ArgumentNullException.ThrowIfNull(call);
        return _byCall.TryGetValue(Callsigns.Normalize(call), out PartnerForwardingState? state) ? state : null;
    }
}

/// <summary>
/// A partner's last forwarding-dial outcome. <see cref="Ok"/> true means the cycle completed cleanly;
/// false carries the <see cref="Error"/> and the <see cref="ConsecutiveFailures"/> count so the UI can
/// say "failing (N): …".
/// </summary>
/// <param name="LastAttemptUtc">When the last dial was attempted.</param>
/// <param name="Ok">True if the last cycle completed without error.</param>
/// <param name="Error">The failure reason when <see cref="Ok"/> is false; null on success.</param>
/// <param name="ConsecutiveFailures">Consecutive failed cycles (0 when the last was clean).</param>
public sealed record PartnerForwardingState(DateTimeOffset LastAttemptUtc, bool Ok, string? Error, int ConsecutiveFailures);
