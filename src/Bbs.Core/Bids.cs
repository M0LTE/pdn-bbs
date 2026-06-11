using System.Globalization;

namespace Bbs.Core;

/// <summary>
/// A row in the BID dedup store (compat spec §2.3). BID records outlive the messages they
/// arrived on — dedup must keep rejecting a bulletin BID after the bulletin itself is killed —
/// and are purged only by BID lifetime (default 60 days, §6).
/// </summary>
/// <param name="Bid">The BID/MID, ≤12 chars, compared case-insensitively [BPQ-SRC LookupBID].</param>
/// <param name="FirstSeen">First time this BID was seen; anchors the lifetime purge.</param>
/// <param name="FirstSeenFrom">
/// Partner the BID first arrived from (null = locally originated). Feeds the routing guard
/// "never route a BID the dedup store has seen from that direction".
/// </param>
/// <param name="MessageNumber">
/// The message this BID currently belongs to, if any — used by the personal-message
/// live-copy duplicate check (compat spec §2.3 [BPQ-SRC DoWeWantIt]).
/// </param>
public sealed record BidRecord(string Bid, DateTimeOffset FirstSeen, string? FirstSeenFrom, long? MessageNumber);

/// <summary>Verdict of the inbound duplicate-BID check (compat spec §2.3).</summary>
public enum BidDisposition
{
    /// <summary>Not a duplicate (or a re-offered personal whose prior copy was forwarded/killed) — accept.</summary>
    Accept,

    /// <summary>Duplicate — answer FS '-' / "NO - BID" (compat spec §1.5 step 3, §3.10).</summary>
    RejectDuplicate,
}

/// <summary>
/// Auto-BID/MID generation per compat spec §2.3: <c>&lt;msgno&gt;_&lt;BBSCALL&gt;</c>
/// (e.g. <c>3331_GM8BPQ</c>), one namespace for all types, ≤12 chars.
/// </summary>
public static class BidGenerator
{
    /// <summary>
    /// Builds <c>&lt;sequence&gt;_&lt;call&gt;</c> and enforces the 12-char cap by truncating
    /// from the right — i.e. the call side — matching LinBPQ's <c>BID[12] = 0</c> truncation
    /// [BPQ-SRC CreateMessage / BBSUtilities.c:5630]. The call's SSID is stripped first
    /// (BBSName never carries one in the BID).
    /// </summary>
    /// <param name="sequence">The store-backed, monotonically-increasing message number.</param>
    /// <param name="bbsCall">The BBS callsign forming the BID suffix.</param>
    public static string Generate(long sequence, string bbsCall)
    {
        ArgumentNullException.ThrowIfNull(bbsCall);
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);

        string call = Callsigns.StripSsid(Callsigns.Normalize(bbsCall));
        string bid = string.Create(CultureInfo.InvariantCulture, $"{sequence}_{call}");
        return bid.Length <= Message.MaxBidLength ? bid : bid[..Message.MaxBidLength];
    }
}
