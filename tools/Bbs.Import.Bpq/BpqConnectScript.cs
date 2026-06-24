namespace Bbs.Import.Bpq;

/// <summary>
/// Normalises a BPQMail forwarding connect-script line into the form pdn-bbs's
/// <c>ConnectScript</c> parser expects, AT IMPORT (Tom's call: mutate the scripts here, not at
/// runtime — pdn's strict <c>C</c> already negotiates, so the parser shouldn't learn BPQ dialects).
///
/// Two BPQ node-command idioms appear in the GB7RDG scripts that pdn's <c>C</c>-only parser would
/// otherwise mis-handle:
/// <list type="bullet">
/// <item><c>NC</c> ("node connect", a negotiated-connect verb) -&gt; <c>C</c> — pdn's <c>C</c> covers it.</item>
/// <item>a leading <c>!</c> on the connect target (BPQ's direct/no-alias flag, e.g. <c>NC 3 !GB7BPQ</c>)
/// -&gt; stripped; RHP's open dials the bare callsign through the node's routing, the only thing it can do.</item>
/// </list>
/// Everything else (plain <c>C</c>, <c>PAUSE</c>, <c>INTERLOCK</c>, the <c>EXPECT=SEND</c> form, a remote
/// command like <c>C uhf gb7cip</c> or <c>bbs</c>) passes through untouched — those are handled (or warned)
/// by the runtime parser. The exact <c>EXPECT=</c> tightening is a separate, live step (the cutover
/// connect-test), not an import concern.
/// </summary>
internal static class BpqConnectScript
{
    public static string Translate(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return raw;
        }

        string[] tokens = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return raw;
        }

        bool isConnect = tokens[0].Equals("C", StringComparison.OrdinalIgnoreCase)
            || tokens[0].Equals("NC", StringComparison.OrdinalIgnoreCase);
        if (!isConnect)
        {
            return raw; // PAUSE / INTERLOCK / bbs / EXPECT=SEND / anything else — leave verbatim.
        }

        // NC -> C (pdn's C negotiates).
        tokens[0] = "C";

        // Strip a single leading "!" from the target (after an optional numeric port: C [port] target).
        int targetIndex = tokens.Length >= 3 && tokens[1].All(char.IsAsciiDigit) ? 2 : 1;
        if (targetIndex < tokens.Length && tokens[targetIndex].StartsWith('!'))
        {
            tokens[targetIndex] = tokens[targetIndex][1..];
        }

        return string.Join(' ', tokens);
    }
}
