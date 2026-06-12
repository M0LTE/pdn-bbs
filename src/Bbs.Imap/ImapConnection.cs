using System.Buffers;
using System.Text;

namespace Bbs.Imap;

/// <summary>
/// The line-and-literal transport for one IMAP connection over a duplex <see cref="Stream"/>
/// (a plaintext socket stream, or an <see cref="System.Net.Security.SslStream"/>). It reads CRLF
/// command lines, splicing in IMAP literals (<c>{n}</c> — read the continuation, then exactly n
/// octets), and writes raw response bytes. UTF-8 on the text path; literal octets are read raw.
/// </summary>
/// <remarks>
/// IMAP commands are CRLF-terminated, but a command argument may be a literal: the client sends
/// <c>...{n}CRLF</c>, the server replies <c>+ OK\r\n</c> (a command continuation request), the client
/// sends exactly n octets, and the command line continues. <see cref="ReadCommandAsync"/> assembles the
/// whole logical command line — text plus any spliced literal octets (decoded back as Latin-1 so each
/// byte survives into the parser) — into one string the engine parses.
/// </remarks>
public sealed class ImapConnection : IDisposable
{
    // A generous cap so a malformed client can't drive us out of memory on one command line.
    private const int MaxCommandBytes = 1 << 20; // 1 MiB
    private const int MaxLiteralBytes = 32 << 20; // 32 MiB — read-mostly server; commands carry tiny literals

    private readonly Stream _stream;
    private readonly byte[] _readBuffer = new byte[8192];
    private int _bufferStart;
    private int _bufferEnd;

    /// <summary>Wraps <paramref name="stream"/> as an IMAP transport.</summary>
    public ImapConnection(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
    }

    /// <summary>
    /// Reads one whole logical command line (CRLF-terminated, with any IMAP literals spliced in), or
    /// null at end of stream. A literal triggers a <c>+ OK</c> continuation before its octets are read.
    /// The returned string carries each literal octet as one Latin-1 char so the parser sees the bytes.
    /// </summary>
    public async Task<string?> ReadCommandAsync(CancellationToken cancellationToken)
    {
        var line = new StringBuilder();
        while (true)
        {
            string? chunk = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (chunk is null)
            {
                return line.Length > 0 ? line.ToString() : null;
            }

            // A trailing "{n}" (non-literal-plus) means a literal follows after a continuation.
            if (TryParseTrailingLiteral(chunk, out int literalLength, out bool literalPlus))
            {
                line.Append(chunk);
                line.Append("\r\n"); // preserve the CRLF the literal count referred to

                if (!literalPlus)
                {
                    await WriteAsync("+ OK\r\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
                }

                byte[] literal = await ReadOctetsAsync(literalLength, cancellationToken).ConfigureAwait(false);
                line.Append(Encoding.Latin1.GetString(literal));

                if (line.Length > MaxCommandBytes)
                {
                    throw new InvalidOperationException("IMAP command exceeded the maximum length.");
                }

                continue; // the command line continues after the literal
            }

            line.Append(chunk);
            return line.ToString();
        }
    }

    /// <summary>Writes raw response bytes (the engine has already CRLF-framed them).</summary>
    public async Task WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        await _stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes a US-ASCII/UTF-8 response string verbatim (it must already end with CRLF).</summary>
    public Task WriteAsync(string text, CancellationToken cancellationToken)
        => WriteAsync(Encoding.UTF8.GetBytes(text), cancellationToken);

    /// <inheritdoc/>
    public void Dispose() => _stream.Dispose();

    /// <summary>Reads one CRLF-terminated line (the CRLF stripped), or null at end of stream.</summary>
    private async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        var bytes = new ArrayBufferWriter<byte>(128);
        while (true)
        {
            if (_bufferStart >= _bufferEnd)
            {
                _bufferStart = 0;
                _bufferEnd = await _stream.ReadAsync(_readBuffer, cancellationToken).ConfigureAwait(false);
                if (_bufferEnd == 0)
                {
                    return bytes.WrittenCount == 0 ? null : Encoding.Latin1.GetString(bytes.WrittenSpan);
                }
            }

            while (_bufferStart < _bufferEnd)
            {
                byte b = _readBuffer[_bufferStart++];
                if (b == '\n')
                {
                    ReadOnlySpan<byte> span = bytes.WrittenSpan;
                    if (span.Length > 0 && span[^1] == '\r')
                    {
                        span = span[..^1];
                    }

                    return Encoding.Latin1.GetString(span);
                }

                bytes.Write([b]);
                if (bytes.WrittenCount > MaxCommandBytes)
                {
                    throw new InvalidOperationException("IMAP command line exceeded the maximum length.");
                }
            }
        }
    }

    /// <summary>Reads exactly <paramref name="count"/> octets (a literal payload), or throws at EOF.</summary>
    private async Task<byte[]> ReadOctetsAsync(int count, CancellationToken cancellationToken)
    {
        if (count < 0 || count > MaxLiteralBytes)
        {
            throw new InvalidOperationException("IMAP literal length out of range.");
        }

        var result = new byte[count];
        int filled = 0;
        while (filled < count)
        {
            if (_bufferStart < _bufferEnd)
            {
                int available = Math.Min(count - filled, _bufferEnd - _bufferStart);
                Array.Copy(_readBuffer, _bufferStart, result, filled, available);
                _bufferStart += available;
                filled += available;
                continue;
            }

            _bufferStart = 0;
            _bufferEnd = await _stream.ReadAsync(_readBuffer, cancellationToken).ConfigureAwait(false);
            if (_bufferEnd == 0)
            {
                throw new EndOfStreamException("Connection closed while reading an IMAP literal.");
            }
        }

        return result;
    }

    /// <summary>
    /// Detects a trailing IMAP literal marker <c>{n}</c> (or the LITERAL+ form <c>{n+}</c>) at the very
    /// end of a command line: returns the octet count and whether it is the non-blocking <c>+</c> form.
    /// </summary>
    private static bool TryParseTrailingLiteral(string line, out int length, out bool literalPlus)
    {
        length = 0;
        literalPlus = false;
        if (line.Length < 3 || line[^1] != '}')
        {
            return false;
        }

        int open = line.LastIndexOf('{');
        if (open < 0 || open == line.Length - 2)
        {
            return false;
        }

        string inner = line[(open + 1)..^1];
        if (inner.EndsWith('+'))
        {
            literalPlus = true;
            inner = inner[..^1];
        }

        return int.TryParse(inner, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out length) && length >= 0;
    }
}
