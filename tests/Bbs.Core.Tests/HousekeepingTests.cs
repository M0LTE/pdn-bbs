namespace Bbs.Core.Tests;

/// <summary>Housekeeping (compat spec §6): K-purge order/grace, the kill-by-age matrix, BID lifetime.</summary>
public sealed class HousekeepingTests : IDisposable
{
    private readonly TestStore _ts = new();

    public void Dispose() => _ts.Dispose();

    private static readonly HousekeepingPolicy Defaults = new();

    [Fact]
    public void KilledMessages_PurgedAtNextRun_ByDefault()
    {
        // §6: "first physically remove K-status messages" — grace 0 = next run, like LinBPQ.
        Message m = _ts.Store.AddMessage(Drafts.Personal());
        _ts.Store.Kill(m.Number);

        HousekeepingSummary summary = Housekeeping.Run(_ts.Store, Defaults);

        Assert.Equal(1, summary.KilledMessagesPurged);
        Assert.Null(_ts.Store.GetMessage(m.Number));
    }

    [Fact]
    public void KilledMessages_RespectPurgeGrace()
    {
        Message m = _ts.Store.AddMessage(Drafts.Personal());
        _ts.Store.Kill(m.Number);

        var policy = new HousekeepingPolicy { KilledPurgeGraceDays = 7 };

        Assert.Equal(0, Housekeeping.Run(_ts.Store, policy).KilledMessagesPurged);
        Assert.NotNull(_ts.Store.GetMessage(m.Number)); // still on disk, status K

        _ts.Time.AdvanceDays(8);
        Assert.Equal(1, Housekeeping.Run(_ts.Store, policy).KilledMessagesPurged);
        Assert.Null(_ts.Store.GetMessage(m.Number));
    }

    [Fact]
    public void KillByAge_RunsAfterPurge_SoAgedMessagesSurviveOneRunOnDisk()
    {
        // LinBPQ order: purge K first, then kill expired — a message killed by age this run is
        // physically removed at a later run (§6, §2.2 "remains on disk until housekeeping
        // removes it").
        Message m = _ts.Store.AddMessage(Drafts.Personal());
        _ts.Time.AdvanceDays(31);

        HousekeepingSummary firstRun = Housekeeping.Run(_ts.Store, Defaults);
        Assert.Equal(1, firstRun.MessagesKilledByAge);
        Assert.Equal(0, firstRun.KilledMessagesPurged);
        Assert.Equal(MessageStatus.Killed, _ts.Store.GetMessage(m.Number)!.Status);

        HousekeepingSummary secondRun = Housekeeping.Run(_ts.Store, Defaults);
        Assert.Equal(1, secondRun.KilledMessagesPurged);
        Assert.Null(_ts.Store.GetMessage(m.Number));
    }

    [Fact]
    public void KillByAge_NotBeforeLifetimeElapses()
    {
        _ts.Store.AddMessage(Drafts.Personal());
        _ts.Time.AdvanceDays(30); // exactly the lifetime: "strictly older" required

        Assert.Equal(0, Housekeeping.Run(_ts.Store, Defaults).MessagesKilledByAge);
    }

    public static TheoryData<MessageType, string, int> AgeMatrix()
    {
        // type, scenario, lifetime-days-field exercised (all defaults 30; we vary per case to
        // prove each category keys off its own knob).
        return new TheoryData<MessageType, string, int>
        {
            { MessageType.Personal, "read", 10 },
            { MessageType.Personal, "unread", 11 },
            { MessageType.Personal, "forwarded", 12 },
            { MessageType.Personal, "unforwarded", 13 },
            { MessageType.Bulletin, "forwarded", 14 },
            { MessageType.Bulletin, "unforwarded", 15 },
            { MessageType.Traffic, "delivered", 16 },
            { MessageType.Traffic, "forwarded", 17 },
            { MessageType.Traffic, "unforwarded", 18 },
        };
    }

    [Theory]
    [MemberData(nameof(AgeMatrix))]
    public void KillByAge_PerTypeAndStateMatrix(MessageType type, string scenario, int lifetimeDays)
    {
        Message m = _ts.Store.AddMessage(type switch
        {
            MessageType.Personal => Drafts.Personal(to: "G8BPQ"),
            MessageType.Bulletin => Drafts.Bulletin(),
            _ => Drafts.Traffic(to: "32118"),
        });

        switch (scenario)
        {
            case "read":
                _ts.Store.MarkRead(m.Number, m.Recipients[0].ToCall);
                break;
            case "forwarded":
                _ts.Store.EnqueueForwards(m.Number, ["GB7BPQ"]);
                _ts.Store.MarkForwarded(m.Number, "GB7BPQ");
                break;
            case "unforwarded":
                _ts.Store.EnqueueForwards(m.Number, ["GB7BPQ"]);
                break;
            case "delivered":
                _ts.Store.MarkDelivered(m.Number);
                break;
            case "unread":
                break;
            default:
                throw new InvalidOperationException(scenario);
        }

        // Give every category a distinct lifetime; only the exercised one is short.
        var policy = new HousekeepingPolicy
        {
            PersonalReadDays = Pick(type, MessageType.Personal, scenario, "read", lifetimeDays),
            PersonalUnreadDays = Pick(type, MessageType.Personal, scenario, "unread", lifetimeDays),
            PersonalForwardedDays = Pick(type, MessageType.Personal, scenario, "forwarded", lifetimeDays),
            PersonalUnforwardedDays = Pick(type, MessageType.Personal, scenario, "unforwarded", lifetimeDays),
            BulletinForwardedDays = Pick(type, MessageType.Bulletin, scenario, "forwarded", lifetimeDays),
            BulletinUnforwardedDays = Pick(type, MessageType.Bulletin, scenario, "unforwarded", lifetimeDays),
            NtsDeliveredDays = Pick(type, MessageType.Traffic, scenario, "delivered", lifetimeDays),
            NtsForwardedDays = Pick(type, MessageType.Traffic, scenario, "forwarded", lifetimeDays),
            NtsUnforwardedDays = Pick(type, MessageType.Traffic, scenario, "unforwarded", lifetimeDays),
        };

        // Just before the lifetime: survives.
        _ts.Time.AdvanceDays(lifetimeDays - 1);
        Assert.Equal(0, Housekeeping.Run(_ts.Store, policy).MessagesKilledByAge);

        // Just after: killed.
        _ts.Time.AdvanceDays(2);
        HousekeepingSummary summary = Housekeeping.Run(_ts.Store, policy);
        Assert.Equal(1, summary.MessagesKilledByAge);
        Assert.Equal(MessageStatus.Killed, _ts.Store.GetMessage(m.Number)!.Status);

        static int Pick(MessageType actual, MessageType wanted, string scenario, string wantedScenario, int shortDays)
            => actual == wanted && scenario == wantedScenario ? shortDays : 1000;
    }

    [Fact]
    public void HeldMessages_ExemptFromAgeKill()
    {
        // Documented judgment: H sits in the sysop's queue (§2.2) and never expires silently.
        Message held = _ts.Store.AddMessage(Drafts.Personal(hold: true));
        _ts.Time.AdvanceDays(400);

        Assert.Equal(0, Housekeeping.Run(_ts.Store, Defaults).MessagesKilledByAge);
        Assert.Equal(MessageStatus.Held, _ts.Store.GetMessage(held.Number)!.Status);
    }

    [Fact]
    public void BulletinQueuedStatus_AgesAsUnforwarded()
    {
        Message bull = _ts.Store.AddMessage(Drafts.Bulletin());
        _ts.Store.EnqueueForwards(bull.Number, ["GB7BPQ"]);
        Assert.Equal(MessageStatus.BulletinQueued, _ts.Store.GetMessage(bull.Number)!.Status);

        var policy = new HousekeepingPolicy { BulletinUnforwardedDays = 5 };
        _ts.Time.AdvanceDays(6);
        Assert.Equal(1, Housekeeping.Run(_ts.Store, policy).MessagesKilledByAge);
    }

    [Fact]
    public void Summary_CountsAllThreeBuckets()
    {
        Message killed = _ts.Store.AddMessage(Drafts.Personal(subject: "kill me"));
        _ts.Store.Kill(killed.Number);
        _ts.Store.AddMessage(Drafts.Personal(subject: "age me"));
        _ts.Store.RecordBid("STALE_BID");

        _ts.Time.AdvanceDays(61);

        HousekeepingSummary summary = Housekeeping.Run(_ts.Store, Defaults);

        Assert.Equal(1, summary.KilledMessagesPurged);
        Assert.Equal(1, summary.MessagesKilledByAge);
        // Both the explicit record and the messages' own BIDs are >60 days old now.
        Assert.True(summary.BidsPurged >= 1);
        Assert.Null(_ts.Store.LookupBid("STALE_BID"));
    }
}
