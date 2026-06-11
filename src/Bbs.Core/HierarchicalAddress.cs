namespace Bbs.Core;

/// <summary>
/// A hierarchical address (HA) / routing designator: <c>CALL[.#REGION].COUNTRY.CONTINENT</c>
/// with WW as the implicit root of every HA (compat spec §2.4). Parsing, completion and
/// matching are a faithful port of [BPQ-SRC MailRouting.c MatchMessagetoBBSList /
/// SetupHAddreses / CheckBBSHElements]:
///
/// <list type="bullet">
/// <item>Elements are stored root-first (WW, continent, country, #region(s), call), matching
/// LinBPQ's element arrays, and upper-cased.</item>
/// <item>Continent codes are canonicalised to the 2-char form (NA≡NOAM etc. — LinBPQ default
/// FOURCHARCONT = 0).</item>
/// <item>A designator whose top element is a known country gets the continent and WW appended;
/// a known continent gets WW appended; an unknown top element leaves the designator
/// un-rooted (<see cref="IsWwRooted"/> false) — LinBPQ treats such bulletins as flood
/// ("Assume a local dis list, and set Flood").</item>
/// <item>Divergence from LinBPQ (deliberate, benign): a literal <c>WWW</c> root is normalised
/// to <c>WW</c> (LinBPQ accepts WWW as "complete" but then compares it literally), and a
/// route pattern ending in .WW does not gain a second WW element.</item>
/// </list>
/// </summary>
public sealed class HierarchicalAddress : IEquatable<HierarchicalAddress>
{
    /// <summary>The root element of every complete HA (compat spec §2.4).</summary>
    public const string Root = "WW";

    private HierarchicalAddress(string raw, IReadOnlyList<string> elements, bool isWwRooted)
    {
        Raw = raw;
        Elements = elements;
        IsWwRooted = isWwRooted;
    }

    /// <summary>The normalised (trimmed, upper-cased) input text.</summary>
    public string Raw { get; }

    /// <summary>
    /// Canonical element list, root-first: e.g. <c>G8BPQ.#23.GBR.EURO</c> parses to
    /// [WW, EU, GBR, #23, G8BPQ]. Empty for an empty designator.
    /// </summary>
    public IReadOnlyList<string> Elements { get; }

    /// <summary>
    /// True when the designator could be completed to the WW root (explicit WW/WWW, known
    /// continent, or known country top element). False for unknown distribution lists, which
    /// LinBPQ floods (compat spec §4.2 "Unconvertible addresses are treated as flood").
    /// </summary>
    public bool IsWwRooted { get; }

    /// <summary>
    /// The leaf (first textual) element — the @BBS callsign part that LinBPQ calls ATBBS and
    /// matches against partner calls and ATCalls lists. Empty string for an empty designator.
    /// </summary>
    public string AtBbs => Elements.Count == 0 ? "" : Elements[^1];

    /// <summary>An empty designator (message with no AT field).</summary>
    public static HierarchicalAddress Empty { get; } = new("", [], false);

    /// <summary>
    /// Parses a message AT designator, applying LinBPQ's completion rules
    /// [BPQ-SRC MailRouting.c MatchMessagetoBBSList "Make sure HA is complete"]:
    /// explicit WW/WWW kept; known continent top → append WW; known country top → append
    /// continent + WW; unknown top → un-rooted, elements kept verbatim.
    /// </summary>
    public static HierarchicalAddress Parse(string? designator)
    {
        string raw = (designator ?? "").Trim().ToUpperInvariant();

        if (raw.Length == 0)
        {
            return Empty;
        }

        string[] tokens = raw.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (tokens.Length == 0)
        {
            return Empty;
        }

        string top = tokens[^1];
        List<string> rootFirst;
        bool rooted;

        if (top is Root or "WWW")
        {
            // Already complete. Normalise WWW → WW (documented divergence).
            rooted = true;
            rootFirst = new List<string>(tokens.Length);
            rootFirst.Add(Root);
            for (int i = tokens.Length - 2; i >= 0; i--)
            {
                rootFirst.Add(tokens[i]);
            }
        }
        else if (GeographicCodes.IsContinent(top))
        {
            rooted = true;
            rootFirst = new List<string>(tokens.Length + 1) { Root };
            for (int i = tokens.Length - 1; i >= 0; i--)
            {
                rootFirst.Add(tokens[i]);
            }
        }
        else if (GeographicCodes.TryGetCountryContinent(top, out string continent))
        {
            rooted = true;
            rootFirst = new List<string>(tokens.Length + 2) { Root, continent };
            for (int i = tokens.Length - 1; i >= 0; i--)
            {
                rootFirst.Add(tokens[i]);
            }
        }
        else
        {
            // "Don't know. Assume a local dis list" — not WW-rooted.
            rooted = false;
            rootFirst = new List<string>(tokens.Length);
            for (int i = tokens.Length - 1; i >= 0; i--)
            {
                rootFirst.Add(tokens[i]);
            }
        }

        CanonicaliseContinentSlot(rootFirst, rooted);
        return new HierarchicalAddress(raw, rootFirst, rooted);
    }

    /// <summary>
    /// Parses a route pattern (a partner HRoutes/HRoutesP entry or BBSHA) the way LinBPQ's
    /// [BPQ-SRC MailRouting.c SetupHAddreses / SetupHAElements] does: WW is always prepended
    /// (no country/continent completion is applied to patterns), and the continent slot is
    /// canonicalised. <c>"WW"</c> yields the bare root.
    /// </summary>
    public static HierarchicalAddress ParseRoutePattern(string? pattern)
    {
        string raw = (pattern ?? "").Trim().ToUpperInvariant();

        if (raw.Length == 0 || raw == Root || raw == "WWW")
        {
            return new HierarchicalAddress(Root, [Root], true);
        }

        string[] tokens = raw.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var rootFirst = new List<string>(tokens.Length + 1) { Root };
        int topIndex = tokens.Length - 1;

        if (tokens[topIndex] is Root or "WWW")
        {
            topIndex--; // don't double the root (documented divergence from LinBPQ, which would)
        }

        for (int i = topIndex; i >= 0; i--)
        {
            rootFirst.Add(tokens[i]);
        }

        CanonicaliseContinentSlot(rootFirst, rooted: true);
        return new HierarchicalAddress(raw, rootFirst, true);
    }

    /// <summary>
    /// Match depth of this designator under <paramref name="pattern"/>: the pattern's element
    /// count when every pattern element matches this designator's elements root-first
    /// ("Only send if all BBS elements match" [BPQ-SRC CheckBBSHElements]), else 0. Depth
    /// counts the WW root, so WW=1, EU=2, GBR.EU=3 — deeper match wins (compat spec §4.2:
    /// "BBS2 with HR GBR.EU" beats "BBS1 with HR EU" for G8BPQ@G8BPQ.#23.GBR.EU).
    /// </summary>
    public int MatchDepth(HierarchicalAddress pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        if (pattern.Elements.Count == 0 || pattern.Elements.Count > Elements.Count)
        {
            return 0;
        }

        for (int i = 0; i < pattern.Elements.Count; i++)
        {
            if (!string.Equals(pattern.Elements[i], Elements[i], StringComparison.Ordinal))
            {
                return 0;
            }
        }

        return pattern.Elements.Count;
    }

    /// <summary>
    /// True when this designator (as a target area) contains <paramref name="station"/> — all
    /// of this designator's elements match the station HA root-first. This is LinBPQ's flood
    /// "Message must be in right area" test [BPQ-SRC CheckBBSHElementsFlood]; an empty
    /// designator contains every station (matches LinBPQ's behaviour for bulls with no AT).
    /// </summary>
    public bool AreaContains(HierarchicalAddress station)
    {
        ArgumentNullException.ThrowIfNull(station);

        if (Elements.Count > station.Elements.Count)
        {
            return false;
        }

        for (int i = 0; i < Elements.Count; i++)
        {
            if (!string.Equals(Elements[i], station.Elements[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The canonical dotted form, leaf-first with the WW root last (e.g. <c>G8BPQ.#23.GBR.EU.WW</c>).</summary>
    public string ToCanonicalString()
    {
        if (Elements.Count == 0)
        {
            return "";
        }

        var leafFirst = new string[Elements.Count];
        for (int i = 0; i < Elements.Count; i++)
        {
            leafFirst[i] = Elements[Elements.Count - 1 - i];
        }

        return string.Join('.', leafFirst);
    }

    /// <inheritdoc/>
    public override string ToString() => ToCanonicalString();

    /// <inheritdoc/>
    public bool Equals(HierarchicalAddress? other)
    {
        if (other is null)
        {
            return false;
        }

        if (IsWwRooted != other.IsWwRooted || Elements.Count != other.Elements.Count)
        {
            return false;
        }

        for (int i = 0; i < Elements.Count; i++)
        {
            if (!string.Equals(Elements[i], other.Elements[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as HierarchicalAddress);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = default(HashCode);
        hash.Add(IsWwRooted);
        foreach (string element in Elements)
        {
            hash.Add(element, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    /// <summary>
    /// Canonicalises the continent slot (element index 1, directly under WW) to the 2-char
    /// code, mirroring LinBPQ's conversion of a 4-char element[1] [BPQ-SRC SetupHAElements].
    /// </summary>
    private static void CanonicaliseContinentSlot(List<string> rootFirst, bool rooted)
    {
        if (rooted && rootFirst.Count > 1 && rootFirst[1].Length == 4)
        {
            rootFirst[1] = GeographicCodes.CanonicalContinent(rootFirst[1]);
        }
    }
}
