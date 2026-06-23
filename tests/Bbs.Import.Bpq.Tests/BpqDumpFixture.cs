using System.Globalization;
using System.Text;
using static Bbs.Import.Bpq.Tests.BpqBinaryBuilders;

namespace Bbs.Import.Bpq.Tests;

/// <summary>
/// Builds a self-consistent BPQMail dump directory in a temp folder (DIRMES.SYS + WFBID.SYS +
/// linmail.cfg + Mail/m_*.mes), so the importer integration tests run against data we control
/// end-to-end. Disposing deletes the temp directory.
/// </summary>
internal sealed class BpqDumpFixture : IDisposable
{
    private readonly DirectoryInfo _dir;

    public BpqDumpFixture()
    {
        _dir = Directory.CreateTempSubdirectory("bpq-import-fixture-");
        Directory.CreateDirectory(Path.Combine(_dir.FullName, "Mail"));
    }

    public string Dir => _dir.FullName;

    public void WriteDirmes(byte[] image) => File.WriteAllBytes(Path.Combine(_dir.FullName, "DIRMES.SYS"), image);

    public void WriteWfbid(byte[] image) => File.WriteAllBytes(Path.Combine(_dir.FullName, "WFBID.SYS"), image);

    public void WriteLinmail(string text) => File.WriteAllText(Path.Combine(_dir.FullName, "linmail.cfg"), text);

    public void WriteBody(int number, string text)
        => File.WriteAllText(
            Path.Combine(_dir.FullName, "Mail", string.Create(CultureInfo.InvariantCulture, $"m_{number:D6}.mes")),
            text,
            new UTF8Encoding(false));

    public void Dispose() => _dir.Delete(recursive: true);

    /// <summary>
    /// A consistent reference dump for GB7RDG: two partners (GB7BSK=BBSNumber 1, GB7CIP=BBSNumber 5),
    /// one human user, and four messages exercising verbatim BIDs, an orphan body, forw/fbbs legs,
    /// and several statuses. WFBID carries an extra orphan BID (message already gone, within lifetime).
    /// </summary>
    public static BpqDumpFixture BuildReference()
    {
        var f = new BpqDumpFixture();

        var messages = new List<MsgSpec>
        {
            // #100 bulletin, forwarded to GB7BSK(1), still queued to GB7CIP(5).
            new()
            {
                Type = 'B', Status = '$', Number = 100, From = "LU9DCE", To = "NEWS",
                Bid = "14986_LU9DCE", Title = "World news", Via = "WW",
                DateReceived = 1782000000, DateCreated = 1781990000, DateChanged = 1782010000,
                ForwBits = [1], FbbsBits = [5],
            },
            // #101 personal, read, fully forwarded to GB7BSK(1).
            new()
            {
                Type = 'P', Status = 'Y', Number = 101, From = "M0LTE", To = "G8BPQ",
                Bid = "101_GB7RDG", Title = "Hello", Via = "G8BPQ.#23.GBR.EU",
                DateReceived = 1782001000, DateCreated = 1782000500, DateChanged = 1782002000,
                ForwBits = [1],
            },
            // #102 killed bulletin (orphan body: no m_*.mes will be written for it).
            new()
            {
                Type = 'B', Status = 'K', Number = 102, From = "KC2NJV", To = "WILDLF",
                Bid = "6323_KC2NJV", Title = "Killed bull",
                DateReceived = 1781000000, DateCreated = 1780990000, DateChanged = 1781500000,
            },
            // #103 NTS traffic, delivered.
            new()
            {
                Type = 'T', Status = 'D', Number = 103, From = "W1AW", To = "12345",
                Bid = "103_GB7RDG", Title = "NTS QSP",
                DateReceived = 1782005000, DateCreated = 1782004000, DateChanged = 1782006000,
            },
        };

        f.WriteDirmes(BuildDirmesNew(latestNumber: 1119, messages));

        // Bodies for all but #102 (deliberate orphan header). Plus an orphan body #777 (no header).
        f.WriteBody(100, "R:260601/0000Z 100@GB7RDG.#42.GBR.EURO LinBPQ6.0.25\r\n\r\nWorld news body.\r\n");
        f.WriteBody(101, "R:260601/0010Z 101@GB7RDG.#42.GBR.EURO LinBPQ6.0.25\r\n\r\nHi there.\r\n");
        f.WriteBody(103, "MID: 103_GB7RDG\r\nBody: 5\r\n\r\nQSP\r\n");
        f.WriteBody(777, "An orphaned body with no header - must be ignored.\r\n");

        // WFBID: the three live-message BIDs plus an orphan BID (#900 message long gone, within lifetime).
        f.WriteWfbid(BuildWfbid(WfbidReader.Size64,
        [
            ('B', "14986_LU9DCE", 100, 20620),
            ('P', "101_GB7RDG", 101, 20620),
            ('T', "103_GB7RDG", 103, 20621),
            ('B', "6323_KC2NJV", 102, 20600),
            ('B', "9999_OLDBID", 900, 20590), // orphan BID — its message is gone
        ]));

        f.WriteLinmail(
            """
            main :
            {
              BBSName = "GB7RDG";
              SYSOPCall = "GB7RDG";
              H-Route = "#42.GBR.EURO";
            };
            BBSForwarding :
            {
              GB7RDG :
              {
                ConnectScript = "";
                Enabled = 0;
                BBSHA = "GB7RDG.#42.GBR.EURO";
              };
              GB7BSK :
              {
                ConnectScript = "C 3 GB7BSK";
                ATCalls = "GBR|WW";
                HRoutesP = "#48.GBR.EURO";
                Enabled = 1;
                UseB2Protocol = 0;
                FwdInterval = 300;
                ConTimeout = 120;
                BBSHA = "GB7BSK.#48.GBR.EURO";
              };
              GB7CIP :
              {
                ConnectScript = "INTERLOCK 3|C 3 !GB7WEM-7|C uhf gb7cip";
                ATCalls = "GBR|WW|EU";
                HRoutesP = "#32.GBR.EURO";
                Enabled = 1;
                UseB2Protocol = 1;
                FwdInterval = 720;
                ConTimeout = 120;
                BBSHA = "GB7CIP.#32.GBR.EURO";
              };
            };
            Housekeeping :
            {
              MaxMsgno = 60000;
              BidLifetime = 60;
              MaxAge = 365;
            };
            BBSUsers :
            {
              M0LTE = "Tom^^GB7RDG^IO91WM^^RG1 1AA^^29^0^0^0^0^0^1781734234^^";
              GB7RDG = "Sysop^^^^^^^1^8^0^2^0^0^0^^";
              GB7BSK = "BBS^^^^^^^0^16^0^1^0^0^0^^";
              GB7CIP = "BBS^^^^^^^0^16^0^5^0^0^0^^";
            };
            """);

        return f;
    }
}
