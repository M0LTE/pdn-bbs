namespace Bbs.Core.Tests;

/// <summary>The L-family listing queries (compat spec §1.3) and the held-invisible rule (§2.2).</summary>
public sealed class ListQueryTests : IDisposable
{
    private readonly TestStore _ts = new();

    public ListQueryTests()
    {
        // 1: P M0LTE→G8BPQ @GB7BPQ.#23.GBR.EURO
        _ts.Store.AddMessage(Drafts.Personal(from: "M0LTE", to: "G8BPQ", at: "GB7BPQ.#23.GBR.EURO", subject: "one"));
        // 2: B M0LTE→ALL @GBR.EURO
        _ts.Store.AddMessage(Drafts.Bulletin(from: "M0LTE", to: "ALL", at: "GBR.EURO", subject: "two"));
        // 3: T K4CJX→32118
        _ts.Store.AddMessage(Drafts.Traffic(from: "K4CJX", to: "32118") with { Subject = "three" });
        // 4: P G8BPQ→M0LTE (held)
        _ts.Store.AddMessage(Drafts.Personal(from: "G8BPQ", to: "M0LTE", subject: "four", hold: true));
        // 5: P G8BPQ→M0LTE;2E0ABC
        _ts.Store.AddMessage(Drafts.Personal(from: "G8BPQ", to: "M0LTE", subject: "five") with { Recipients = ["M0LTE", "2E0ABC"] });
    }

    public void Dispose() => _ts.Dispose();

    private long[] Numbers(MessageQuery query) => [.. _ts.Store.ListMessages(query).Select(m => m.Number)];

    [Fact]
    public void DefaultList_IsNewestFirst_AndHidesHeld()
    {
        Assert.Equal([5, 3, 2, 1], Numbers(new MessageQuery()));
    }

    [Fact]
    public void OldestFirst_ReversesOrder()
    {
        Assert.Equal([1, 2, 3, 5], Numbers(new MessageQuery { OldestFirst = true }));
    }

    [Fact]
    public void ByType_FiltersLbLpLt()
    {
        Assert.Equal([5, 1], Numbers(new MessageQuery { Type = MessageType.Personal }));
        Assert.Equal([2], Numbers(new MessageQuery { Type = MessageType.Bulletin }));
        Assert.Equal([3], Numbers(new MessageQuery { Type = MessageType.Traffic }));
    }

    [Fact]
    public void ByStatus_FiltersAndRespectsSysopFlags()
    {
        Assert.Equal([5, 3, 2, 1], Numbers(new MessageQuery { Status = MessageStatus.Unread }));

        // LH is sysop-only: held messages appear only with IncludeHeld (§2.2 held-invisible).
        Assert.Empty(Numbers(new MessageQuery { Status = MessageStatus.Held }));
        Assert.Equal([4], Numbers(new MessageQuery { Status = MessageStatus.Held, IncludeHeld = true }));
    }

    [Fact]
    public void KilledMessages_OnlyVisibleWithIncludeKilled()
    {
        _ts.Store.Kill(1);
        Assert.Equal([5, 3, 2], Numbers(new MessageQuery()));
        Assert.Equal([1], Numbers(new MessageQuery { Status = MessageStatus.Killed, IncludeKilled = true }));
    }

    [Fact]
    public void Range_MinAndMax()
    {
        Assert.Equal([3, 2], Numbers(new MessageQuery { MinNumber = 2, MaxNumber = 3 }));
        Assert.Equal([5, 3], Numbers(new MessageQuery { MinNumber = 3 }));
    }

    [Fact]
    public void Limit_TakesNewestN()
    {
        Assert.Equal([5, 3], Numbers(new MessageQuery { Limit = 2 }));
    }

    [Fact]
    public void Since_FiltersByReceiptTime()
    {
        _ts.Time.AdvanceDays(1);
        DateTimeOffset mark = _ts.Time.GetUtcNow();
        Message six = _ts.Store.AddMessage(Drafts.Personal(subject: "six"));

        Assert.Equal([six.Number], Numbers(new MessageQuery { Since = mark }));
    }

    [Fact]
    public void AddressedTo_MatchesPerRecipient()
    {
        // LM for M0LTE: messages 5 (multi-recipient) but not held 4.
        Assert.Equal([5], Numbers(new MessageQuery { ToCall = "M0LTE" }));
        // The same message lists for the other recipient too.
        Assert.Equal([5], Numbers(new MessageQuery { ToCall = "2E0ABC" }));
        Assert.Equal([1], Numbers(new MessageQuery { ToCall = "G8BPQ" }));
    }

    [Fact]
    public void FromCall_Filters()
    {
        Assert.Equal([5], Numbers(new MessageQuery { FromCall = "G8BPQ" }));
        Assert.Equal([3], Numbers(new MessageQuery { FromCall = "K4CJX" }));
    }

    [Fact]
    public void AtPrefix_MatchesUpToInputLength()
    {
        // "L@ matches up to the length of the input string" (§1.3).
        Assert.Equal([2, 1], Numbers(new MessageQuery { AtPrefix = "GB" }));
        Assert.Equal([1], Numbers(new MessageQuery { AtPrefix = "GB7BPQ" }));
        Assert.Equal([2], Numbers(new MessageQuery { AtPrefix = "gbr" }));
        Assert.Empty(Numbers(new MessageQuery { AtPrefix = "VK" }));
    }

    [Fact]
    public void CombinedFilters_Compose()
    {
        // e.g. "LP> M0LTE"-shaped query: personals addressed to M0LTE, newest-first.
        Assert.Equal([5], Numbers(new MessageQuery { Type = MessageType.Personal, ToCall = "M0LTE" }));
    }
}
