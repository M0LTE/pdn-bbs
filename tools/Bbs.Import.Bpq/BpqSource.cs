using System.Globalization;

namespace Bbs.Import.Bpq;

/// <summary>
/// A fully-parsed, in-memory view of a BPQMail dump directory: message headers + bodies, the BID
/// dedup store, the config (partners/users), and the BBSNumber→partner-call map needed to decode
/// the per-message forward bitmaps. Loading is read-only; the BPQ source is never modified (this is
/// what makes "rollback = rebuild/discard" true by construction).
/// </summary>
internal sealed class BpqSource
{
    private BpqSource(
        string directory,
        BpqMailConfig config,
        Bpq32NodeInfo? node,
        DirmesReader.Result dirmes,
        WfbidReader.Result wfbid,
        IReadOnlyDictionary<int, string> bbsNumberToPartner,
        IReadOnlyDictionary<int, BpqMessageHeader> bodyMissingByNumber,
        IReadOnlyList<int> orphanBodies,
        IReadOnlyList<string> warnings)
    {
        Directory = directory;
        Config = config;
        Node = node;
        Dirmes = dirmes;
        Wfbid = wfbid;
        BbsNumberToPartner = bbsNumberToPartner;
        OrphanHeaders = bodyMissingByNumber;
        OrphanBodies = orphanBodies;
        Warnings = warnings;
    }

    public string Directory { get; }
    public BpqMailConfig Config { get; }
    public Bpq32NodeInfo? Node { get; }
    public DirmesReader.Result Dirmes { get; }
    public WfbidReader.Result Wfbid { get; }

    /// <summary>BBSNumber → partner callsign, from the BBSUsers F_BBS records (the bitmap decode key — Rule 2).</summary>
    public IReadOnlyDictionary<int, string> BbsNumberToPartner { get; }

    /// <summary>Headers whose <c>m_%06d.mes</c> body file is missing (number → header).</summary>
    public IReadOnlyDictionary<int, BpqMessageHeader> OrphanHeaders { get; }

    /// <summary>Body files (<c>m_%06d.mes</c>) on disk with no matching DIRMES header (already-purged messages).</summary>
    public IReadOnlyList<int> OrphanBodies { get; }

    /// <summary>All warnings accumulated while loading.</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>Loads and validates a BPQMail dump directory. Throws only on a fundamentally unusable dump.</summary>
    public static BpqSource Load(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        var warnings = new List<string>();

        if (!System.IO.Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"BPQ dump directory not found: {directory}");
        }

        string dirmesPath = RequireFile(directory, "DIRMES.SYS");
        string wfbidPath = Path.Combine(directory, "WFBID.SYS");
        string linmailPath = RequireFile(directory, "linmail.cfg");
        string mailDir = Path.Combine(directory, "Mail");

        DirmesReader.Result dirmes = DirmesReader.Read(dirmesPath);
        warnings.AddRange(dirmes.Warnings);

        WfbidReader.Result wfbid;
        if (File.Exists(wfbidPath))
        {
            wfbid = WfbidReader.Read(wfbidPath);
            warnings.AddRange(wfbid.Warnings);
        }
        else
        {
            warnings.Add("WFBID.SYS not found; the BID dedup store will be empty (network re-flood risk if any live BIDs exist).");
            wfbid = new WfbidReader.Result(WfbidReader.Size64, 0, [], []);
        }

        BpqMailConfig config = LinmailConfigParser.Read(linmailPath);

        Bpq32NodeInfo? node = null;
        string bpq32Path = Path.Combine(directory, "bpq32.cfg");
        if (File.Exists(bpq32Path) && new FileInfo(bpq32Path).Length > 0)
        {
            node = Bpq32CfgParser.Read(bpq32Path);
        }
        else
        {
            string bak = Path.Combine(directory, "bpq32.cfg.bak");
            if (File.Exists(bak))
            {
                node = Bpq32CfgParser.Read(bak);
            }
        }

        // Cross-check the BBS callsign between linmail.cfg and bpq32.cfg's BBS APPLICATION line.
        if (node?.BbsCall is { } bbsCall && config.BbsName.Length > 0)
        {
            string nodeBase = Bbs.Core.Callsigns.StripSsid(bbsCall.ToUpperInvariant());
            string cfgBase = Bbs.Core.Callsigns.StripSsid(config.BbsName.ToUpperInvariant());
            if (!string.Equals(nodeBase, cfgBase, StringComparison.Ordinal))
            {
                warnings.Add(
                    $"BBS callsign mismatch: linmail.cfg BBSName='{config.BbsName}' but bpq32.cfg BBS application call='{bbsCall}'. " +
                    $"Using BBSName for the import identity.");
            }
        }

        // Inventory body files + match against headers.
        var bodiesOnDisk = new HashSet<int>();
        if (System.IO.Directory.Exists(mailDir))
        {
            foreach (string f in System.IO.Directory.EnumerateFiles(mailDir, "m_*.mes"))
            {
                string name = Path.GetFileNameWithoutExtension(f); // m_000123
                if (name.Length == 8 &&
                    int.TryParse(name.AsSpan(2), NumberStyles.Integer, CultureInfo.InvariantCulture, out int num))
                {
                    bodiesOnDisk.Add(num);
                }
            }
        }
        else
        {
            warnings.Add($"Mail/ directory not found at {mailDir}; all message bodies will be empty.");
        }

        var orphanHeaders = new Dictionary<int, BpqMessageHeader>();
        var headerNumbers = new HashSet<int>();
        foreach (BpqMessageHeader h in dirmes.Messages)
        {
            headerNumbers.Add(h.Number);
            if (!bodiesOnDisk.Contains(h.Number))
            {
                orphanHeaders[h.Number] = h;
            }
        }

        var orphanBodies = bodiesOnDisk.Where(n => !headerNumbers.Contains(n)).OrderBy(n => n).ToList();

        if (orphanHeaders.Count > 0)
        {
            warnings.Add(
                $"{orphanHeaders.Count} message header(s) have no m_*.mes body file (e.g. " +
                $"{string.Join(", ", orphanHeaders.Keys.Order().Take(8))}); they will be imported with an empty body.");
        }

        if (orphanBodies.Count > 0)
        {
            warnings.Add(
                $"{orphanBodies.Count} m_*.mes body file(s) have no DIRMES header (already purged from the catalogue); " +
                $"they are ignored (importing them would resurrect deleted mail).");
        }

        Dictionary<int, string> bbsNumberToPartner = BuildBbsNumberMap(config, warnings);

        return new BpqSource(directory, config, node, dirmes, wfbid, bbsNumberToPartner, orphanHeaders, orphanBodies, warnings);
    }

    /// <summary>Reads a message body file as raw bytes; returns empty for an orphan header.</summary>
    public byte[] ReadBody(int messageNumber)
    {
        string path = Path.Combine(
            Directory,
            "Mail",
            string.Create(CultureInfo.InvariantCulture, $"m_{messageNumber:D6}.mes"));
        return File.Exists(path) ? File.ReadAllBytes(path) : [];
    }

    private static Dictionary<int, string> BuildBbsNumberMap(BpqMailConfig config, List<string> warnings)
    {
        var map = new Dictionary<int, string>();
        foreach (BpqUser u in config.Users)
        {
            if (!u.IsBbs || u.BbsNumber == 0)
            {
                continue;
            }

            if (map.TryGetValue(u.BbsNumber, out string? existing) &&
                !string.Equals(existing, u.Call, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add(
                    $"Duplicate BBSNumber {u.BbsNumber} maps to both '{existing}' and '{u.Call}'. The forward bitmaps are " +
                    $"ambiguous for this bit; keeping '{u.Call}' (last wins). Verify the partner mapping before cutover.");
            }

            map[u.BbsNumber] = u.Call;
        }

        return map;
    }

    private static string RequireFile(string directory, string name)
    {
        string path = Path.Combine(directory, name);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Required BPQ file not found: {name} (in {directory})", path);
        }

        return path;
    }
}
