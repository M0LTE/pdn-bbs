using System.Text;
using Bbs.Host.Sessions;

namespace Bbs.Host.Tests;

/// <summary>
/// The incremental line splitter that feeds the console session. The headline case is the
/// telnet-NVT <b>CR-NUL</b> bare carriage return (RFC 854): a strict telnet client ends every line
/// with CR-NUL, and before the NUL-drop fix the trailing NUL prefixed the NEXT line, so the second
/// and every subsequent command arrived as <c>"\0&lt;cmd&gt;"</c> — an unknown command that printed
/// as the command itself (the NUL is invisible). That is the "M9YYY-1 ready, what next? help →
/// Sorry, I don't know &quot;help&quot;" bug, reproduced here at the unit level.
/// </summary>
public sealed class LineAssemblerTests
{
    private static IReadOnlyList<string> Feed(LineAssembler a, string s) =>
        a.Feed(Encoding.Latin1.GetBytes(s));

    [Fact]
    public void CrNul_terminator_does_not_leak_the_nul_into_the_next_line()
    {
        // The exact production scenario: two CR-NUL-terminated commands in one feed.
        var a = new LineAssembler();
        var lines = a.Feed(Encoding.Latin1.GetBytes("help\r\0help\r\0"));
        Assert.Equal(["help", "help"], lines); // NOT ["help", "\0help"]
    }

    [Fact]
    public void CrNul_split_across_feeds_still_drops_the_nul()
    {
        var a = new LineAssembler();
        Assert.Equal(["one"], Feed(a, "one\r"));   // CR completes the line
        Assert.Equal(["two"], Feed(a, "\0two\r")); // leading NUL (the CR-NUL tail) is dropped, not kept
    }

    [Fact]
    public void Lone_nul_is_dropped_anywhere()
    {
        var a = new LineAssembler();
        Assert.Equal(["abc"], Feed(a, "a\0b\0c\r")); // embedded NVT NUL no-ops vanish
    }

    [Fact]
    public void Crlf_is_coalesced_to_one_line()
    {
        var a = new LineAssembler();
        Assert.Equal(["a", "b"], Feed(a, "a\r\nb\r\n"));
    }

    [Fact]
    public void Crlf_split_across_feeds_yields_one_line()
    {
        var a = new LineAssembler();
        Assert.Equal(["a"], Feed(a, "a\r"));   // CR ends the line
        Assert.Equal(["b"], Feed(a, "\nb\r")); // the LF half of the CRLF is swallowed, not an empty line
    }

    [Fact]
    public void Bare_cr_and_bare_lf_each_terminate()
    {
        Assert.Equal(["a", "b"], Feed(new LineAssembler(), "a\rb\r"));
        Assert.Equal(["c", "d"], Feed(new LineAssembler(), "c\nd\n"));
    }
}
