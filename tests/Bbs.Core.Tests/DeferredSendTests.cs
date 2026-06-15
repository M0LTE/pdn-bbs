namespace Bbs.Core.Tests;

/// <summary>
/// The store half of the webmail "undo send" (schema v10): a composed message is deferred — held
/// (hidden + unforwarded) with a <c>send_release_utc</c> marker — until its undo window lapses, when a
/// release worker clears the marker + unholds it; Undo cancels (kills) it while the window is open.
/// </summary>
public sealed class DeferredSendTests : IDisposable
{
    private readonly TestStore _ts = new();

    public void Dispose() => _ts.Dispose();

    [Fact]
    public void DeferSend_HoldsAndStampsMarker_AndHidesFromForwardQueue()
    {
        Message m = _ts.Store.AddMessage(Drafts.Personal(to: "M0LTE", at: "GB7RDG"));
        _ts.Store.EnqueueForwards(m.Number, ["GB7RDG"]);

        _ts.Store.DeferSend(m.Number, windowSeconds: 5);

        Message held = _ts.Store.GetMessage(m.Number)!;
        Assert.Equal(MessageStatus.Held, held.Status);
        Assert.NotNull(held.SendReleaseUtc);
        Assert.Equal(
            _ts.Time.GetUtcNow().AddSeconds(5).ToUnixTimeSeconds(),
            held.SendReleaseUtc!.Value.ToUnixTimeSeconds());

        // Held → out of the live forward queue while pending (won't leak before the window lapses).
        Assert.Empty(_ts.Store.GetForwardQueue("GB7RDG"));
    }

    [Fact]
    public void DeferSend_DoesNotDisturbKilledOrAlreadyHeld()
    {
        Message killed = _ts.Store.AddMessage(Drafts.Personal());
        _ts.Store.Kill(killed.Number);
        _ts.Store.DeferSend(killed.Number, 5);
        Message afterKilled = _ts.Store.GetMessage(killed.Number)!;
        Assert.Equal(MessageStatus.Killed, afterKilled.Status);
        Assert.Null(afterKilled.SendReleaseUtc);

        Message held = _ts.Store.AddMessage(Drafts.Personal(hold: true));
        _ts.Store.DeferSend(held.Number, 5);
        Assert.Null(_ts.Store.GetMessage(held.Number)!.SendReleaseUtc); // unchanged: was already held
    }

    [Fact]
    public void ListDueDeferredSends_IsEmptyBeforeWindow_AndReturnsItAfter()
    {
        Message m = _ts.Store.AddMessage(Drafts.Personal());
        _ts.Store.DeferSend(m.Number, windowSeconds: 5);

        // Before the window: nothing due.
        Assert.Empty(_ts.Store.ListDueDeferredSends());

        // At/after the window: the message is due (recipients attached).
        _ts.Time.Advance(TimeSpan.FromSeconds(5));
        Message due = Assert.Single(_ts.Store.ListDueDeferredSends());
        Assert.Equal(m.Number, due.Number);
        Assert.NotEmpty(due.Recipients);
    }

    [Fact]
    public void ReleaseDeferredSend_ClearsMarkerAndUnholds()
    {
        // A personal reverts to N.
        Message p = _ts.Store.AddMessage(Drafts.Personal());
        _ts.Store.DeferSend(p.Number, 5);
        _ts.Store.ReleaseDeferredSend(p.Number);
        Message releasedP = _ts.Store.GetMessage(p.Number)!;
        Assert.Null(releasedP.SendReleaseUtc);
        Assert.Equal(MessageStatus.Unread, releasedP.Status);

        // A bulletin with queued forwards reverts to $ (the Unhold transition).
        Message bull = _ts.Store.AddMessage(Drafts.Bulletin());
        _ts.Store.EnqueueForwards(bull.Number, ["GB7BPQ"]);
        _ts.Store.DeferSend(bull.Number, 5);
        _ts.Store.ReleaseDeferredSend(bull.Number);
        Message releasedB = _ts.Store.GetMessage(bull.Number)!;
        Assert.Null(releasedB.SendReleaseUtc);
        Assert.Equal(MessageStatus.BulletinQueued, releasedB.Status);
        // Released → back in the forward queue.
        Assert.Equal([bull.Number], _ts.Store.GetForwardQueue("GB7BPQ").Select(x => x.Number));
    }

    [Fact]
    public void CancelDeferredSend_KillsWhilePending_AndIsNoOpOnceReleased()
    {
        Message m = _ts.Store.AddMessage(Drafts.Personal());
        _ts.Store.DeferSend(m.Number, windowSeconds: 5);

        // Within the window: Undo kills it and clears the marker.
        Assert.True(_ts.Store.CancelDeferredSend(m.Number));
        Message cancelled = _ts.Store.GetMessage(m.Number)!;
        Assert.Equal(MessageStatus.Killed, cancelled.Status);
        Assert.Null(cancelled.SendReleaseUtc);

        // A second message that has been released: Undo is a no-op (the worker already routed it).
        Message other = _ts.Store.AddMessage(Drafts.Personal());
        _ts.Store.DeferSend(other.Number, 5);
        _ts.Time.Advance(TimeSpan.FromSeconds(5));
        _ts.Store.ReleaseDeferredSend(other.Number);
        Assert.False(_ts.Store.CancelDeferredSend(other.Number));
        Assert.Equal(MessageStatus.Unread, _ts.Store.GetMessage(other.Number)!.Status);
    }

    [Fact]
    public void CancelDeferredSend_AfterWindowLapses_ButBeforeRelease_IsNoOp()
    {
        // The marker is still set but the window is in the past: Undo must not cancel (the worker owns
        // it now). This guards the server-side window enforcement the no-JS Undo relies on.
        Message m = _ts.Store.AddMessage(Drafts.Personal());
        _ts.Store.DeferSend(m.Number, windowSeconds: 5);
        _ts.Time.Advance(TimeSpan.FromSeconds(6));

        Assert.False(_ts.Store.CancelDeferredSend(m.Number));
        Assert.Equal(MessageStatus.Held, _ts.Store.GetMessage(m.Number)!.Status); // still pending release
    }

    [Fact]
    public void DeferredMarker_PersistsAcrossReopen()
    {
        Message m = _ts.Store.AddMessage(Drafts.Personal());
        _ts.Store.DeferSend(m.Number, windowSeconds: 5);
        long expected = _ts.Store.GetMessage(m.Number)!.SendReleaseUtc!.Value.ToUnixTimeSeconds();

        BbsStore reopened = _ts.Reopen();
        Message after = reopened.GetMessage(m.Number)!;
        Assert.Equal(MessageStatus.Held, after.Status);
        Assert.NotNull(after.SendReleaseUtc);
        Assert.Equal(expected, after.SendReleaseUtc!.Value.ToUnixTimeSeconds());
    }
}
