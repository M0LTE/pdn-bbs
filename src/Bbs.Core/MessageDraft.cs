namespace Bbs.Core;

/// <summary>
/// Input shape for <see cref="BbsStore.AddMessage"/>. Field limits are applied by the store
/// on insert: subject truncated to 60 (compat spec §1.5), BID to 12 (§2.3), TO/FROM to 6 with
/// SSID stripped (§1.5), AT to 40 (§2.4).
/// </summary>
public sealed record MessageDraft
{
    /// <summary>P/B/T per compat spec §2.1.</summary>
    public required MessageType Type { get; init; }

    /// <summary>Sender callsign (the connected user, or the &lt; call override — compat spec §1.5).</summary>
    public required string From { get; init; }

    /// <summary>One or more addressees (S-line recipients separated by ';' — compat spec §1.5).</summary>
    public required IReadOnlyList<string> Recipients { get; init; }

    /// <summary>The AT (@BBS) field, or null/empty for none.</summary>
    public string? At { get; init; }

    /// <summary>
    /// Caller-supplied BID (the $bid token, sigil already removed — compat spec §1.5/§2.3),
    /// or null to auto-allocate <c>&lt;msgno&gt;_&lt;BBSCALL&gt;</c> per §2.3.
    /// </summary>
    public string? Bid { get; init; }

    /// <summary>Subject/title.</summary>
    public string Subject { get; init; } = "";

    /// <summary>Raw body bytes (Latin-1-safe; stored verbatim).</summary>
    public ReadOnlyMemory<byte> Body { get; init; }

    /// <summary>Partner BBS the message arrived from, or null if entered locally.</summary>
    public string? ReceivedFrom { get; init; }

    /// <summary>
    /// Store with initial status H instead of N (held by filters / new-user hold / too-big /
    /// looping / bad date — compat spec §2.2).
    /// </summary>
    public bool Hold { get; init; }
}
