using System.Text;

namespace Bbs.Host.Sessions;

/// <summary>
/// Incremental Latin-1 line splitter tolerant of CR, LF and CRLF terminators (the RF side
/// is CR-discipline; telnet-ish peers send CRLF — IBbsTerminal contract). A CRLF pair
/// yields one line even when split across feeds. NUL (0x00) is dropped: telnet NVT (RFC 854)
/// defines NUL as a no-op and transmits a bare carriage return as CR-NUL, so a strict telnet
/// client ends every line with CR-NUL — without dropping it the trailing NUL would prefix the
/// NEXT line (e.g. <c>"help\r\0help\r\0"</c> → the second line becomes <c>"\0help"</c>, an
/// unknown command that prints as "help" because the NUL is invisible).
/// </summary>
public sealed class LineAssembler
{
    private readonly StringBuilder _current = new();
    private bool _skipNextLf;

    /// <summary>Feeds bytes; returns the lines completed by this chunk (terminators stripped).</summary>
    public IReadOnlyList<string> Feed(ReadOnlySpan<byte> data)
    {
        List<string>? lines = null;
        foreach (byte b in data)
        {
            if (b == 0x00)
            {
                _skipNextLf = false; // a CR-NUL bare carriage return is complete; the NUL is a no-op
                continue;            // NVT NUL is never line content
            }

            if (_skipNextLf)
            {
                _skipNextLf = false;
                if (b == 0x0A)
                {
                    continue;
                }
            }

            if (b == 0x0D)
            {
                (lines ??= []).Add(Take());
                _skipNextLf = true;
            }
            else if (b == 0x0A)
            {
                (lines ??= []).Add(Take());
            }
            else
            {
                _current.Append((char)b); // Latin-1: byte == code point
            }
        }

        return lines ?? (IReadOnlyList<string>)[];
    }

    private string Take()
    {
        string line = _current.ToString();
        _current.Clear();
        return line;
    }
}
