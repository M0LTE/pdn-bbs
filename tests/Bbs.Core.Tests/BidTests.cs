namespace Bbs.Core.Tests;

/// <summary>BID generation (§2.3) and the dedup store: case-insensitivity, lifetime, survives-kill.</summary>
public sealed class BidTests : IDisposable
{
    private readonly TestStore _ts = new();

    public void Dispose() => _ts.Dispose();

    // ---------------------------------------------------------------- generation

    [Theory]
    [InlineData(1, "GB7PDN", "1_GB7PDN")]
    [InlineData(3331, "GM8BPQ", "3331_GM8BPQ")]      // §2.3's example shape
    [InlineData(123456, "GB7PDN", "123456_GB7PD")]   // 13 chars → call side truncated to fit 12
    [InlineData(12345678901, "GB7PDN", "12345678901_")] // pathological: number consumes the cap
    [InlineData(7, "gb7pdn-1", "7_GB7PDN")]          // SSID stripped, upper-cased
    public void Generate_BuildsNumberUnderscoreCall_CappedAt12(long sequence, string call, string expected)
    {
        string bid = BidGenerator.Generate(sequence, call);
        Assert.Equal(expected, bid);
        Assert.True(bid.Length <= Message.MaxBidLength);
    }

    [Fact]
    public void AutoBid_IsMonotonic_AndStoreBacked()
    {
        Message first = _ts.Store.AddMessage(Drafts.Personal());
        Message second = _ts.Store.AddMessage(Drafts.Personal());

        Assert.Equal($"{first.Number}_GB7PDN", first.Bid);
        Assert.Equal($"{second.Number}_GB7PDN", second.Bid);
        Assert.True(second.Number > first.Number);

        // Survives reopen: the sequence continues, never repeats.
        _ts.Reopen();
        Message third = _ts.Store.AddMessage(Drafts.Personal());
        Assert.True(third.Number > second.Number);
        Assert.Equal($"{third.Number}_GB7PDN", third.Bid);
    }

    // ---------------------------------------------------------------- dedup store

    [Fact]
    public void Lookup_IsCaseInsensitive()
    {
        // §2.3: "lookup case-insensitive (_stricmp)".
        _ts.Store.AddMessage(Drafts.Bulletin(bid: "123_gb7abc"));

        Assert.NotNull(_ts.Store.LookupBid("123_GB7ABC"));
        Assert.NotNull(_ts.Store.LookupBid("123_Gb7AbC"));
        Assert.Null(_ts.Store.LookupBid("124_GB7ABC"));
    }

    [Fact]
    public void RecordBid_PreservesFirstSeenTimeAndDirection()
    {
        Assert.True(_ts.Store.RecordBid("55_GB7XYZ", seenFrom: "GB7BPQ-1"));
        DateTimeOffset firstSeen = _ts.Time.GetUtcNow();

        _ts.Time.AdvanceDays(5);
        Assert.False(_ts.Store.RecordBid("55_gb7xyz", seenFrom: "GB7AAA"));

        BidRecord record = _ts.Store.LookupBid("55_GB7XYZ")!;
        Assert.Equal(firstSeen.ToUnixTimeSeconds(), record.FirstSeen.ToUnixTimeSeconds());
        Assert.Equal("GB7BPQ-1", record.FirstSeenFrom);
    }

    [Fact]
    public void Dedup_SurvivesKillAndPhysicalPurge()
    {
        // §2.3 + task spec: dedup must survive the message itself being killed.
        Message bull = _ts.Store.AddMessage(Drafts.Bulletin(bid: "77_GB7BPQ", receivedFrom: "GB7BPQ"));
        _ts.Store.Kill(bull.Number);
        Housekeeping.Run(_ts.Store, new HousekeepingPolicy());
        Assert.Null(_ts.Store.GetMessage(bull.Number)); // physically gone

        Assert.NotNull(_ts.Store.LookupBid("77_GB7BPQ"));
        Assert.Equal(BidDisposition.RejectDuplicate, _ts.Store.CheckInboundBid("77_gb7bpq", MessageType.Bulletin, "ALL"));
    }

    [Fact]
    public void Lifetime_PurgesOldBids()
    {
        // §6: BID Lifetime default 60 days.
        _ts.Store.RecordBid("OLD_BID");
        _ts.Time.AdvanceDays(61);
        _ts.Store.RecordBid("NEW_BID");

        HousekeepingSummary summary = Housekeeping.Run(_ts.Store, new HousekeepingPolicy());

        Assert.Equal(1, summary.BidsPurged);
        Assert.Null(_ts.Store.LookupBid("OLD_BID"));
        Assert.NotNull(_ts.Store.LookupBid("NEW_BID"));
    }

    // ---------------------------------------------------------------- inbound dup matrix (§2.3 [BPQ-SRC DoWeWantIt])

    [Fact]
    public void Inbound_UnknownBid_Accepts()
    {
        Assert.Equal(BidDisposition.Accept, _ts.Store.CheckInboundBid("1_GB7NEW", MessageType.Bulletin, "ALL"));
        Assert.Equal(BidDisposition.Accept, _ts.Store.CheckInboundBid("1_GB7NEW", MessageType.Personal, "G8BPQ"));
    }

    [Fact]
    public void Inbound_Bulletin_AnyKnownBidRejects_CaseFlipped()
    {
        _ts.Store.AddMessage(Drafts.Bulletin(bid: "42_GB7BPQ"));
        Assert.Equal(BidDisposition.RejectDuplicate, _ts.Store.CheckInboundBid("42_gb7bpq", MessageType.Bulletin, "TECH"));
    }

    [Fact]
    public void Inbound_Personal_LiveCopySameTo_Rejects()
    {
        _ts.Store.AddMessage(Drafts.Personal(to: "G8BPQ", bid: "9_GB7BPQ"));
        Assert.Equal(BidDisposition.RejectDuplicate, _ts.Store.CheckInboundBid("9_GB7BPQ", MessageType.Personal, "G8BPQ"));
    }

    [Fact]
    public void Inbound_Personal_DifferentTo_Accepts()
    {
        // [BPQ-SRC]: "if the same TO we will assume the same message" — different TO is not a dup.
        _ts.Store.AddMessage(Drafts.Personal(to: "G8BPQ", bid: "9_GB7BPQ"));
        Assert.Equal(BidDisposition.Accept, _ts.Store.CheckInboundBid("9_GB7BPQ", MessageType.Personal, "2E0ABC"));
    }

    [Theory]
    [InlineData(false)] // forwarded copy → accept again
    [InlineData(true)]  // killed copy → accept again
    public void Inbound_Personal_ForwardedOrKilledCopy_AcceptsAgain(bool kill)
    {
        // §2.3: "forwarded/killed copies are accepted again".
        Message m = _ts.Store.AddMessage(Drafts.Personal(to: "G8BPQ", bid: "9_GB7BPQ"));
        if (kill)
        {
            _ts.Store.Kill(m.Number);
        }
        else
        {
            _ts.Store.EnqueueForwards(m.Number, ["GB7BPQ"]);
            _ts.Store.MarkForwarded(m.Number, "GB7BPQ");
        }

        Assert.Equal(BidDisposition.Accept, _ts.Store.CheckInboundBid("9_GB7BPQ", MessageType.Personal, "G8BPQ"));
    }

    [Fact]
    public void Inbound_Personal_HeldCopy_Rejects()
    {
        // [BPQ-SRC DoWeWantIt]: live = status N/Y/H.
        _ts.Store.AddMessage(Drafts.Personal(to: "G8BPQ", bid: "9_GB7BPQ", hold: true));
        Assert.Equal(BidDisposition.RejectDuplicate, _ts.Store.CheckInboundBid("9_GB7BPQ", MessageType.Personal, "G8BPQ"));
    }

    [Fact]
    public void Inbound_Traffic_FollowsThePersonalLiveCopyRule()
    {
        // [BPQ-SRC DoWeWantIt] only special-cases 'B'; T uses the live-copy path.
        Message m = _ts.Store.AddMessage(Drafts.Traffic(to: "32118", bid: "8_K4CJX"));
        Assert.Equal(BidDisposition.RejectDuplicate, _ts.Store.CheckInboundBid("8_K4CJX", MessageType.Traffic, "32118"));

        _ts.Store.Kill(m.Number);
        Assert.Equal(BidDisposition.Accept, _ts.Store.CheckInboundBid("8_K4CJX", MessageType.Traffic, "32118"));
    }

    [Fact]
    public void Inbound_SuppliedBidLongerThan12_TruncatesBeforeComparing()
    {
        // §2.3 / [BPQ-SRC BBSUtilities.c:5630]: stored BID truncated at 12 — a 13-char offer
        // matching the first 12 chars is the same BID.
        _ts.Store.AddMessage(Drafts.Bulletin(bid: "1234567890AB"));
        Assert.Equal(BidDisposition.RejectDuplicate, _ts.Store.CheckInboundBid("1234567890ABC", MessageType.Bulletin, "ALL"));
    }
}
