namespace Bbs.Core;

/// <summary>
/// Message type per compat spec §2.1: P personal, B bulletin, T NTS traffic.
/// </summary>
public enum MessageType
{
    /// <summary>P — personal message.</summary>
    Personal,

    /// <summary>B — bulletin.</summary>
    Bulletin,

    /// <summary>T — NTS traffic.</summary>
    Traffic,
}

/// <summary>Wire-letter conversions for <see cref="MessageType"/>.</summary>
public static class MessageTypeExtensions
{
    /// <summary>The single-letter wire code (P/B/T) per compat spec §2.1.</summary>
    public static char ToCode(this MessageType type) => type switch
    {
        MessageType.Personal => 'P',
        MessageType.Bulletin => 'B',
        MessageType.Traffic => 'T',
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    /// <summary>
    /// Forwarding priority order T, P, B per compat spec §2.1 [BPQ-DOC changelog 1.0.4.25].
    /// Lower sorts first.
    /// </summary>
    public static int ForwardPriority(this MessageType type) => type switch
    {
        MessageType.Traffic => 0,
        MessageType.Personal => 1,
        MessageType.Bulletin => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    /// <summary>Parses the single-letter wire code (case-insensitive).</summary>
    public static MessageType MessageTypeFromCode(char code) => char.ToUpperInvariant(code) switch
    {
        'P' => MessageType.Personal,
        'B' => MessageType.Bulletin,
        'T' => MessageType.Traffic,
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Valid message types are P, B and T (compat spec §2.1)."),
    };
}
