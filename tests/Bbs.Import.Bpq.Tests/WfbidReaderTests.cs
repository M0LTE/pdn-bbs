using Bbs.Import.Bpq;
using static Bbs.Import.Bpq.Tests.BpqBinaryBuilders;

namespace Bbs.Import.Bpq.Tests;

/// <summary>
/// Parser tests for WFBID.SYS (<c>BIDRec</c>). The record size is build-dependent (18 bytes on a
/// 32-bit LinBPQ, 22 on a 64-bit one); the reader auto-detects it.
/// </summary>
public sealed class WfbidReaderTests
{
    [Fact]
    public void Decode_32BitLayout_18Bytes_ReadsBids()
    {
        byte[] image = BuildWfbid(WfbidReader.Size32,
        [
            ('B', "10447_LU9DCE", 116, 20565),
            ('B', "10460_LU9DCE", 117, 20565),
            ('P', "923_KC2NJV", 50, 20600),
        ]);

        WfbidReader.Result result = WfbidReader.Decode(image);

        Assert.Equal(18, result.RecordSize);
        Assert.Equal(3, result.ControlCount);
        Assert.Equal(3, result.Bids.Count);
        Assert.Equal("10447_LU9DCE", result.Bids[0].Bid);
        Assert.Equal('B', result.Bids[0].Mode);
        Assert.Equal((ushort)20565, result.Bids[0].TimestampDays);
    }

    [Fact]
    public void Decode_64BitLayout_22Bytes_ReadsBids()
    {
        byte[] image = BuildWfbid(WfbidReader.Size64,
        [
            ('P', "57_GB7BPQ-1", 57, 20600),
        ]);

        WfbidReader.Result result = WfbidReader.Decode(image);

        Assert.Equal(22, result.RecordSize);
        Assert.Equal(1, result.ControlCount);
        BpqBidRecord b = Assert.Single(result.Bids);
        Assert.Equal("57_GB7BPQ-1", b.Bid);
    }

    [Fact]
    public void Decode_ControlCountMismatch_Warns()
    {
        // Build 2 records but stamp the control count to 5 — the reader imports all on-disk records.
        byte[] image = BuildWfbid(WfbidReader.Size32, [('B', "A_X", 1, 100), ('B', "B_Y", 2, 100)]);
        // overwrite control count (offset 14) to 5
        image[14] = 5;

        WfbidReader.Result result = WfbidReader.Decode(image);
        Assert.Equal(2, result.Bids.Count);
        Assert.Contains(result.Warnings, w => w.Contains("control record reports", StringComparison.Ordinal));
    }

    [Fact]
    public void Decode_EmptyFile_ReturnsNoBids()
    {
        WfbidReader.Result result = WfbidReader.Decode([]);
        Assert.Empty(result.Bids);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public void Decode_RealStaleSnapshot_18ByteRecords_AllParsed()
    {
        if (!Fixtures.HasGb7rdgSnapshot)
        {
            return;
        }

        WfbidReader.Result result = WfbidReader.Read(Fixtures.Gb7rdgWfbid());
        Assert.Equal(18, result.RecordSize);    // gb7rdg was a 32-bit build
        Assert.Equal(1662, result.ControlCount);
        Assert.Equal(1662, result.Bids.Count);
    }
}
