namespace Bbs.Core;

/// <summary>
/// Lifetimes for the housekeeping run (compat spec §6): kill-by-age per type+state —
/// "Personals Read/Unread/Forwarded/Unforwarded, Bulls Forwarded/Unforwarded, NTS
/// Delivered/Forwarded/Unforwarded (all default 30 in current code)" — BID Lifetime
/// (default 60), and the grace before killed messages are physically removed (default 0 =
/// at the next run, matching LinBPQ's "first physically remove K-status messages").
/// All values in days; a message is killed when strictly older than its lifetime.
/// </summary>
public sealed record HousekeepingPolicy
{
    /// <summary>Lifetime of read (Y) personals, days.</summary>
    public int PersonalReadDays { get; init; } = 30;

    /// <summary>Lifetime of unread (N, no forwarding pending) personals, days.</summary>
    public int PersonalUnreadDays { get; init; } = 30;

    /// <summary>Lifetime of forwarded (F) personals, days.</summary>
    public int PersonalForwardedDays { get; init; } = 30;

    /// <summary>Lifetime of unforwarded personals (N with forwarding still pending), days.</summary>
    public int PersonalUnforwardedDays { get; init; } = 30;

    /// <summary>Lifetime of forwarded (F) bulletins, days.</summary>
    public int BulletinForwardedDays { get; init; } = 30;

    /// <summary>Lifetime of unforwarded bulletins (N, Y or $), days.</summary>
    public int BulletinUnforwardedDays { get; init; } = 30;

    /// <summary>Lifetime of delivered (D) NTS messages, days.</summary>
    public int NtsDeliveredDays { get; init; } = 30;

    /// <summary>Lifetime of forwarded (F) NTS messages, days.</summary>
    public int NtsForwardedDays { get; init; } = 30;

    /// <summary>Lifetime of unforwarded (N or Y) NTS messages, days.</summary>
    public int NtsUnforwardedDays { get; init; } = 30;

    /// <summary>BID dedup-record lifetime, days (compat spec §2.3/§6 "BID Lifetime default 60").</summary>
    public int BidLifetimeDays { get; init; } = 60;

    /// <summary>
    /// Grace between a kill and physical deletion, days. 0 purges at the next run — LinBPQ's
    /// behaviour ("remains on disk until housekeeping removes it", compat spec §2.2/§6).
    /// </summary>
    public int KilledPurgeGraceDays { get; init; }
}

/// <summary>Counts from one housekeeping run, for the Host's log.</summary>
/// <param name="KilledMessagesPurged">K messages physically deleted.</param>
/// <param name="MessagesKilledByAge">Messages moved to K by the age matrix.</param>
/// <param name="BidsPurged">BID dedup records dropped by lifetime.</param>
public sealed record HousekeepingSummary(int KilledMessagesPurged, int MessagesKilledByAge, int BidsPurged);

/// <summary>
/// The housekeeping pass (compat spec §6), invoked by the Host on a timer (daily at
/// maintenance time, and on demand for a DOHOUSEKEEPING equivalent). Order matches LinBPQ:
/// "first physically remove K-status messages, then kill expired ones" — so a message killed
/// by age this run survives on disk until a later run, exactly like LinBPQ. BID purge runs
/// last; BID records deliberately outlive their messages (§2.3 dedup-survives-kill).
///
/// Status→lifetime mapping judgments (the spec names categories, not statuses): H messages are
/// exempt (they sit in the sysop's queue per §2.2 and cannot expire silently); "unforwarded"
/// personals are N with forwarding still pending, "unread" are N without; bulletins map F vs
/// everything-else (N/Y/$). Named deferrals: per-From/To/At overrides ("ALL, 10" style),
/// non-delivery notifications, message renumbering (§6).
/// </summary>
public static class Housekeeping
{
    private const long SecondsPerDay = 86_400;

    /// <summary>Runs one housekeeping pass against <paramref name="store"/> using its TimeProvider.</summary>
    public static HousekeepingSummary Run(BbsStore store, HousekeepingPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(policy);

        long now = store.NowSeconds();

        // 1. Physically remove killed messages past the grace.
        int purgedKilled = store.PurgeKilledMessages(now - (policy.KilledPurgeGraceDays * SecondsPerDay));

        // 2. Kill by age, per type+state.
        int killedByAge = 0;
        killedByAge += store.KillByAge('P', "F", Cutoff(now, policy.PersonalForwardedDays));
        killedByAge += store.KillByAge('P', "Y", Cutoff(now, policy.PersonalReadDays));
        killedByAge += store.KillByAge('P', "N", Cutoff(now, policy.PersonalUnforwardedDays), hasPendingForwards: true);
        killedByAge += store.KillByAge('P', "N", Cutoff(now, policy.PersonalUnreadDays), hasPendingForwards: false);
        killedByAge += store.KillByAge('B', "F", Cutoff(now, policy.BulletinForwardedDays));
        killedByAge += store.KillByAge('B', "NY$", Cutoff(now, policy.BulletinUnforwardedDays));
        killedByAge += store.KillByAge('T', "D", Cutoff(now, policy.NtsDeliveredDays));
        killedByAge += store.KillByAge('T', "F", Cutoff(now, policy.NtsForwardedDays));
        killedByAge += store.KillByAge('T', "NY", Cutoff(now, policy.NtsUnforwardedDays));

        // 3. BID lifetime purge.
        int purgedBids = store.PurgeExpiredBids(now - (policy.BidLifetimeDays * SecondsPerDay));

        return new HousekeepingSummary(purgedKilled, killedByAge, purgedBids);
    }

    private static long Cutoff(long now, int days) => now - (days * SecondsPerDay);
}
