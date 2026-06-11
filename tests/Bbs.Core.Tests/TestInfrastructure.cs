namespace Bbs.Core.Tests;

/// <summary>Deterministic, manually-advanced clock for TimeProvider-driven code under test.</summary>
internal sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow = new(2026, 6, 11, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan delta) => _utcNow += delta;

    public void AdvanceDays(double days) => _utcNow += TimeSpan.FromDays(days);
}

/// <summary>
/// One store in its own temp directory (Directory.CreateTempSubdirectory per test; no shared
/// state) with a fake clock. Dispose removes the directory.
/// </summary>
internal sealed class TestStore : IDisposable
{
    public const string OwnCall = "GB7PDN";

    private readonly DirectoryInfo _dir;

    public TestStore(string bbsCall = OwnCall)
    {
        _dir = Directory.CreateTempSubdirectory("bbs-core-test-");
        DbPath = Path.Combine(_dir.FullName, "bbs.db");
        Time = new FakeTimeProvider();
        Store = BbsStore.Open(DbPath, bbsCall, Time);
    }

    public string DbPath { get; }

    public FakeTimeProvider Time { get; }

    public BbsStore Store { get; private set; }

    /// <summary>Closes and reopens the store on the same file (migration idempotence, persistence).</summary>
    public BbsStore Reopen(string bbsCall = OwnCall)
    {
        Store.Dispose();
        Store = BbsStore.Open(DbPath, bbsCall, Time);
        return Store;
    }

    /// <summary>Opens a second, independent store instance on the same file (WAL concurrent access).</summary>
    public BbsStore OpenSecond(string bbsCall = OwnCall) => BbsStore.Open(DbPath, bbsCall, Time);

    public void Dispose()
    {
        Store.Dispose();
        _dir.Delete(recursive: true);
    }
}

internal static class Drafts
{
    public static MessageDraft Personal(
        string from = "M0LTE",
        string to = "G8BPQ",
        string? at = null,
        string? bid = null,
        string subject = "test message",
        string body = "hello\r",
        string? receivedFrom = null,
        bool hold = false)
        => new()
        {
            Type = MessageType.Personal,
            From = from,
            Recipients = [to],
            At = at,
            Bid = bid,
            Subject = subject,
            Body = System.Text.Encoding.Latin1.GetBytes(body),
            ReceivedFrom = receivedFrom,
            Hold = hold,
        };

    public static MessageDraft Bulletin(
        string from = "M0LTE",
        string to = "ALL",
        string? at = null,
        string? bid = null,
        string subject = "bulletin",
        string? receivedFrom = null)
        => Personal(from, to, at, bid, subject, receivedFrom: receivedFrom) with { Type = MessageType.Bulletin };

    public static MessageDraft Traffic(
        string from = "M0LTE",
        string to = "32118",
        string? at = null,
        string? bid = null)
        => Personal(from, to, at, bid, subject: "QTC 1") with { Type = MessageType.Traffic };
}
