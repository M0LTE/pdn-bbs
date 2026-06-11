using System.Text;

namespace Bbs.Core;

/// <summary>One recipient of a stored message, with its per-recipient read state.</summary>
/// <param name="ToCall">Addressee callsign (≤6 chars, SSID stripped — compat spec §1.5).</param>
/// <param name="ReadAt">When this recipient read the message, or null if unread by them.</param>
public sealed record MessageRecipient(string ToCall, DateTimeOffset? ReadAt);

/// <summary>
/// A stored BBS message. Immutable snapshot of a row in the store; mutate via
/// <see cref="BbsStore"/> methods so the compat-spec status transitions (§2.2) are enforced.
/// </summary>
public sealed record Message
{
    /// <summary>Maximum stored subject length — compat spec §1.5 step 4: "Stored max 60 chars".</summary>
    public const int MaxSubjectLength = 60;

    /// <summary>Maximum stored BID length — compat spec §2.3: "Stored BID is ≤12 chars (truncated)".</summary>
    public const int MaxBidLength = 12;

    /// <summary>Maximum AT (@BBS) field length — compat spec §1.5: "@ ATBBS (≤40 chars)" / §2.4.</summary>
    public const int MaxAtLength = 40;

    /// <summary>Store-assigned message number; monotonically increasing, never reused.</summary>
    public required long Number { get; init; }

    /// <summary>P/B/T per compat spec §2.1.</summary>
    public required MessageType Type { get; init; }

    /// <summary>N Y $ F K H D per compat spec §2.2.</summary>
    public required MessageStatus Status { get; init; }

    /// <summary>Sender callsign (≤6 chars, SSID stripped — compat spec §1.5).</summary>
    public required string From { get; init; }

    /// <summary>
    /// The AT (@BBS) field: a hierarchical routing designator or bare BBS call, ≤40 chars
    /// (compat spec §2.4). Null when the message has no @ field.
    /// </summary>
    public string? At { get; init; }

    /// <summary>BID/MID, ≤12 chars, deduplicated case-insensitively (compat spec §2.3).</summary>
    public required string Bid { get; init; }

    /// <summary>Subject/title, ≤60 chars stored (compat spec §1.5).</summary>
    public required string Subject { get; init; }

    /// <summary>
    /// Raw body bytes. Kept as bytes end-to-end so Latin-1 (and any 8-bit) user text survives
    /// storage and FBB forwarding unmodified.
    /// </summary>
    public ReadOnlyMemory<byte> Body { get; init; }

    /// <summary>
    /// Callsign of the partner BBS this message arrived from, or null if entered locally.
    /// Feeds the routing loop guard (never route back where it came from — compat spec §4.2
    /// "Never to the partner it came from").
    /// </summary>
    public string? ReceivedFrom { get; init; }

    /// <summary>UTC receipt time at this BBS (drives housekeeping age — compat spec §6).</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>UTC time the message entered status K, anchoring the K-purge grace (compat spec §6).</summary>
    public DateTimeOffset? KilledAt { get; init; }

    /// <summary>
    /// All recipients. A multi-recipient message (S-line recipients separated by ';',
    /// compat spec §1.5) is stored once with one row per recipient so it lists per-user.
    /// </summary>
    public IReadOnlyList<MessageRecipient> Recipients { get; init; } = [];

    /// <summary>Decodes <see cref="Body"/> as Latin-1 (byte-transparent, never lossy).</summary>
    public string GetBodyText() => Encoding.Latin1.GetString(Body.Span);
}
