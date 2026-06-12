using System.Globalization;

namespace Bbs.Imap;

/// <summary>
/// A parsed IMAP sequence-set (RFC 3501 §9 <c>sequence-set</c>): a comma-separated list of
/// numbers and <c>n:m</c> ranges, where <c>*</c> denotes the largest value in use (the highest
/// message sequence number, or the highest UID). The same grammar serves both a message-sequence
/// set (<c>FETCH</c>/<c>STORE</c>) and a UID set (<c>UID FETCH</c>/<c>UID STORE</c>); the caller
/// supplies the meaning of <c>*</c> and the universe to test against.
/// </summary>
/// <remarks>
/// A range is order-independent — <c>5:3</c> means the same set as <c>3:5</c> (RFC 3501 §9:
/// "the order of the seq-numbers ... is irrelevant"). <c>*</c> is the largest in-use value, so
/// <c>*:4</c> against a 9-message mailbox is <c>4:9</c>. The parser is total over the grammar and
/// rejects anything outside it (empty atoms, non-numeric tokens, a bare <c>0</c>) so a malformed
/// set is a clean protocol <c>BAD</c>, never a partial fetch.
/// </remarks>
public static class ImapSequenceSet
{
    /// <summary>
    /// Parses <paramref name="text"/> into the concrete, ascending, de-duplicated set of values it
    /// selects, given <paramref name="star"/> as the value of <c>*</c>. Returns false (with an empty
    /// list) when <paramref name="text"/> is not a well-formed sequence-set, or when a numeric token
    /// is <c>0</c> or overflows (neither is a valid sequence/UID value — RFC 3501 §9 <c>nz-number</c>).
    /// The values are not range-checked against any mailbox here; the caller intersects with what
    /// actually exists (a set may legitimately name values past the end — <c>1:*</c> is the common case).
    /// </summary>
    /// <param name="text">The raw sequence-set token (e.g. <c>1</c>, <c>2:4</c>, <c>1:*</c>, <c>*</c>, <c>1,3,5:7</c>).</param>
    /// <param name="star">The value substituted for <c>*</c> (the largest sequence number or UID in use).</param>
    /// <param name="values">On success, the selected values in ascending order with no duplicates.</param>
    public static bool TryParse(string? text, long star, out IReadOnlyList<long> values)
    {
        values = [];
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var set = new SortedSet<long>();
        foreach (string atom in text.Split(','))
        {
            if (atom.Length == 0)
            {
                return false; // an empty atom (leading/trailing/double comma) is malformed
            }

            int colon = atom.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0)
            {
                if (!TryValue(atom, star, out long single))
                {
                    return false;
                }

                set.Add(single);
                continue;
            }

            // A range "lo:hi" — exactly one colon, both sides present.
            string loText = atom[..colon];
            string hiText = atom[(colon + 1)..];
            if (hiText.Contains(':', StringComparison.Ordinal)
                || !TryValue(loText, star, out long lo)
                || !TryValue(hiText, star, out long hi))
            {
                return false;
            }

            // Order-independent: 5:3 == 3:5 (RFC 3501 §9).
            if (lo > hi)
            {
                (lo, hi) = (hi, lo);
            }

            for (long v = lo; v <= hi; v++)
            {
                set.Add(v);
            }
        }

        values = [.. set];
        return true;
    }

    /// <summary>Parses one sequence-set element: a positive number, or <c>*</c> ⇒ <paramref name="star"/>.</summary>
    private static bool TryValue(string token, long star, out long value)
    {
        if (token == "*")
        {
            value = star;
            return true;
        }

        if (long.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value > 0)
        {
            return true;
        }

        value = 0;
        return false;
    }
}
