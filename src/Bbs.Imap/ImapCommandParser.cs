using System.Globalization;

namespace Bbs.Imap;

/// <summary>
/// Splits an assembled IMAP command line (as produced by <see cref="ImapConnection.ReadCommandAsync"/>,
/// with any literals already spliced in) into its tokens: the tag, the command word, and the argument
/// atoms / quoted-strings / literal payloads. A parenthesised list (<c>(\Seen \Deleted)</c>, a FETCH
/// item list) is returned as a single token with its surrounding parentheses, for the command handler
/// to sub-parse.
/// </summary>
/// <remarks>
/// IMAP token kinds (RFC 3501 §9): an <c>atom</c> (unquoted run of non-special chars), a
/// <c>quoted</c> string (<c>"..."</c> with <c>\</c> escapes), or a <c>literal</c> (<c>{n}CRLF</c>
/// then n octets). The literal is recognised here by its <c>{n}\r\n</c> marker — exactly the form the
/// connection spliced in — and the following n chars become one token. Parentheses balance so a list
/// argument is captured whole.
/// </remarks>
public static class ImapCommandParser
{
    /// <summary>
    /// Tokenises <paramref name="line"/>. Returns false only for an unterminated quoted-string,
    /// unbalanced parentheses, or a malformed literal marker — a clean signal to answer <c>BAD</c>.
    /// Whitespace between tokens is consumed; a literal token's value is the raw payload chars.
    /// </summary>
    public static bool TryTokenize(string line, out IReadOnlyList<ImapToken> tokens)
    {
        var result = new List<ImapToken>();
        tokens = result;
        int i = 0;

        while (i < line.Length)
        {
            char c = line[i];
            if (c is ' ' or '\r' or '\n')
            {
                i++;
                continue;
            }

            if (c == '"')
            {
                if (!TryReadQuoted(line, ref i, out string quoted))
                {
                    return false;
                }

                result.Add(new ImapToken(ImapTokenKind.Quoted, quoted));
                continue;
            }

            if (c == '{')
            {
                if (!TryReadLiteral(line, ref i, out string literal))
                {
                    return false;
                }

                result.Add(new ImapToken(ImapTokenKind.Literal, literal));
                continue;
            }

            if (c == '(')
            {
                if (!TryReadParenList(line, ref i, out string list))
                {
                    return false;
                }

                result.Add(new ImapToken(ImapTokenKind.List, list));
                continue;
            }

            // An atom: a run up to the next space, quote, paren or end. (We keep '[' ']' inside the
            // atom so a FETCH item like BODY[HEADER] stays one token.)
            int start = i;
            while (i < line.Length && line[i] is not (' ' or '\r' or '\n' or '"' or '(' or ')'))
            {
                i++;
            }

            result.Add(new ImapToken(ImapTokenKind.Atom, line[start..i]));
        }

        return true;
    }

    private static bool TryReadQuoted(string line, ref int i, out string value)
    {
        value = string.Empty;
        var sb = new System.Text.StringBuilder();
        i++; // opening quote
        while (i < line.Length)
        {
            char c = line[i++];
            if (c == '\\' && i < line.Length)
            {
                sb.Append(line[i++]);
                continue;
            }

            if (c == '"')
            {
                value = sb.ToString();
                return true;
            }

            sb.Append(c);
        }

        return false; // unterminated
    }

    private static bool TryReadLiteral(string line, ref int i, out string value)
    {
        value = string.Empty;
        int close = line.IndexOf('}', i);
        if (close < 0)
        {
            return false;
        }

        string inner = line[(i + 1)..close];
        if (inner.EndsWith('+'))
        {
            inner = inner[..^1];
        }

        if (!int.TryParse(inner, NumberStyles.None, CultureInfo.InvariantCulture, out int count))
        {
            return false;
        }

        int payloadStart = close + 1;

        // Skip the CRLF that followed the {n} marker when the connection spliced the literal.
        if (payloadStart < line.Length && line[payloadStart] == '\r')
        {
            payloadStart++;
        }

        if (payloadStart < line.Length && line[payloadStart] == '\n')
        {
            payloadStart++;
        }

        if (payloadStart + count > line.Length)
        {
            return false; // the literal claims more octets than the line carries
        }

        value = line.Substring(payloadStart, count);
        i = payloadStart + count;
        return true;
    }

    private static bool TryReadParenList(string line, ref int i, out string value)
    {
        value = string.Empty;
        int depth = 0;
        int start = i;
        while (i < line.Length)
        {
            char c = line[i];
            if (c == '"')
            {
                if (!TryReadQuoted(line, ref i, out _))
                {
                    return false;
                }

                continue;
            }

            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth--;
                if (depth == 0)
                {
                    i++;
                    value = line[start..i];
                    return true;
                }
            }

            i++;
        }

        return false; // unbalanced
    }
}

/// <summary>The kind of a tokenised IMAP argument.</summary>
public enum ImapTokenKind
{
    /// <summary>An unquoted atom (a keyword, number, flag, sequence-set, or FETCH item).</summary>
    Atom,

    /// <summary>A quoted-string (its surrounding quotes and escapes removed).</summary>
    Quoted,

    /// <summary>A literal payload (its raw octet chars).</summary>
    Literal,

    /// <summary>A parenthesised list, captured whole including its outer parentheses.</summary>
    List,
}

/// <summary>One tokenised IMAP argument: its <see cref="Kind"/> and decoded <see cref="Value"/>.</summary>
/// <param name="Kind">The token kind.</param>
/// <param name="Value">The decoded value (quotes/escapes removed; a list keeps its parentheses).</param>
public sealed record ImapToken(ImapTokenKind Kind, string Value);
