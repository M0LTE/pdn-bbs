using System.Text;
using Bbs.Core;

namespace Bbs.Core.Tests;

/// <summary>
/// Unit tests for <see cref="PacketText"/> — the header-text codec for FBB B1
/// titles and B2F header values (e.g. <c>Subject:</c>). Pins the three
/// load-bearing invariants: ASCII is byte-identical on the wire, a UTF-8 value
/// round-trips byte-for-byte, and a genuine Latin-1 high byte is decoded
/// faithfully then "upgraded" to its 2-byte UTF-8 form on egress.
/// </summary>
public sealed class PacketTextTests
{
    [Fact]
    public void EncodeHeader_AsciiText_IsByteIdenticalToAscii()
    {
        const string text = "Hello there 123";
        var utf8 = PacketText.EncodeHeader(text);

        Assert.Equal(Encoding.ASCII.GetBytes(text), utf8);
        Assert.Equal(Encoding.Latin1.GetBytes(text), utf8); // ASCII == Latin-1 == UTF-8
    }

    [Fact]
    public void DecodeHeader_AsciiBytes_DecodesToSameString()
    {
        const string text = "Plain ASCII subject";
        Assert.Equal(text, PacketText.DecodeHeader(Encoding.ASCII.GetBytes(text)));
    }

    [Fact]
    public void RoundTrip_AsciiSubject_IsByteExact()
    {
        const string subject = "Re: Weekly net minutes";
        var encoded = PacketText.EncodeHeader(subject);

        Assert.Equal(subject, PacketText.DecodeHeader(encoded));
        Assert.Equal(Encoding.ASCII.GetBytes(subject), encoded);
    }

    [Fact]
    public void DecodeHeader_ValidUtf8_DecodesAsUtf8()
    {
        // Smart quotes “ ” (U+201C/U+201D) + an accented char é (U+00E9).
        const string subject = "He said “hello” to José";
        var utf8 = Encoding.UTF8.GetBytes(subject);

        Assert.Equal(subject, PacketText.DecodeHeader(utf8));
    }

    [Fact]
    public void RoundTrip_Utf8Subject_IsByteFaithful()
    {
        const string subject = "He said “hello” to José";
        var wire = Encoding.UTF8.GetBytes(subject);

        // Decode-then-encode reproduces the SAME bytes.
        var decoded = PacketText.DecodeHeader(wire);
        Assert.Equal(subject, decoded);
        Assert.Equal(wire, PacketText.EncodeHeader(decoded));
    }

    [Fact]
    public void DecodeHeader_Latin1HighByte_FallsBackToLatin1()
    {
        // 0xA3 alone is invalid UTF-8 → must decode as Latin-1 → '£' (U+00A3).
        byte[] latin1 = [(byte)'C', (byte)'o', (byte)'s', (byte)'t', (byte)' ', 0xA3, (byte)'5'];

        Assert.Equal("Cost £5", PacketText.DecodeHeader(latin1));
    }

    [Fact]
    public void RoundTrip_Latin1HighByte_UpgradesToTwoByteUtf8()
    {
        // The deliberate, rare upgrade: 0xA3 ('£') → U+00A3 → 0xC2 0xA3 on egress.
        byte[] latin1 = [0xA3];
        var decoded = PacketText.DecodeHeader(latin1);

        Assert.Equal("£", decoded);
        Assert.Equal([0xC2, 0xA3], PacketText.EncodeHeader(decoded));
    }
}
