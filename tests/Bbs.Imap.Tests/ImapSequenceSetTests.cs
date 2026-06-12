using Bbs.Imap;

namespace Bbs.Imap.Tests;

public sealed class ImapSequenceSetTests
{
    [Fact]
    public void TryParse_SingleNumber_ReturnsThatValue()
    {
        Assert.True(ImapSequenceSet.TryParse("3", star: 9, out IReadOnlyList<long> values));
        Assert.Equal([3L], values);
    }

    [Fact]
    public void TryParse_Range_IsInclusiveAndAscending()
    {
        Assert.True(ImapSequenceSet.TryParse("2:5", star: 9, out IReadOnlyList<long> values));
        Assert.Equal([2L, 3L, 4L, 5L], values);
    }

    [Fact]
    public void TryParse_ReversedRange_IsOrderIndependent()
    {
        Assert.True(ImapSequenceSet.TryParse("5:2", star: 9, out IReadOnlyList<long> values));
        Assert.Equal([2L, 3L, 4L, 5L], values);
    }

    [Fact]
    public void TryParse_Star_SubstitutesTheLargestValue()
    {
        Assert.True(ImapSequenceSet.TryParse("*", star: 7, out IReadOnlyList<long> values));
        Assert.Equal([7L], values);
    }

    [Fact]
    public void TryParse_StarRange_ExpandsAgainstStar()
    {
        Assert.True(ImapSequenceSet.TryParse("4:*", star: 9, out IReadOnlyList<long> values));
        Assert.Equal([4L, 5L, 6L, 7L, 8L, 9L], values);

        Assert.True(ImapSequenceSet.TryParse("*:4", star: 9, out IReadOnlyList<long> reversed));
        Assert.Equal([4L, 5L, 6L, 7L, 8L, 9L], reversed);
    }

    [Fact]
    public void TryParse_CommaList_MergesAndDeduplicates()
    {
        Assert.True(ImapSequenceSet.TryParse("1,3,5:7,3", star: 9, out IReadOnlyList<long> values));
        Assert.Equal([1L, 3L, 5L, 6L, 7L], values);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]      // nz-number: 0 is not a valid sequence/UID
    [InlineData("1,")]      // trailing empty atom
    [InlineData(",1")]      // leading empty atom
    [InlineData("1::3")]    // malformed range
    [InlineData("abc")]     // non-numeric
    [InlineData("1:2:3")]   // too many colons
    public void TryParse_Malformed_ReturnsFalse(string input)
    {
        Assert.False(ImapSequenceSet.TryParse(input, star: 9, out IReadOnlyList<long> values));
        Assert.Empty(values);
    }

    [Fact]
    public void TryParse_ValuesPastTheEnd_AreReturnedAsIs()
    {
        // The parser does not range-check against any mailbox — the caller intersects with what exists.
        Assert.True(ImapSequenceSet.TryParse("8:12", star: 10, out IReadOnlyList<long> values));
        Assert.Equal([8L, 9L, 10L, 11L, 12L], values);
    }
}
