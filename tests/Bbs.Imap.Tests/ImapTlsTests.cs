using System.Net.Sockets;
using Bbs.Core;
using Bbs.Imap;
using MailKit.Net.Imap;
using MailKit.Security;

namespace Bbs.Imap.Tests;

/// <summary>
/// Exercises the implicit-TLS path end to end: a server with a generated self-signed cert, a MailKit
/// client connecting over SSL (accepting the untrusted self-signed cert via a callback), and an
/// authenticated session. Proves the TLS wrap + handshake + persisted-cert generation actually work,
/// not just that the seam exists.
/// </summary>
public sealed class ImapTlsTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("bbs-imap-tls-test-");

    public void Dispose() => _dir.Delete(recursive: true);

    [Fact]
    public async Task ImplicitTls_GeneratesCert_AndAuthenticatesOverSsl()
    {
        using var test = new TestStore();
        test.Store.SetMailPassword("M0LTE", "tls passphrase");
        test.Store.AddMessage(Drafts.Personal(to: "M0LTE", subject: "secret"));

        string certPath = Path.Combine(_dir.FullName, "imap-cert.pfx");
        var server = new ImapServer(
            new ImapServerOptions
            {
                Bind = "127.0.0.1",
                Port = 0,
                TlsEnabled = true,
                GenerateSelfSigned = true,
                SelfSignedCertPath = certPath,
            },
            test.Store,
            test.Time);

        using var cts = new CancellationTokenSource();
        Task serverTask = server.RunAsync(cts.Token);
        try
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (server.BoundPort == 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10);
            }

            Assert.NotEqual(0, server.BoundPort);
            Assert.True(File.Exists(certPath)); // the self-signed cert was generated and persisted

            using var client = new ImapClient();

            // The generated cert is self-signed/untrusted; accept it for THIS loopback test (the channel
            // is still TLS-encrypted — we are proving the handshake, not certificate trust).
#pragma warning disable CA5359 // accept-any cert is intentional for a self-signed loopback test
            client.ServerCertificateValidationCallback = (_, _, _, _) => true;
#pragma warning restore CA5359
            await client.ConnectAsync("127.0.0.1", server.BoundPort, SecureSocketOptions.SslOnConnect);
            Assert.True(client.IsSecure);

            await client.AuthenticateAsync("M0LTE", "tls passphrase");
            await client.Inbox.OpenAsync(MailKit.FolderAccess.ReadOnly);
            Assert.Equal(1, client.Inbox.Count);

            await client.DisconnectAsync(quit: true);
        }
        finally
        {
            await cts.CancelAsync();
            try
            {
                await serverTask;
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }
    }

    [Fact]
    public async Task PlaintextClientOnTlsPort_IsClosedFast_NotLeftHanging()
    {
        // A plaintext/STARTTLS client mis-pointed at the implicit-TLS port (e.g. an iOS Mail
        // account-verify probe) completes TCP and then waits for a server greeting that never comes,
        // while the server waits for a ClientHello — an indefinite deadlock. The handshake timeout must
        // turn that into a fast close so neither side hangs.
        using var test = new TestStore();
        string certPath = Path.Combine(_dir.FullName, "imap-cert.pfx");
        var server = new ImapServer(
            new ImapServerOptions
            {
                Bind = "127.0.0.1",
                Port = 0,
                TlsEnabled = true,
                GenerateSelfSigned = true,
                SelfSignedCertPath = certPath,
                TlsHandshakeTimeout = TimeSpan.FromMilliseconds(500),
            },
            test.Store,
            test.Time);

        using var cts = new CancellationTokenSource();
        Task serverTask = server.RunAsync(cts.Token);
        try
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (server.BoundPort == 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10);
            }

            Assert.NotEqual(0, server.BoundPort);

            using var tcp = new TcpClient();
            await tcp.ConnectAsync("127.0.0.1", server.BoundPort);
            using NetworkStream ns = tcp.GetStream();

            // Send no ClientHello. The server must close within the handshake timeout: a read returns 0
            // (graceful close) or faults (reset) — both mean "closed". A hang would time out the wait.
            var buf = new byte[16];
            int read;
            try
            {
                read = await ns.ReadAsync(buf).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (IOException)
            {
                read = 0; // connection reset is also a close, not a hang
            }

            Assert.Equal(0, read);
        }
        finally
        {
            await cts.CancelAsync();
            try
            {
                await serverTask;
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }
    }
}
