namespace Bbs.Core;

/// <summary>
/// Visibility and permission rules from compat spec §2.2. The store is mechanical; the
/// Console (and webmail) enforce these before calling mutating store methods.
/// </summary>
public static class MessageRules
{
    /// <summary>
    /// Held-invisible rule: H messages are invisible to non-sysops (compat spec §2.2 "Held —
    /// can't be forwarded, read or killed except by sysop"; §6 "expect held (H) messages to be
    /// invisible non-sysop"). Killed messages are sysop-only too (§2.2 "sysop LK sees it").
    /// </summary>
    public static bool IsVisibleInLists(Message message, bool isSysop)
    {
        ArgumentNullException.ThrowIfNull(message);
        return isSysop || (message.Status != MessageStatus.Held && message.Status != MessageStatus.Killed);
    }

    /// <summary>
    /// Read permission: sysop anything; H/K sysop only (§2.2); P readable by sender or an
    /// addressee ("Message %d not for you" otherwise — §1.3); B and T readable by any user
    /// (§2.1 "T messages are readable by any user").
    /// </summary>
    public static bool CanRead(Message message, string byCall, bool isSysop)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(byCall);

        if (isSysop)
        {
            return true;
        }

        if (message.Status is MessageStatus.Held or MessageStatus.Killed)
        {
            return false;
        }

        return message.Type switch
        {
            MessageType.Personal => IsSenderOrAddressee(message, byCall),
            _ => true,
        };
    }

    /// <summary>
    /// Kill rights per compat spec §2.2 [BPQ-SRC OkToKillMessage]: sysop anything; P by sender
    /// or addressee; B by sender; T by anyone. H can only be killed by sysop.
    /// </summary>
    public static bool CanKill(Message message, string byCall, bool isSysop)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(byCall);

        if (isSysop)
        {
            return true;
        }

        if (message.Status == MessageStatus.Held)
        {
            return false;
        }

        return message.Type switch
        {
            MessageType.Personal => IsSenderOrAddressee(message, byCall),
            MessageType.Bulletin => Callsigns.BaseEquals(message.From, byCall),
            MessageType.Traffic => true,
            _ => false,
        };
    }

    private static bool IsSenderOrAddressee(Message message, string byCall)
    {
        if (Callsigns.BaseEquals(message.From, byCall))
        {
            return true;
        }

        foreach (MessageRecipient recipient in message.Recipients)
        {
            if (Callsigns.BaseEquals(recipient.ToCall, byCall))
            {
                return true;
            }
        }

        return false;
    }
}
