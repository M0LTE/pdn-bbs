using Bbs.Core;
using Bbs.Imap;
using Microsoft.Extensions.Time.Testing;

namespace Bbs.Imap.Tests;

/// <summary>
/// One <see cref="BbsStore"/> in its own temp directory with a fake clock, plus message-draft
/// helpers — mirroring <c>Bbs.Core.Tests</c> so the IMAP tests seed the same way the rest of the
/// suite does. Dispose removes the directory.
/// </summary>
internal sealed class TestStore : IDisposable
{
    public const string BbsCall = "GB7PDN";

    private readonly DirectoryInfo _dir;

    public TestStore(string bbsCall = BbsCall)
    {
        _dir = Directory.CreateTempSubdirectory("bbs-imap-test-");
        Time = new FakeTimeProvider(new DateTimeOffset(2026, 6, 11, 12, 0, 0, TimeSpan.Zero));
        Store = BbsStore.Open(Path.Combine(_dir.FullName, "bbs.db"), bbsCall, Time);
    }

    public FakeTimeProvider Time { get; }

    public BbsStore Store { get; }

    public void Dispose()
    {
        Store.Dispose();
        _dir.Delete(recursive: true);
    }
}

/// <summary>Message-draft factories for the IMAP tests (copied shape from Bbs.Core.Tests.Drafts).</summary>
internal static class Drafts
{
    public static MessageDraft Personal(
        string from = "M0LTE",
        string to = "G8BPQ",
        string? at = null,
        string subject = "test message",
        string body = "hello\r")
        => new()
        {
            Type = MessageType.Personal,
            From = from,
            Recipients = [to],
            At = at,
            Subject = subject,
            Body = System.Text.Encoding.Latin1.GetBytes(body),
        };

    public static MessageDraft Bulletin(
        string from = "M0LTE",
        string to = "ALL",
        string subject = "bulletin",
        string body = "notice\r")
        => new()
        {
            Type = MessageType.Bulletin,
            From = from,
            Recipients = [to],
            Subject = subject,
            Body = System.Text.Encoding.Latin1.GetBytes(body),
        };
}
