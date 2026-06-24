using Bbs.Import.Bpq;

namespace Bbs.Import.Bpq.Tests;

/// <summary>
/// The import-time BPQ connect-script normalisation: NC -> C, strip the leading "!" target flag,
/// everything else verbatim. pdn-bbs's runtime parser stays strict "C"; the translation lives here.
/// </summary>
public sealed class BpqConnectScriptTests
{
    [Theory]
    // NC verb -> C (with + without a port; the "!" target flag stripped).
    [InlineData("NC 3 EI5IYB-1", "C 3 EI5IYB-1")]
    [InlineData("NC 3 !GB7BPQ", "C 3 GB7BPQ")]
    [InlineData("NC 2 !GB7BSK-1", "C 2 GB7BSK-1")]
    [InlineData("nc GB7BPQ", "C GB7BPQ")]
    // plain C: only the "!" is stripped.
    [InlineData("C 3 !GB7WEM-7", "C 3 GB7WEM-7")]
    [InlineData("C 9 !M9YYY-1", "C 9 M9YYY-1")]
    // already-clean / non-connect lines pass through untouched.
    [InlineData("C NDHBBS", "C NDHBBS")]
    [InlineData("C uhf gb7cip", "C uhf gb7cip")]
    [InlineData("bbs", "bbs")]
    [InlineData("PAUSE 5", "PAUSE 5")]
    [InlineData("INTERLOCK 3", "INTERLOCK 3")]
    [InlineData("GB7RDG>=BBS", "GB7RDG>=BBS")]
    [InlineData("", "")]
    public void Translate_NormalisesBpqDialect(string input, string expected)
        => Assert.Equal(expected, BpqConnectScript.Translate(input));
}
