namespace Bbs.Core.Tests;

/// <summary>
/// Hierarchical addressing (compat spec §2.4): parse/normalise/equivalence/matching, data-driven
/// from the spec's examples and the pinned [BPQ-SRC MailRouting.c] behaviour.
/// </summary>
public sealed class HierarchicalAddressTests
{
    // ---------------------------------------------------------------- parse / normalise

    [Theory]
    // Spec §2.4 example: full HA, explicit nothing — country completes continent + WW.
    [InlineData("G8BPQ.#23.GBR.EU", "G8BPQ.#23.GBR.EU.WW", true)]
    // 4-char continent canonicalises to 2-char (NA≡NOAM, EU≡EURO — §2.4).
    [InlineData("G8BPQ.#23.GBR.EURO", "G8BPQ.#23.GBR.EU.WW", true)]
    [InlineData("GB7BPQ.#23.GBR.EURO.WW", "GB7BPQ.#23.GBR.EU.WW", true)]
    // §2.4: "ALL@USA ≡ ALL@USA.NA" — country top element gains continent and root.
    [InlineData("USA", "USA.NA.WW", true)]
    [InlineData("USA.NA", "USA.NA.WW", true)]
    [InlineData("USA.NOAM", "USA.NA.WW", true)]
    // bare continent gains the root.
    [InlineData("EU", "EU.WW", true)]
    [InlineData("EURO", "EU.WW", true)]
    // explicit roots; WWW tolerated as WW.
    [InlineData("WW", "WW", true)]
    [InlineData("GBR.EU.WWW", "GBR.EU.WW", true)]
    // case-insensitive throughout.
    [InlineData("g8bpq.#23.gbr.euro", "G8BPQ.#23.GBR.EU.WW", true)]
    // unknown top element: kept verbatim, NOT WW-rooted ("Assume a local dis list" [BPQ-SRC]).
    [InlineData("PACKET", "PACKET", false)]
    [InlineData("GB7BPQ", "GB7BPQ", false)]
    public void Parse_NormalisesPerLinBpq(string input, string canonical, bool rooted)
    {
        HierarchicalAddress ha = HierarchicalAddress.Parse(input);
        Assert.Equal(canonical, ha.ToCanonicalString());
        Assert.Equal(rooted, ha.IsWwRooted);
    }

    [Fact]
    public void Parse_EmptyAndNull_GiveTheEmptyDesignator()
    {
        Assert.Equal(HierarchicalAddress.Empty, HierarchicalAddress.Parse(null));
        Assert.Equal(HierarchicalAddress.Empty, HierarchicalAddress.Parse("  "));
        Assert.Empty(HierarchicalAddress.Parse("").Elements);
        Assert.Equal("", HierarchicalAddress.Parse("").AtBbs);
        Assert.False(HierarchicalAddress.Parse("").IsWwRooted);
    }

    [Fact]
    public void Parse_ElementsAreRootFirst_AndAtBbsIsTheLeaf()
    {
        HierarchicalAddress ha = HierarchicalAddress.Parse("G8BPQ.#23.GBR.EURO");
        Assert.Equal(["WW", "EU", "GBR", "#23", "G8BPQ"], ha.Elements);
        Assert.Equal("G8BPQ", ha.AtBbs);
    }

    [Theory]
    // §2.4 equivalences as whole-address equality.
    [InlineData("GBR.EURO", "GBR.EU")]
    [InlineData("USA", "USA.NOAM")]
    [InlineData("usa.na.ww", "USA.NOAM")]
    [InlineData("GBR.EU.WWW", "GBR.EURO.WW")]
    public void Parse_EquivalentForms_AreEqual(string left, string right)
    {
        Assert.Equal(HierarchicalAddress.Parse(left), HierarchicalAddress.Parse(right));
    }

    // ---------------------------------------------------------------- route patterns

    [Theory]
    // Patterns always get the WW root prepended [BPQ-SRC SetupHAddreses], no country completion.
    [InlineData("GBR.EU", "GBR.EU.WW")]
    [InlineData("GBR.EURO", "GBR.EU.WW")]
    [InlineData("WW", "WW")]
    [InlineData("", "WW")]
    [InlineData("#23.GBR.EURO", "#23.GBR.EU.WW")]
    // No completion for patterns: a bare country does NOT gain its continent.
    [InlineData("GBR", "GBR.WW")]
    // A trailing .WW does not double the root (documented divergence from LinBPQ).
    [InlineData("GBR.EU.WW", "GBR.EU.WW")]
    public void ParseRoutePattern_AlwaysRooted(string input, string canonical)
    {
        HierarchicalAddress pattern = HierarchicalAddress.ParseRoutePattern(input);
        Assert.Equal(canonical, pattern.ToCanonicalString());
        Assert.True(pattern.IsWwRooted);
    }

    // ---------------------------------------------------------------- matching

    [Theory]
    // designator, pattern, expected depth (0 = no match). Depth counts the WW root.
    [InlineData("G8BPQ.#23.GBR.EU", "WW", 1)]
    [InlineData("G8BPQ.#23.GBR.EU", "EU", 2)]
    [InlineData("G8BPQ.#23.GBR.EU", "GBR.EU", 3)]          // §4.2's doc example: deeper than EU
    [InlineData("G8BPQ.#23.GBR.EU", "#23.GBR.EU", 4)]
    [InlineData("G8BPQ.#23.GBR.EURO", "GBR.EU", 3)]        // 4-char/2-char equivalence in matching
    [InlineData("G8BPQ.#23.GBR.EU", "GBR.EURO", 3)]
    [InlineData("G8BPQ.#23.GBR.EU", "FRA.EU", 0)]          // wrong country
    [InlineData("EU", "GBR.EU", 0)]                        // pattern deeper than designator: no match
    [InlineData("WW", "GBR.EU", 0)]
    [InlineData("USA", "NA", 2)]                           // country completion feeds matching
    [InlineData("PACKET", "WW", 0)]                        // un-rooted designators never match HR patterns
    public void MatchDepth_RootFirstFullPatternMatch(string designator, string pattern, int expectedDepth)
    {
        HierarchicalAddress at = HierarchicalAddress.Parse(designator);
        Assert.Equal(expectedDepth, at.MatchDepth(HierarchicalAddress.ParseRoutePattern(pattern)));
    }

    [Theory]
    // area (message AT), station (BBSHA), expected contains — the flood "in target area" test.
    [InlineData("GBR.EU", "GB7BPQ.#23.GBR.EURO", true)]
    [InlineData("EU", "GB7BPQ.#23.GBR.EURO", true)]
    [InlineData("WW", "GB7BPQ.#23.GBR.EURO", true)]
    [InlineData("#23.GBR.EU", "GB7BPQ.#23.GBR.EURO", true)]
    [InlineData("#41.GBR.EU", "GB7BPQ.#23.GBR.EURO", false)] // different region
    [InlineData("USA.NA", "GB7BPQ.#23.GBR.EURO", false)]
    [InlineData("", "GB7BPQ.#23.GBR.EURO", true)]            // no AT: contains everything (BPQ NOHA path)
    public void AreaContains_FloodTargetAreaTest(string area, string stationHa, bool expected)
    {
        HierarchicalAddress at = HierarchicalAddress.Parse(area);
        HierarchicalAddress station = HierarchicalAddress.ParseRoutePattern(stationHa);
        Assert.Equal(expected, at.AreaContains(station));
    }
}
