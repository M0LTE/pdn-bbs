namespace Bbs.Core.Tests;

/// <summary>The persisted per-partner forwarding health (schema v9): success/failure recording, the
/// consecutive-failure streak, and survival across a store reopen (a node restart).</summary>
public sealed class ForwardingStatusStoreTests : IDisposable
{
    private readonly TestStore _ts = new();

    public void Dispose() => _ts.Dispose();

    [Fact]
    public void Unknown_partner_hasNoStatus()
    {
        Assert.Null(_ts.Store.GetForwardingStatus("GB7RDG"));
    }

    [Fact]
    public void Failure_streakIncrements_andSuccessResets()
    {
        _ts.Store.RecordForwardingFailure("GB7RDG", "connect refused");
        PartnerForwardingState s1 = _ts.Store.GetForwardingStatus("GB7RDG")!;
        Assert.False(s1.Ok);
        Assert.Equal("connect refused", s1.Error);
        Assert.Equal(1, s1.ConsecutiveFailures);

        _ts.Store.RecordForwardingFailure("GB7RDG", "still refused");
        Assert.Equal(2, _ts.Store.GetForwardingStatus("GB7RDG")!.ConsecutiveFailures);

        _ts.Store.RecordForwardingSuccess("GB7RDG");
        PartnerForwardingState ok = _ts.Store.GetForwardingStatus("GB7RDG")!;
        Assert.True(ok.Ok);
        Assert.Null(ok.Error);
        Assert.Equal(0, ok.ConsecutiveFailures);

        _ts.Store.RecordForwardingFailure("GB7RDG", "refused");
        Assert.Equal(1, _ts.Store.GetForwardingStatus("GB7RDG")!.ConsecutiveFailures); // restarts after a success
    }

    [Fact]
    public void Status_survivesAReopen_theRestartCase()
    {
        _ts.Store.RecordForwardingFailure("GB7RDG", "connect refused");
        _ts.Store.RecordForwardingFailure("GB7RDG", "connect refused");

        BbsStore reopened = _ts.Reopen();   // simulate a node restart

        PartnerForwardingState s = reopened.GetForwardingStatus("GB7RDG")!;
        Assert.False(s.Ok);
        Assert.Equal("connect refused", s.Error);
        Assert.Equal(2, s.ConsecutiveFailures);   // the streak persisted, not reset to "—"
    }

    [Fact]
    public void Lookup_isCallsignCaseInsensitive()
    {
        _ts.Store.RecordForwardingFailure("gb7rdg", "x");
        Assert.NotNull(_ts.Store.GetForwardingStatus("GB7RDG"));
    }
}
