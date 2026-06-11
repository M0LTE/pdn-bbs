namespace Bbs.Core;

/// <summary>
/// A BBS user record (compat spec §1.1/§2.4): callsign identity, the Home BBS used to
/// auto-complete @ fields, session bookkeeping for the L family, and the pdn-username
/// mapping that webmail's gateway identity binds onto (design.md "Identity/trust for
/// webmail").
/// </summary>
public sealed record User
{
    /// <summary>User callsign (unique, case-insensitive).</summary>
    public required string Callsign { get; init; }

    /// <summary>First name, prompted on first connect (compat spec §1.1).</summary>
    public string? Name { get; init; }

    /// <summary>
    /// Home BBS, ideally a full HA ("Please enter HA with HomeBBS eg g8bpq.gbr.eu" — compat
    /// spec §1.3 Home). Used to auto-complete a missing @ field (§1.5 step 2).
    /// </summary>
    public string? HomeBbs { get; init; }

    /// <summary>Last login time (drives "new since login" listings).</summary>
    public DateTimeOffset? LastLogin { get; init; }

    /// <summary>Highest message number this user has listed ($Z / "L = new since last L", compat spec §1.1/§1.3).</summary>
    public long LastListedNumber { get; init; }

    /// <summary>The pdn platform username this callsign maps to, for webmail later. Null if unlinked.</summary>
    public string? PdnUsername { get; init; }
}
