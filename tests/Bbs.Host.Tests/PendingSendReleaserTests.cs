using System.Text;
using Bbs.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bbs.Host.Tests;

/// <summary>
/// The webmail "undo send" release worker: a deferred message stays held (hidden + unrouted) until its
/// undo window lapses, when <see cref="PendingSendReleaser.ReleaseDue"/> clears the marker and routes
/// it — the same store→routing→forward-queue path compose would have taken immediately.
/// </summary>
public sealed class PendingSendReleaserTests : IAsyncDisposable
{
    private const string OwnCall = "GB7PDN";
    private const string HRoute = "#23.GBR.EURO";

    private readonly DirectoryInfo _dir;
    private readonly FakeTimeProvider _time;
    private readonly BbsStore _store;
    private readonly RoutingService _routing;
    private readonly PendingSendReleaser _releaser;

    public PendingSendReleaserTests()
    {
        _dir = Directory.CreateTempSubdirectory("bbs-pendingsend-test-");
        _time = new FakeTimeProvider(new DateTimeOffset(2026, 6, 11, 12, 0, 0, TimeSpan.Zero));
        _store = BbsStore.Open(Path.Combine(_dir.FullName, "bbs.db"), OwnCall, _time);
        _routing = new RoutingService(
            _store, new RoutingEngine(OwnCall, HRoute), NullLogger<RoutingService>.Instance);
        _releaser = new PendingSendReleaser(_store, _routing, _time, NullLogger<PendingSendReleaser>.Instance);
    }

    public ValueTask DisposeAsync()
    {
        _store.Dispose();
        _dir.Delete(recursive: true);
        return ValueTask.CompletedTask;
    }

    private Message DeferOutbound()
    {
        _store.UpsertPartner(new Partner { Call = "GB7RDG", AtCalls = ["*"] });
        Message m = _store.AddMessage(new MessageDraft
        {
            Type = MessageType.Personal,
            From = "M0LTE",
            Recipients = ["G8ABC"],
            At = "GB7RDG",
            Subject = "deferred",
            Body = Encoding.Latin1.GetBytes("body\r"),
        });
        _store.DeferSend(m.Number, windowSeconds: 5);
        return m;
    }

    [Fact]
    public void ReleaseDue_BeforeWindow_DoesNotRoute_AndLeavesItHeld()
    {
        Message m = DeferOutbound();

        _releaser.ReleaseDue(); // window not yet lapsed

        Assert.Equal(MessageStatus.Held, _store.GetMessage(m.Number)!.Status);
        Assert.NotNull(_store.GetMessage(m.Number)!.SendReleaseUtc);
        Assert.Empty(_store.GetForwardQueue("GB7RDG")); // not routed
    }

    [Fact]
    public void ReleaseDue_AfterWindow_RoutesIt_AndClearsTheMarker()
    {
        Message m = DeferOutbound();

        _time.Advance(TimeSpan.FromSeconds(5));
        _releaser.ReleaseDue();

        Message released = _store.GetMessage(m.Number)!;
        Assert.Null(released.SendReleaseUtc);
        Assert.NotEqual(MessageStatus.Held, released.Status);
        // Routed: it is now in the partner's live forward queue.
        Assert.Equal([m.Number], _store.GetForwardQueue("GB7RDG").Select(x => x.Number));
    }
}
