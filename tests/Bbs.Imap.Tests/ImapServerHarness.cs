using System.Net.Sockets;
using System.Text;
using Bbs.Core;
using Bbs.Imap;
using MailKit.Net.Imap;

namespace Bbs.Imap.Tests;

/// <summary>
/// Spins a real <see cref="ImapServer"/> on <c>127.0.0.1:0</c> (an ephemeral port, plaintext) over a
/// caller-supplied store, and hands out connected MailKit <see cref="ImapClient"/>s — MailKit being the
/// strict correctness oracle (it throws on any malformed response). In-process over loopback, like the
/// host's WebmailTests, so it runs in normal CI (not Interop).
/// </summary>
internal sealed class ImapServerHarness : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts;
    private readonly Task _serverTask;
    private readonly ImapServer _server;

    private ImapServerHarness(ImapServer server, Task serverTask, CancellationTokenSource cts)
    {
        _server = server;
        _serverTask = serverTask;
        _cts = cts;
    }

    /// <summary>The TCP port the server bound.</summary>
    public int Port => _server.BoundPort;

    /// <summary>Starts a plaintext server over <paramref name="store"/> and waits until it has bound a port.</summary>
    public static async Task<ImapServerHarness> StartAsync(BbsStore store, TimeProvider time)
    {
        var server = new ImapServer(
            new ImapServerOptions { Bind = "127.0.0.1", Port = 0, TlsEnabled = false }, store, time);
        var cts = new CancellationTokenSource();
        Task serverTask = server.RunAsync(cts.Token);

        // Wait for the listener to bind (BoundPort becomes non-zero once Start() ran).
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (server.BoundPort == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10).ConfigureAwait(false);
        }

        if (server.BoundPort == 0)
        {
            await cts.CancelAsync().ConfigureAwait(false);
            cts.Dispose();
            throw new InvalidOperationException("IMAP server did not bind a port in time.");
        }

        return new ImapServerHarness(server, serverTask, cts);
    }

    /// <summary>Connects a MailKit client to the server (plaintext, no STARTTLS).</summary>
    public async Task<ImapClient> ConnectAsync()
    {
        var client = new ImapClient();
        await client.ConnectAsync("127.0.0.1", Port, MailKit.Security.SecureSocketOptions.None).ConfigureAwait(false);
        return client;
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _serverTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }

        _cts.Dispose();
    }
}

/// <summary>
/// A minimal raw IMAP client for tests that need to drive a command MailKit's high-level API does not
/// expose (chiefly a non-PEEK <c>BODY[]</c> fetch). It sends a tagged command and reads response lines
/// until the matching tagged completion, returning the whole accumulated text.
/// </summary>
internal sealed class RawImapClient : IAsyncDisposable
{
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly byte[] _buffer = new byte[16384];
    private readonly StringBuilder _pending = new();

    private RawImapClient(TcpClient tcp, NetworkStream stream)
    {
        _tcp = tcp;
        _stream = stream;
    }

    public static async Task<RawImapClient> ConnectAsync(int port)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", port).ConfigureAwait(false);
        var client = new RawImapClient(tcp, tcp.GetStream());
        await client.ReadUntilAsync(line => line.StartsWith("* OK", StringComparison.Ordinal)).ConfigureAwait(false);
        return client;
    }

    /// <summary>Sends <c>tag command CRLF</c> and returns all response text up to the tagged completion.</summary>
    public async Task<string> CommandAsync(string tag, string command)
    {
        byte[] bytes = Encoding.ASCII.GetBytes($"{tag} {command}\r\n");
        await _stream.WriteAsync(bytes).ConfigureAwait(false);
        await _stream.FlushAsync().ConfigureAwait(false);
        return await ReadUntilAsync(line =>
            line.StartsWith($"{tag} OK", StringComparison.Ordinal)
            || line.StartsWith($"{tag} NO", StringComparison.Ordinal)
            || line.StartsWith($"{tag} BAD", StringComparison.Ordinal)).ConfigureAwait(false);
    }

    private async Task<string> ReadUntilAsync(Func<string, bool> isTerminal)
    {
        var accumulated = new StringBuilder();
        while (true)
        {
            string? line = await ReadLineAsync().ConfigureAwait(false);
            if (line is null)
            {
                return accumulated.ToString();
            }

            accumulated.Append(line).Append('\n');
            if (isTerminal(line))
            {
                return accumulated.ToString();
            }
        }
    }

    private async Task<string?> ReadLineAsync()
    {
        while (true)
        {
            string buffered = _pending.ToString();
            int newline = buffered.IndexOf('\n', StringComparison.Ordinal);
            if (newline >= 0)
            {
                string line = buffered[..newline].TrimEnd('\r');
                _pending.Clear();
                _pending.Append(buffered[(newline + 1)..]);
                return line;
            }

            int read = await _stream.ReadAsync(_buffer).ConfigureAwait(false);
            if (read == 0)
            {
                return _pending.Length > 0 ? _pending.ToString() : null;
            }

            _pending.Append(Encoding.Latin1.GetString(_buffer, 0, read));
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stream.Dispose();
        _tcp.Dispose();
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }
}
