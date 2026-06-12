using Bbs.Core;
using Bbs.Imap;

namespace Bbs.Imap.Tests;

public sealed class ImapBackendTests
{
    [Fact]
    public void Authenticate_RightPassword_ReturnsBaseCallsign()
    {
        using var test = new TestStore();
        test.Store.SetMailPassword("M0LTE", "correct horse");
        var backend = new ImapBackend(test.Store);

        Assert.Equal("M0LTE", backend.Authenticate("M0LTE-7", "correct horse"));
        Assert.Null(backend.Authenticate("M0LTE", "wrong"));
        Assert.Null(backend.Authenticate("G0XYZ", "correct horse")); // no password set
    }

    [Fact]
    public void Inbox_HoldsOnlyPersonalsAddressedToTheCallsign()
    {
        using var test = new TestStore();
        test.Store.AddMessage(Drafts.Personal(from: "G0AAA", to: "M0LTE", subject: "one"));
        test.Store.AddMessage(Drafts.Personal(from: "G0BBB", to: "M0LTE", subject: "two"));
        test.Store.AddMessage(Drafts.Personal(from: "G0CCC", to: "G8ZZZ", subject: "not for me"));
        test.Store.AddMessage(Drafts.Bulletin(to: "ALL", subject: "a bulletin"));

        var backend = new ImapBackend(test.Store);
        ImapFolder inbox = backend.ResolveFolder("M0LTE", "INBOX")!;
        ImapMailbox mailbox = backend.OpenMailbox("M0LTE", inbox)!;

        Assert.Equal(2, mailbox.Count);
        Assert.All(mailbox.Messages, h => Assert.Equal(MessageType.Personal, h.Message.Type));
        Assert.DoesNotContain(mailbox.Messages, h => h.Message.Subject == "not for me");
    }

    [Fact]
    public void Inbox_UidEqualsMessageNumber_AndSequenceIsAscending()
    {
        using var test = new TestStore();
        Message first = test.Store.AddMessage(Drafts.Personal(to: "M0LTE", subject: "first"));
        Message second = test.Store.AddMessage(Drafts.Personal(to: "M0LTE", subject: "second"));

        var backend = new ImapBackend(test.Store);
        ImapMailbox mailbox = backend.OpenMailbox("M0LTE", backend.ResolveFolder("M0LTE", "INBOX")!)!;

        Assert.Equal(1, mailbox.Messages[0].Sequence);
        Assert.Equal(first.Number, mailbox.Messages[0].Uid);
        Assert.Equal(2, mailbox.Messages[1].Sequence);
        Assert.Equal(second.Number, mailbox.Messages[1].Uid);
        Assert.Equal(ImapBackend.UidValidity, mailbox.UidValidity);
        Assert.Equal((uint)(test.Store.GetLatestMessageNumber() + 1), mailbox.UidNext);
    }

    [Fact]
    public void Inbox_SeenReflectsRecipientReadState()
    {
        using var test = new TestStore();
        Message unread = test.Store.AddMessage(Drafts.Personal(to: "M0LTE", subject: "unread"));
        Message read = test.Store.AddMessage(Drafts.Personal(to: "M0LTE", subject: "read"));
        test.Store.MarkRead(read.Number, "M0LTE");

        var backend = new ImapBackend(test.Store);
        ImapMailbox mailbox = backend.OpenMailbox("M0LTE", backend.ResolveFolder("M0LTE", "INBOX")!)!;

        Assert.False(mailbox.ByUid(unread.Number)!.Seen);
        Assert.True(mailbox.ByUid(read.Number)!.Seen);
        Assert.Equal(1, mailbox.UnseenCount);
        Assert.Equal(1, mailbox.FirstUnseenSequence);
    }

    [Fact]
    public void Bulletins_EnumeratesDistinctCategories_AsSelectableLeaves()
    {
        using var test = new TestStore();
        test.Store.AddMessage(Drafts.Bulletin(to: "ALL", subject: "all-1"));
        test.Store.AddMessage(Drafts.Bulletin(to: "NEWS", subject: "news-1"));
        test.Store.AddMessage(Drafts.Bulletin(to: "ALL", subject: "all-2"));

        var backend = new ImapBackend(test.Store);
        IReadOnlyList<ImapFolder> folders = backend.ListFolders("M0LTE");

        Assert.Contains(folders, f => f is { Kind: ImapFolderKind.Inbox, Name: "INBOX", Selectable: true });
        Assert.Contains(folders, f => f is { Kind: ImapFolderKind.BulletinsRoot, Name: "Bulletins", Selectable: false });
        Assert.Contains(folders, f => f is { Name: "Bulletins/ALL", Selectable: true });
        Assert.Contains(folders, f => f is { Name: "Bulletins/NEWS", Selectable: true });

        ImapMailbox all = backend.OpenMailbox("M0LTE", backend.ResolveFolder("M0LTE", "Bulletins/ALL")!)!;
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public void Bulletins_AreAlwaysReportedSeen_NamedLimitation()
    {
        // Per-user bulletin read-state cannot be stored in the current schema (the recipient is the
        // category, not the user), so bulletins are reported always-\Seen. See ImapBackend remarks.
        using var test = new TestStore();
        test.Store.AddMessage(Drafts.Bulletin(to: "NEWS", subject: "fresh"));

        var backend = new ImapBackend(test.Store);
        ImapMailbox news = backend.OpenMailbox("M0LTE", backend.ResolveFolder("M0LTE", "Bulletins/NEWS")!)!;

        Assert.True(news.Messages[0].Seen);
        Assert.Equal(0, news.UnseenCount);
    }

    [Fact]
    public void SevenPlusParts_AreHiddenFromInboxAndBulletinCategories()
    {
        using var test = new TestStore();
        Message part = test.Store.AddMessage(Drafts.Personal(to: "M0LTE", subject: "7plus part"));
        test.Store.AddMessage(Drafts.Personal(to: "M0LTE", subject: "normal"));
        // Record the first message as a 7plus source part so it is hidden.
        test.Store.RecordSevenPlusPart("file-key", "FIELDS.JPG", fileSize: 1000, totalParts: 3, blockLines: 10,
            partNumber: 1, sourceMessageNumber: part.Number);

        var backend = new ImapBackend(test.Store);
        ImapMailbox inbox = backend.OpenMailbox("M0LTE", backend.ResolveFolder("M0LTE", "INBOX")!)!;

        Assert.Single(inbox.Messages);
        Assert.Equal("normal", inbox.Messages[0].Message.Subject);
    }

    [Fact]
    public void ResolveFolder_UnknownMailbox_ReturnsNull()
    {
        using var test = new TestStore();
        var backend = new ImapBackend(test.Store);

        Assert.NotNull(backend.ResolveFolder("M0LTE", "inbox")); // INBOX is case-insensitive
        Assert.Null(backend.ResolveFolder("M0LTE", "Bulletins/NOPE"));
        Assert.Null(backend.ResolveFolder("M0LTE", "Sent"));
        Assert.False(backend.ResolveFolder("M0LTE", "Bulletins")!.Selectable);
    }
}
