namespace Bbs.Core;

/// <summary>
/// Message status per compat spec §2.2 (verbatim list from [BPQ-DOC MailServer]):
/// N not read or forwarded; Y has been read; $ bulletin that still has stations to be
/// forwarded to; F has been forwarded to all stations; K killed (remains on disk until
/// housekeeping removes it); H held (can't be forwarded, read or killed except by sysop);
/// D delivered (NTS only). There is no archive status.
/// </summary>
public enum MessageStatus
{
    /// <summary>N — not read or forwarded.</summary>
    Unread,

    /// <summary>Y — has been read.</summary>
    Read,

    /// <summary>$ — bulletin that still has stations to be forwarded to.</summary>
    BulletinQueued,

    /// <summary>F — has been forwarded to all stations.</summary>
    Forwarded,

    /// <summary>K — killed; remains on disk until housekeeping removes it (sysop LK sees it).</summary>
    Killed,

    /// <summary>H — held; can't be forwarded, read or killed except by sysop.</summary>
    Held,

    /// <summary>D — delivered (NTS only).</summary>
    Delivered,
}

/// <summary>Wire-letter conversions for <see cref="MessageStatus"/>.</summary>
public static class MessageStatusExtensions
{
    /// <summary>The single-letter wire code (N/Y/$/F/K/H/D) per compat spec §2.2.</summary>
    public static char ToCode(this MessageStatus status) => status switch
    {
        MessageStatus.Unread => 'N',
        MessageStatus.Read => 'Y',
        MessageStatus.BulletinQueued => '$',
        MessageStatus.Forwarded => 'F',
        MessageStatus.Killed => 'K',
        MessageStatus.Held => 'H',
        MessageStatus.Delivered => 'D',
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    /// <summary>Parses the single-letter wire code (case-insensitive for letters).</summary>
    public static MessageStatus MessageStatusFromCode(char code) => char.ToUpperInvariant(code) switch
    {
        'N' => MessageStatus.Unread,
        'Y' => MessageStatus.Read,
        '$' => MessageStatus.BulletinQueued,
        'F' => MessageStatus.Forwarded,
        'K' => MessageStatus.Killed,
        'H' => MessageStatus.Held,
        'D' => MessageStatus.Delivered,
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Valid statuses are N Y $ F K H D (compat spec §2.2)."),
    };
}
