using System.Diagnostics;
using System.Net.Sockets;

namespace Bbs.Interop.Tests;

/// <summary>
/// The xunit collection both interop test classes join: shares one <see cref="OracleFixture"/>
/// and — critically — serialises the classes, so only one FBB session uses the simulated RF
/// channel (and the PDNBBS-1 identity) at a time.
/// </summary>
[CollectionDefinition(Name)]
public class OracleCollection : ICollectionFixture<OracleFixture>
{
    /// <summary>The collection name.</summary>
    public const string Name = "Oracle";
}

/// <summary>
/// Asserts the LinBPQ+BPQMail oracle stack (docker/compose.oracle.yml) is up and SERVING,
/// following the packet.net interop convention: the fixture does NOT bring the stack up —
/// CI does (with <c>up -d --wait</c> gating on the healthchecks) and local runs do it by
/// hand. Beyond the port probes it drives a full telnet login → <c>BBS</c> → prompt →
/// sign-off round trip: container-healthy runs a beat ahead of BPQMail actually serving
/// its application streams, and ONE dial against a not-ready oracle wedges a BBS stream
/// ("All BBS Ports are in use") and poisons every test behind it.
/// </summary>
/// <remarks>
/// The endpoints default to the documented stack (docker/README port map) and can be
/// re-pointed at a PRIVATE oracle instance via <c>PDNBBS_ORACLE_KISS_PORT</c>,
/// <c>PDNBBS_ORACLE_TELNET_PORT</c> and <c>PDNBBS_ORACLE_CONTAINER</c>. The oracle is a
/// stateful singleton (one forwarding-partner identity, one RF channel), so two actors
/// sharing one instance poison each other's sessions (docker/README delta 9) — a second
/// developer/agent on the same box should run their own stack and point these at it. CI
/// and ordinary local runs need none of them.
/// </remarks>
public sealed class OracleFixture
{
    /// <summary>netsim node a — our KISS-TCP attach point (docker README port map).</summary>
    public const string KissHost = "127.0.0.1";

    /// <summary>netsim node a KISS-TCP port.</summary>
    public static readonly int KissPort = EnvInt("PDNBBS_ORACLE_KISS_PORT", 8200);

    /// <summary>LinBPQ telnet (node prompt → BBS).</summary>
    public static readonly int TelnetPort = EnvInt("PDNBBS_ORACLE_TELNET_PORT", 8210);

    /// <summary>The LinBPQ container name (compose.oracle.yml) — docker exec target.</summary>
    public static readonly string OracleContainer =
        Environment.GetEnvironmentVariable("PDNBBS_ORACLE_CONTAINER") is { Length: > 0 } c ? c : "pdnbbs-gb7bpq";

    private static int EnvInt(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out int value) ? value : fallback;

    /// <summary>The oracle node callsign.</summary>
    public const string OracleNodeCall = "GB7BPQ";

    /// <summary>The oracle BBS application callsign (answers direct AX.25 connects).</summary>
    public const string OracleBbsCall = "GB7BPQ-1";

    /// <summary>Creates the fixture, probing the stack.</summary>
    public OracleFixture()
    {
        ProbeAsync().GetAwaiter().GetResult();
    }

    private static async Task ProbeAsync()
    {
        // The compose healthchecks gate `up -d --wait`, but container-healthy runs a
        // beat ahead of BPQMail serving its application streams: a cold oracle greets
        // telnet and still answers `BBS` with "All BBS Ports are in use". A dial
        // against that wedges a BBS stream and poisons every test behind it — so gate
        // on the real thing: a full login → BBS → prompt → sign-off round trip,
        // retried (fresh connection each attempt; disposing frees the stream).
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
        while (true)
        {
            try
            {
                using var kiss = new TcpClient();
                await kiss.ConnectAsync(KissHost, KissPort).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);

                using var telnet = await TelnetBbsClient.ConnectAsync(KissHost, TelnetPort, CancellationToken.None)
                    .ConfigureAwait(false);
                await telnet.LoginAndEnterBbsAsync(CancellationToken.None).ConfigureAwait(false);
                await telnet.SignOffAsync(CancellationToken.None).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is SocketException or TimeoutException or IOException or InvalidOperationException)
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new InvalidOperationException(
                        "The LinBPQ oracle's BBS never answered a telnet login → BBS round trip on " +
                        $"{KissHost}:{KissPort} (netsim KISS) / :{TelnetPort} (telnet). " +
                        "Bring the stack up first (docker compose -f docker/compose.oracle.yml up -d --wait); " +
                        "if it IS up, its BBS streams are likely wedged by a prior aborted run — " +
                        "recycle it: down -v, wipe docker/oracle/state (root-owned: use a throwaway " +
                        "container like smoke.sh does), up -d --wait.",
                        ex);
                }

                await Task.Delay(2000).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Runs a shell command inside the oracle container (the smoke.sh on-disk assertion
    /// path: the state bind mount is root-owned, so inspect it via docker exec rather
    /// than host reads). Returns stdout; a non-zero exit returns null.
    /// </summary>
    public static async Task<string?> OracleShellAsync(string shellCommand, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "docker",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(OracleContainer);
        psi.ArgumentList.Add("sh");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(shellCommand);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("could not start docker exec");
        string stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return process.ExitCode == 0 ? stdout : null;
    }

    /// <summary>
    /// Polls the oracle's on-disk message store (<c>/data/Mail/m_*.mes</c> — the primary
    /// interop assertion target, compat spec §7.4) until a file containing
    /// <paramref name="needle"/> appears, and returns that file's full text. Store writes
    /// lag the FBB session by up to a few seconds — every wait is a poll-with-deadline.
    /// </summary>
    public static async Task<string> WaitForMailFileAsync(
        string needle, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            // grep -l writes the matching path; fixed-string match so the nonce
            // can never be misread as a pattern.
            string? path = await OracleShellAsync(
                $"grep -lF '{needle}' /data/Mail/m_*.mes 2>/dev/null | head -1",
                cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(path))
            {
                string? content = await OracleShellAsync(
                    $"cat '{path.Trim()}'", cancellationToken).ConfigureAwait(false);
                if (content is not null)
                {
                    return content;
                }
            }

            if (DateTime.UtcNow > deadline)
            {
                string? listing = await OracleShellAsync(
                    "ls -la /data/Mail/ 2>/dev/null; tail -40 /data/logs/log_*_BBS.txt 2>/dev/null",
                    cancellationToken).ConfigureAwait(false);
                throw new TimeoutException(
                    $"'{needle}' never appeared in the oracle store (/data/Mail/m_*.mes). " +
                    $"Oracle state:\n{listing}");
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }
    }
}
