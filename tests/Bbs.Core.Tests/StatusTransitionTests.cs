namespace Bbs.Core.Tests;

/// <summary>Status transitions per compat spec §2.2, plus the forward-queue state machine.</summary>
public sealed class StatusTransitionTests : IDisposable
{
    private readonly TestStore _ts = new();

    public void Dispose() => _ts.Dispose();

    [Fact]
    public void MarkRead_ByAddressee_MovesNtoY_AndStampsRecipient()
    {
        Message m = _ts.Store.AddMessage(Drafts.Personal(to: "G8BPQ"));

        Assert.True(_ts.Store.MarkRead(m.Number, "G8BPQ"));

        Message after = _ts.Store.GetMessage(m.Number)!;
        Assert.Equal(MessageStatus.Read, after.Status);
        Assert.NotNull(Assert.Single(after.Recipients).ReadAt);
    }

    [Fact]
    public void MarkRead_ByNonAddressee_Fails()
    {
        Message m = _ts.Store.AddMessage(Drafts.Personal(to: "G8BPQ"));
        Assert.False(_ts.Store.MarkRead(m.Number, "2E0ABC"));
        Assert.Equal(MessageStatus.Unread, _ts.Store.GetMessage(m.Number)!.Status);
    }

    [Fact]
    public void MarkRead_TrafficIsNotSetY()
    {
        // §2.2: "T messages are not set Y on read".
        Message m = _ts.Store.AddMessage(Drafts.Traffic(to: "32118"));
        Assert.True(_ts.Store.MarkRead(m.Number, "32118"));
        Assert.Equal(MessageStatus.Unread, _ts.Store.GetMessage(m.Number)!.Status);
    }

    [Theory]
    [InlineData(false)] // forwarded
    [InlineData(true)]  // killed
    public void MarkRead_NeverOverwritesTerminalStatuses(bool kill)
    {
        // §2.2: read "never overwrites K/H/F/D".
        Message m = _ts.Store.AddMessage(Drafts.Personal(to: "G8BPQ"));
        if (kill)
        {
            _ts.Store.Kill(m.Number);
        }
        else
        {
            _ts.Store.EnqueueForwards(m.Number, ["GB7BPQ"]);
            _ts.Store.MarkForwarded(m.Number, "GB7BPQ");
        }

        MessageStatus before = _ts.Store.GetMessage(m.Number)!.Status;
        _ts.Store.MarkRead(m.Number, "G8BPQ");
        Assert.Equal(before, _ts.Store.GetMessage(m.Number)!.Status);
    }

    [Fact]
    public void Kill_SetsKAndStampsKillTime()
    {
        Message m = _ts.Store.AddMessage(Drafts.Personal());
        Assert.True(_ts.Store.Kill(m.Number));

        Message after = _ts.Store.GetMessage(m.Number)!;
        Assert.Equal(MessageStatus.Killed, after.Status);
        Assert.Equal(_ts.Time.GetUtcNow().ToUnixTimeSeconds(), after.KilledAt!.Value.ToUnixTimeSeconds());

        Assert.False(_ts.Store.Kill(m.Number)); // already killed
    }

    [Fact]
    public void EnqueueForwards_BulletinMovesNtoDollar()
    {
        // §2.2: "$ immediately for a bulletin with queued forwarding".
        Message bull = _ts.Store.AddMessage(Drafts.Bulletin());
        _ts.Store.EnqueueForwards(bull.Number, ["GB7BPQ", "GB7AAA"]);
        Assert.Equal(MessageStatus.BulletinQueued, _ts.Store.GetMessage(bull.Number)!.Status);

        // Personals stay N while queued.
        Message p = _ts.Store.AddMessage(Drafts.Personal());
        _ts.Store.EnqueueForwards(p.Number, ["GB7BPQ"]);
        Assert.Equal(MessageStatus.Unread, _ts.Store.GetMessage(p.Number)!.Status);
    }

    [Fact]
    public void GetMessageForwards_ReportsPerLegSentState()
    {
        // Homed locally → no forward legs.
        Message local = _ts.Store.AddMessage(Drafts.Personal(subject: "local"));
        Assert.Empty(_ts.Store.GetMessageForwards(local.Number));

        // Two partners; one sent, one still pending.
        Message m = _ts.Store.AddMessage(Drafts.Personal(subject: "two-legs"));
        _ts.Store.EnqueueForwards(m.Number, ["GB7AAA", "GB7BPQ"]);
        _ts.Store.MarkForwarded(m.Number, "GB7AAA");

        IReadOnlyList<MessageForward> legs = _ts.Store.GetMessageForwards(m.Number);
        Assert.Equal(2, legs.Count);
        Assert.True(legs.Single(f => f.PartnerCall == "GB7AAA").Forwarded);
        Assert.False(legs.Single(f => f.PartnerCall == "GB7BPQ").Forwarded);
    }

    [Fact]
    public void MarkForwarded_SetsFOnlyWhenAllPartnersClear()
    {
        // §2.2: "per-partner bits cleared one at a time; F only when all clear".
        Message bull = _ts.Store.AddMessage(Drafts.Bulletin());
        _ts.Store.EnqueueForwards(bull.Number, ["GB7BPQ", "GB7AAA"]);

        Assert.True(_ts.Store.MarkForwarded(bull.Number, "GB7BPQ"));
        Assert.Equal(MessageStatus.BulletinQueued, _ts.Store.GetMessage(bull.Number)!.Status);

        Assert.True(_ts.Store.MarkForwarded(bull.Number, "GB7AAA"));
        Assert.Equal(MessageStatus.Forwarded, _ts.Store.GetMessage(bull.Number)!.Status);

        Assert.False(_ts.Store.MarkForwarded(bull.Number, "GB7AAA")); // nothing pending
    }

    [Fact]
    public void ForwardQueue_OrdersByPriorityTPB_AndExcludesKilledAndHeld()
    {
        // §2.1: forwarding priority order T, P, B.
        Message bull = _ts.Store.AddMessage(Drafts.Bulletin());
        Message p = _ts.Store.AddMessage(Drafts.Personal());
        Message t = _ts.Store.AddMessage(Drafts.Traffic());
        Message killed = _ts.Store.AddMessage(Drafts.Personal(subject: "killed"));
        Message held = _ts.Store.AddMessage(Drafts.Personal(subject: "held", hold: true));

        foreach (long n in new[] { bull.Number, p.Number, t.Number, killed.Number, held.Number })
        {
            _ts.Store.EnqueueForwards(n, ["GB7BPQ"]);
        }

        _ts.Store.Kill(killed.Number);

        Assert.Equal(
            [t.Number, p.Number, bull.Number],
            _ts.Store.GetForwardQueue("GB7BPQ").Select(m => m.Number));

        // Queue rows are per-partner.
        Assert.Empty(_ts.Store.GetForwardQueue("GB7ZZZ"));
    }

    [Fact]
    public void Unhold_RevertsToDollarForQueuedBulletin_ElseN()
    {
        // §1.4 UH: "status reverts to $ if forwarding queued else N"
        // [BPQ-SRC BBSUtilities.c:3586 — $ only for type B with pending forwards].
        Message bull = _ts.Store.AddMessage(Drafts.Bulletin() with { Hold = true });
        _ts.Store.EnqueueForwards(bull.Number, ["GB7BPQ"]);
        Assert.True(_ts.Store.Unhold(bull.Number));
        Assert.Equal(MessageStatus.BulletinQueued, _ts.Store.GetMessage(bull.Number)!.Status);

        Message p = _ts.Store.AddMessage(Drafts.Personal(hold: true));
        _ts.Store.EnqueueForwards(p.Number, ["GB7BPQ"]);
        Assert.True(_ts.Store.Unhold(p.Number));
        Assert.Equal(MessageStatus.Unread, _ts.Store.GetMessage(p.Number)!.Status);

        Assert.False(_ts.Store.Unhold(p.Number)); // not held any more
    }

    [Fact]
    public void HeldMessage_ResumesQueuedForwardingAfterUnhold()
    {
        Message bull = _ts.Store.AddMessage(Drafts.Bulletin());
        _ts.Store.EnqueueForwards(bull.Number, ["GB7BPQ"]);

        _ts.Store.HoldMessage(bull.Number);
        Assert.Empty(_ts.Store.GetForwardQueue("GB7BPQ")); // H can't be forwarded (§2.2)

        _ts.Store.Unhold(bull.Number);
        Assert.Equal([bull.Number], _ts.Store.GetForwardQueue("GB7BPQ").Select(m => m.Number));
    }

    [Fact]
    public void MarkDelivered_OnlyForTraffic()
    {
        // §1.3 D n: T → status D; non-T → "not an NTS Message".
        Message t = _ts.Store.AddMessage(Drafts.Traffic());
        Message p = _ts.Store.AddMessage(Drafts.Personal());

        Assert.True(_ts.Store.MarkDelivered(t.Number));
        Assert.Equal(MessageStatus.Delivered, _ts.Store.GetMessage(t.Number)!.Status);

        Assert.False(_ts.Store.MarkDelivered(p.Number));
        Assert.Equal(MessageStatus.Unread, _ts.Store.GetMessage(p.Number)!.Status);
    }

    [Fact]
    public void MessageRules_HeldInvisibleAndKillRights()
    {
        Message held = _ts.Store.AddMessage(Drafts.Personal(from: "M0LTE", to: "G8BPQ", hold: true));
        Message p = _ts.Store.AddMessage(Drafts.Personal(from: "M0LTE", to: "G8BPQ"));
        Message b = _ts.Store.AddMessage(Drafts.Bulletin(from: "M0LTE"));
        Message t = _ts.Store.AddMessage(Drafts.Traffic(from: "K4CJX"));

        // Held-invisible (§2.2): only sysop sees/reads/kills H.
        Assert.False(MessageRules.IsVisibleInLists(held, isSysop: false));
        Assert.True(MessageRules.IsVisibleInLists(held, isSysop: true));
        Assert.False(MessageRules.CanRead(held, "G8BPQ", isSysop: false));
        Assert.True(MessageRules.CanRead(held, "ANYONE", isSysop: true));
        Assert.False(MessageRules.CanKill(held, "M0LTE", isSysop: false));
        Assert.True(MessageRules.CanKill(held, "ANYONE", isSysop: true));

        // P: sender or addressee may read/kill; others not (§1.3/§2.2).
        Assert.True(MessageRules.CanRead(p, "M0LTE", false));
        Assert.True(MessageRules.CanRead(p, "G8BPQ", false));
        Assert.False(MessageRules.CanRead(p, "2E0ABC", false));
        Assert.True(MessageRules.CanKill(p, "G8BPQ", false));
        Assert.False(MessageRules.CanKill(p, "2E0ABC", false));

        // B: anyone reads, only sender kills. T: anyone reads and kills (§2.2).
        Assert.True(MessageRules.CanRead(b, "2E0ABC", false));
        Assert.True(MessageRules.CanKill(b, "M0LTE", false));
        Assert.False(MessageRules.CanKill(b, "2E0ABC", false));
        Assert.True(MessageRules.CanRead(t, "2E0ABC", false));
        Assert.True(MessageRules.CanKill(t, "2E0ABC", false));
    }
}
