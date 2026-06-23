namespace Bbs.Import.Bpq;

/// <summary>
/// The node-layer facts the importer cross-checks from <c>bpq32.cfg</c>: the node callsign and the
/// BBS application's callsign + alias. bpq32.cfg is a flat keyword file (NOT libconfig); lines are
/// <c>KEYWORD=value</c> or <c>KEYWORD args</c>, with <c>;</c> starting a comment.
/// Example: <c>APPLICATION 1,BBS,,GB7RDG-2,RDGBBS,255</c> → BBS call GB7RDG-2, alias RDGBBS.
/// </summary>
internal sealed record Bpq32NodeInfo
{
    /// <summary>NODECALL value, or null if absent.</summary>
    public required string? NodeCall { get; init; }

    /// <summary>The BBS application's callsign (APPLICATION ... ,BBS, ... ,&lt;Call&gt;,...), or null.</summary>
    public required string? BbsCall { get; init; }

    /// <summary>The BBS application's alias, or null.</summary>
    public required string? BbsAlias { get; init; }
}

/// <summary>Parses the subset of <c>bpq32.cfg</c> the importer needs.</summary>
internal static class Bpq32CfgParser
{
    /// <summary>Reads and parses a bpq32.cfg file from disk.</summary>
    public static Bpq32NodeInfo Read(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Parse(File.ReadAllText(path));
    }

    /// <summary>Parses bpq32.cfg text. Exposed for unit testing.</summary>
    public static Bpq32NodeInfo Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string? nodeCall = null;
        string? bbsCall = null;
        string? bbsAlias = null;

        foreach (string rawLine in text.Split('\n'))
        {
            string line = StripComment(rawLine).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("NODECALL", StringComparison.OrdinalIgnoreCase))
            {
                nodeCall = AfterEquals(line);
            }
            else if (line.StartsWith("APPLICATION", StringComparison.OrdinalIgnoreCase) && bbsCall is null)
            {
                // APPLICATION n,CMD,NewCommand,Call,Alias,Quality[,L2Alias]
                string args = line["APPLICATION".Length..].Trim();
                string[] f = args.Split(',');
                if (f.Length >= 4 && string.Equals(f[1].Trim(), "BBS", StringComparison.OrdinalIgnoreCase))
                {
                    bbsCall = NullIfEmpty(f[3].Trim());
                    bbsAlias = f.Length >= 5 ? NullIfEmpty(f[4].Trim()) : null;
                }
            }
        }

        return new Bpq32NodeInfo { NodeCall = nodeCall, BbsCall = bbsCall, BbsAlias = bbsAlias };
    }

    private static string StripComment(string line)
    {
        int semi = line.IndexOf(';', StringComparison.Ordinal);
        return semi < 0 ? line : line[..semi];
    }

    private static string? AfterEquals(string line)
    {
        int eq = line.IndexOf('=', StringComparison.Ordinal);
        return eq < 0 ? null : NullIfEmpty(line[(eq + 1)..].Trim());
    }

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;
}
