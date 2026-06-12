using System.Globalization;
using System.Text;

namespace Bbs.Imap;

/// <summary>
/// Builds the data items of one <c>* n FETCH (...)</c> response (RFC 3501 §7.4.2 / §6.4.5). Given the
/// requested item list and a message handle, it formats each item — <c>UID</c>, <c>FLAGS</c>,
/// <c>INTERNALDATE</c>, <c>RFC822.SIZE</c>, <c>ENVELOPE</c>, <c>BODY</c>/<c>BODYSTRUCTURE</c>, and the
/// section fetches <c>BODY[]</c>/<c>BODY[HEADER]</c>/<c>BODY[TEXT]</c> and their <c>RFC822*</c> aliases —
/// and reports whether any non-PEEK body section was read (so the session marks <c>\Seen</c>).
/// </summary>
internal sealed class ImapFetch
{
    private readonly ImapMessageHandle _handle;
    private readonly List<string> _textItems = [];
    private readonly List<(string Name, ReadOnlyMemory<byte> Bytes)> _literalItems = [];

    private ImapFetch(ImapMessageHandle handle) => _handle = handle;

    /// <summary>True when a non-PEEK body/text section was fetched (a <c>\Seen</c>-setting access).</summary>
    public bool TouchedBody { get; private set; }

    /// <summary>
    /// Formats the requested <paramref name="items"/> for <paramref name="handle"/> into the parsed
    /// item set. Each item is one of the recognised FETCH data items; an unknown item is ignored
    /// (the session validates the list up front, so this only ever sees recognised items). Returns the
    /// built fetch, ready for <see cref="WriteResponseAsync"/>.
    /// </summary>
    public static ImapFetch Build(ImapMessageHandle handle, IReadOnlyList<string> items, bool alwaysIncludeUid)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(items);

        var fetch = new ImapFetch(handle);
        bool uidIncluded = false;

        foreach (string item in items)
        {
            string upper = item.ToUpperInvariant();
            if (upper == "UID")
            {
                uidIncluded = true;
            }

            fetch.Append(item);
        }

        // UID FETCH must always return UID even if the client didn't ask (RFC 3501 §6.4.8).
        if (alwaysIncludeUid && !uidIncluded)
        {
            fetch.Append("UID");
        }

        return fetch;
    }

    /// <summary>
    /// Writes the whole <c>* seq FETCH (...)</c> line, splicing each body section as a <c>{n}</c>
    /// literal so binary bytes are framed correctly (RFC 3501 §7.4.2: a section response is a literal).
    /// </summary>
    public async Task WriteResponseAsync(ImapConnection connection, int sequence, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var sb = new StringBuilder();
        sb.Append("* ");
        sb.Append(sequence.ToString(CultureInfo.InvariantCulture));
        sb.Append(" FETCH (");

        // Interleave the two ordered streams in their original append order is unnecessary for a client;
        // emit the text items first, then the literal items, each space-separated.
        bool first = true;
        foreach (string text in _textItems)
        {
            if (!first)
            {
                sb.Append(' ');
            }

            first = false;
            sb.Append(text);
        }

        // Body-section items carry a literal: write the accumulated text prefix, then the {n}+bytes.
        await connection.WriteAsync(Encoding.UTF8.GetBytes(sb.ToString()), cancellationToken).ConfigureAwait(false);

        foreach ((string name, ReadOnlyMemory<byte> bytes) in _literalItems)
        {
            var prefix = new StringBuilder();
            if (!first)
            {
                prefix.Append(' ');
            }

            first = false;
            prefix.Append(name);
            prefix.Append(" {");
            prefix.Append(bytes.Length.ToString(CultureInfo.InvariantCulture));
            prefix.Append("}\r\n");
            await connection.WriteAsync(Encoding.UTF8.GetBytes(prefix.ToString()), cancellationToken).ConfigureAwait(false);
            await connection.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }

        await connection.WriteAsync(")\r\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    private void Append(string item)
    {
        string upper = item.ToUpperInvariant();
        ImapRenderedMessage rendered = _handle.Rendered;

        switch (upper)
        {
            case "UID":
                _textItems.Add($"UID {_handle.Uid.ToString(CultureInfo.InvariantCulture)}");
                return;
            case "FLAGS":
                _textItems.Add($"FLAGS ({FlagList()})");
                return;
            case "INTERNALDATE":
                _textItems.Add($"INTERNALDATE \"{InternalDate()}\"");
                return;
            case "RFC822.SIZE":
                _textItems.Add($"RFC822.SIZE {rendered.Size.ToString(CultureInfo.InvariantCulture)}");
                return;
            case "ENVELOPE":
                _textItems.Add($"ENVELOPE {rendered.BuildEnvelope()}");
                return;
            case "BODY":
                _textItems.Add($"BODY {rendered.BuildBodyStructure(extended: false)}");
                return;
            case "BODYSTRUCTURE":
                _textItems.Add($"BODYSTRUCTURE {rendered.BuildBodyStructure(extended: true)}");
                return;
            case "RFC822":
                _literalItems.Add(("RFC822", rendered.Full));
                TouchedBody = true;
                return;
            case "RFC822.HEADER":
                _literalItems.Add(("RFC822.HEADER", rendered.Header));
                return;
            case "RFC822.TEXT":
                _literalItems.Add(("RFC822.TEXT", rendered.Text));
                TouchedBody = true;
                return;
        }

        // Section fetches: BODY[...] / BODY.PEEK[...].
        if (upper.StartsWith("BODY", StringComparison.Ordinal))
        {
            AppendBodySection(item, upper, rendered);
            return;
        }

        // An unrecognised item slipped through validation: emit nothing (defensive).
    }

    private void AppendBodySection(string item, string upper, ImapRenderedMessage rendered)
    {
        bool peek = upper.Contains(".PEEK[", StringComparison.Ordinal);

        int open = item.IndexOf('[', StringComparison.Ordinal);
        int close = item.IndexOf(']', StringComparison.Ordinal);
        if (open < 0 || close < 0 || close < open)
        {
            return;
        }

        string section = item[(open + 1)..close].ToUpperInvariant();

        // The response name echoes the request but never the ".PEEK" (RFC 3501 §7.4.2).
        string responseName = $"BODY[{item[(open + 1)..close]}]";

        ReadOnlyMemory<byte> bytes = section switch
        {
            "" => rendered.Full,
            "HEADER" => rendered.Header,
            "TEXT" => rendered.Text,
            _ => rendered.Full, // an unsupported section spec falls back to the whole message
        };

        _literalItems.Add((responseName, bytes));

        // A non-PEEK fetch of any body/text section sets \Seen; HEADER-only and PEEK do not change it
        // here (HEADER is metadata; PEEK is by definition non-marking). We treat full + TEXT as marking.
        if (!peek && section is "" or "TEXT")
        {
            TouchedBody = true;
        }
    }

    /// <summary>The space-separated IMAP flag list for the handle (only <c>\Seen</c> is tracked).</summary>
    private string FlagList() => _handle.Seen ? "\\Seen" : string.Empty;

    /// <summary>The INTERNALDATE form <c>dd-MMM-yyyy HH:mm:ss zzzz</c> (RFC 3501 §7.4.2 date-time).</summary>
    private string InternalDate()
    {
        DateTimeOffset created = _handle.Message.CreatedAt;
        string day = created.Day.ToString("D2", CultureInfo.InvariantCulture);
        string month = created.ToString("MMM", CultureInfo.InvariantCulture);
        string rest = created.ToString("yyyy HH:mm:ss", CultureInfo.InvariantCulture);
        string offset = created.ToString("zzz", CultureInfo.InvariantCulture).Replace(":", string.Empty, StringComparison.Ordinal);
        return $"{day}-{month}-{rest} {offset}";
    }
}
