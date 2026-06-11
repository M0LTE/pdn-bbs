namespace Bbs.Core;

/// <summary>
/// Filter shape for <see cref="BbsStore.ListMessages"/>, covering the L-family listings of
/// compat spec §1.3: L (new since last list — use <see cref="MinNumber"/>), Lx by status,
/// LB/LP/LT by type, LM (<see cref="ToCall"/>), LL n (<see cref="Limit"/>), L n-m ranges,
/// L&lt; (<see cref="FromCall"/>), L@ (<see cref="AtPrefix"/> — "matches up to the length of
/// the input string"), LR (<see cref="OldestFirst"/>). Results are newest-first by message
/// number unless <see cref="OldestFirst"/> is set.
/// </summary>
public sealed record MessageQuery
{
    /// <summary>Filter by type (LB/LP/LT).</summary>
    public MessageType? Type { get; init; }

    /// <summary>Filter by status (LN/LY/LF/L$/LD; LH/LK additionally need the sysop include flags).</summary>
    public MessageStatus? Status { get; init; }

    /// <summary>Lowest message number to include (L n- and "new since last L").</summary>
    public long? MinNumber { get; init; }

    /// <summary>Highest message number to include (L n-m).</summary>
    public long? MaxNumber { get; init; }

    /// <summary>Only messages received at/after this instant ("new since login").</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>Only messages addressed to this callsign (LM / L&gt; call). Matched per-recipient.</summary>
    public string? ToCall { get; init; }

    /// <summary>Only messages from this callsign (L&lt; call).</summary>
    public string? FromCall { get; init; }

    /// <summary>
    /// AT-field prefix (L@ bbs): "L@ matches up to the length of the input string"
    /// (compat spec §1.3).
    /// </summary>
    public string? AtPrefix { get; init; }

    /// <summary>Maximum rows returned, applied after ordering (LL n = newest n).</summary>
    public int? Limit { get; init; }

    /// <summary>List oldest-first (LR) instead of the default newest-first.</summary>
    public bool OldestFirst { get; init; }

    /// <summary>Include H messages (sysop LH) — held-invisible rule, compat spec §2.2.</summary>
    public bool IncludeHeld { get; init; }

    /// <summary>Include K messages (sysop LK) — compat spec §2.2.</summary>
    public bool IncludeKilled { get; init; }
}
