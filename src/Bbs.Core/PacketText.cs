using System.Text;

namespace Bbs.Core;

/// <summary>
/// Encoding helpers for human-readable header text carried over the packet
/// wire — the FBB B1 SOH title (spec §3.6) and B2F header values such as
/// <c>Subject:</c> (spec §3.9). The body of a message is handled elsewhere
/// (stored raw, decoded UTF-8-or-Latin-1 at display); these helpers cover the
/// SUBJECT/title fields, which are stored as decoded strings and so must be
/// decoded correctly at ingest and round-trip faithfully at egress.
/// </summary>
/// <remarks>
/// <para><b>Ingest (<see cref="DecodeHeader"/>):</b> decode as UTF-8 when the
/// bytes are valid UTF-8 (strict decoder), otherwise fall back to Latin-1.
/// Modern gateways/Winlink emit UTF-8 subjects; legacy FBB BBSes emit Latin-1.
/// A strict UTF-8 attempt distinguishes the two losslessly: any byte run that
/// is not well-formed UTF-8 (e.g. a lone 0xA3 '£') falls through to Latin-1,
/// where every single byte maps to the matching U+0000..U+00FF code point.</para>
///
/// <para><b>Egress (<see cref="EncodeHeader"/>):</b> always encode as UTF-8.
/// This is correct and interop-safe:</para>
/// <list type="bullet">
/// <item>UTF-8 of an ASCII string == ASCII bytes == Latin-1 bytes, so ASCII
/// subjects are byte-identical on the wire — no interop change.</item>
/// <item>A UTF-8 subject decoded then UTF-8-encoded reproduces the SAME bytes
/// (byte-faithful round-trip), so a received UTF-8 subject is forwarded
/// unchanged.</item>
/// <item>Only a genuine Latin-1 high-byte subject (e.g. 0xA3 '£', invalid
/// UTF-8 → decoded as Latin-1 → U+00A3) re-encodes to 2 UTF-8 bytes
/// (0xC2 0xA3) — a deliberate, rare "upgrade". This fixes display without
/// degrading the forward; Latin-1 egress would instead turn a received UTF-8
/// subject into '?' downstream, which is why we do NOT encode as Latin-1.</item>
/// </list>
/// </remarks>
public static class PacketText
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    /// <summary>
    /// Decodes packet header text: UTF-8 when <paramref name="bytes"/> is valid
    /// UTF-8, otherwise Latin-1 (a byte-for-byte fallback that never fails). See
    /// the type remarks for why this losslessly distinguishes the two encodings.
    /// </summary>
    public static string DecodeHeader(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    /// <summary>
    /// Encodes packet header text as UTF-8. ASCII strings are byte-identical to
    /// their ASCII/Latin-1 form (no interop change); a string previously decoded
    /// from UTF-8 by <see cref="DecodeHeader"/> re-encodes to the same bytes
    /// (byte-faithful round-trip). See the type remarks for the full rationale.
    /// </summary>
    public static byte[] EncodeHeader(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Encoding.UTF8.GetBytes(text);
    }
}
