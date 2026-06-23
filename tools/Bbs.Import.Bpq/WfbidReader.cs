using System.Buffers.Binary;
using System.Text;

namespace Bbs.Import.Bpq;

/// <summary>
/// Reads <c>WFBID.SYS</c> — the array of <c>BIDRec</c> records (bpqmail.h:684), BPQMail's BID
/// dedup database. Record [0] is a control record whose first union word holds the BID count
/// (verified against SaveBIDDatabase, BBSUtilities.c:1762 <c>BIDRecPtr[0]-&gt;u.msgno = NumberofBIDs</c>).
///
/// <para>
/// Record-size auto-detection (critical). <c>BIDRec</c> is:
/// <code>char mode; char BID[13]; union { struct { u16 msgno; u16 timestamp; }; CIRCUIT* conn; } u;</code>
/// The union is pointer-sized, so <c>sizeof(BIDRec)</c> is BUILD-dependent: 1 + 13 + 4 = 18 bytes on a
/// 32-bit LinBPQ, and 1 + 13 + 8 = 22 bytes on a 64-bit LinBPQ (the extra 4 bytes pad the union to
/// the 8-byte pointer). The real fixtures confirm both: gb7rdg-config/bpq/WFBID.SYS is 29934 = 18×1663
/// (32-bit), docker/oracle/state/WFBID.SYS is 22 (64-bit). This reader picks the size that divides the
/// file length exactly; if both could, it prefers the one whose control count matches the record count.
/// </para>
/// </summary>
internal static class WfbidReader
{
    /// <summary>BIDRec size on a 32-bit LinBPQ build.</summary>
    public const int Size32 = 18;

    /// <summary>BIDRec size on a 64-bit LinBPQ build.</summary>
    public const int Size64 = 22;

    private const int OffMode = 0;
    private const int OffBid = 1;    // char[13]
    private const int OffMsgNo = 14; // u16
    private const int OffTimestamp = 16; // u16

    /// <summary>The result of decoding a WFBID.SYS file.</summary>
    internal sealed record Result(
        int RecordSize,
        int ControlCount,
        IReadOnlyList<BpqBidRecord> Bids,
        IReadOnlyList<string> Warnings);

    /// <summary>Reads and decodes a WFBID.SYS file from disk.</summary>
    public static Result Read(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Decode(File.ReadAllBytes(path), path);
    }

    /// <summary>Decodes the raw bytes of a WFBID.SYS file. Exposed for unit testing.</summary>
    public static Result Decode(byte[] data, string sourceName = "WFBID.SYS")
    {
        ArgumentNullException.ThrowIfNull(data);
        var warnings = new List<string>();

        if (data.Length == 0)
        {
            warnings.Add($"{sourceName}: file is empty; treating as zero BIDs.");
            return new Result(Size64, 0, [], warnings);
        }

        int recordSize = DetectRecordSize(data, sourceName, warnings);

        if (data.Length % recordSize != 0)
        {
            warnings.Add(
                $"{sourceName}: length {data.Length} is not a multiple of the chosen {recordSize}-byte record size " +
                $"({data.Length % recordSize} trailing byte(s) ignored).");
        }

        int recordCount = data.Length / recordSize;
        if (recordCount == 0)
        {
            warnings.Add($"{sourceName}: smaller than one record; no control record present.");
            return new Result(recordSize, 0, [], warnings);
        }

        int controlCount = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(OffMsgNo, 2));

        var bids = new List<BpqBidRecord>(recordCount - 1);
        for (int i = 1; i < recordCount; i++)
        {
            ReadOnlySpan<byte> rec = data.AsSpan(i * recordSize, recordSize);
            string bid = ReadCString(rec.Slice(OffBid, 13));

            // Skip blank/zeroed slots (a defensive guard for compacted files).
            if (bid.Length == 0)
            {
                continue;
            }

            bids.Add(new BpqBidRecord
            {
                Mode = (char)rec[OffMode],
                Bid = bid,
                MsgNo = BinaryPrimitives.ReadUInt16LittleEndian(rec.Slice(OffMsgNo, 2)),
                TimestampDays = BinaryPrimitives.ReadUInt16LittleEndian(rec.Slice(OffTimestamp, 2)),
            });
        }

        if (controlCount != recordCount - 1)
        {
            warnings.Add(
                $"{sourceName}: control record reports {controlCount} BID(s) but the file holds {recordCount - 1} " +
                $"record(s). Importing all on-disk records (the dedup store must be complete).");
        }

        return new Result(recordSize, controlCount, bids, warnings);
    }

    private static int DetectRecordSize(byte[] data, string sourceName, List<string> warnings)
    {
        bool div18 = data.Length % Size32 == 0;
        bool div22 = data.Length % Size64 == 0;

        if (div18 && !div22)
        {
            return Size32;
        }

        if (div22 && !div18)
        {
            return Size64;
        }

        if (div18 && div22)
        {
            // Both divide (e.g. small files): use the control count to disambiguate.
            int control = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(OffMsgNo, 2));
            if (data.Length / Size64 - 1 == control)
            {
                return Size64;
            }

            if (data.Length / Size32 - 1 == control)
            {
                return Size32;
            }

            return Size64; // Default to the modern 64-bit layout.
        }

        warnings.Add(
            $"{sourceName}: length {data.Length} divides neither the 32-bit ({Size32}) nor the 64-bit ({Size64}) " +
            $"BIDRec size; assuming the 64-bit layout and ignoring trailing bytes.");
        return Size64;
    }

    private static string ReadCString(ReadOnlySpan<byte> field)
    {
        int nul = field.IndexOf((byte)0);
        int len = nul < 0 ? field.Length : nul;
        return len == 0 ? string.Empty : Encoding.Latin1.GetString(field[..len]);
    }
}
