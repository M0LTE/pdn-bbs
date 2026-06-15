using Bbs.Host.Forwarding;
using Microsoft.Extensions.Time.Testing;

namespace Bbs.Host.Tests;

public class ForwardingStatusTests
{
    private static FakeTimeProvider Clock() => new(new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Get_IsNullUntilTheFirstRecord()
    {
        var status = new ForwardingStatus(Clock());
        Assert.Null(status.Get("GB7RDG"));   // never dialled → no health to show
    }

    [Fact]
    public void Failure_StreakIncrements_AndSuccessResetsIt()
    {
        var status = new ForwardingStatus(Clock());

        status.RecordFailure("GB7RDG", "connect refused");
        Assert.False(status.Get("GB7RDG")!.Ok);
        Assert.Equal(1, status.Get("GB7RDG")!.ConsecutiveFailures);
        Assert.Equal("connect refused", status.Get("GB7RDG")!.Error);

        status.RecordFailure("GB7RDG", "still refused");
        Assert.Equal(2, status.Get("GB7RDG")!.ConsecutiveFailures);   // streak grows
        Assert.Equal("still refused", status.Get("GB7RDG")!.Error);

        status.RecordSuccess("GB7RDG");
        Assert.True(status.Get("GB7RDG")!.Ok);
        Assert.Equal(0, status.Get("GB7RDG")!.ConsecutiveFailures);   // cleared
        Assert.Null(status.Get("GB7RDG")!.Error);

        status.RecordFailure("GB7RDG", "refused");
        Assert.Equal(1, status.Get("GB7RDG")!.ConsecutiveFailures);   // restarts after a success
    }

    [Fact]
    public void Lookup_IsCallsignCaseInsensitive()
    {
        var status = new ForwardingStatus(Clock());
        status.RecordFailure("gb7rdg", "x");
        Assert.NotNull(status.Get("GB7RDG"));
    }
}
