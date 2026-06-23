using System.Buffers.Binary;
using System.Text;

namespace Bbs.Import.Bpq;

/// <summary>
/// Reads <c>DIRMES.SYS</c> — the array of fixed-size <c>struct MsgInfo</c> message headers
/// (bpqmail.h:615). Record [0] is a control record (its <c>length</c> field at offset 6 holds
/// the latest/highest message number, i.e. BPQ's <c>LatestMsg</c>; verified against
/// BBSUtilities.c:1393 <c>LatestMsg = MsgHddrPtr[0]-&gt;length</c>). The file is <c>#pragma pack(1)</c>,
/// little-endian, NUL-terminated fixed char arrays.
///
/// <para>
/// Layout auto-detection. The current ("new") <c>struct MsgInfo</c> is 308 bytes. The legacy
/// <c>struct OldMsgInfo</c> (bpqmail.h:586) is 243 bytes on a 64-bit build (8-byte <c>time_t</c>).
/// BPQ itself detects the legacy format by the control record's <c>status</c> byte == 1
/// (BBSUtilities.c:1397). This reader uses BOTH signals: it prefers whichever record size divides
/// the file length exactly, and cross-checks the control status. This keeps it robust against the
/// stale/inconsistent gb7rdg snapshot (it must not crash and must report anomalies).
/// </para>
/// </summary>
internal static class DirmesReader
{
    /// <summary>Size of the current <c>struct MsgInfo</c> (verified: 308 bytes, pack(1)).</summary>
    public const int NewRecordSize = 308;

    /// <summary>Size of the legacy <c>struct OldMsgInfo</c> on a 64-bit build (verified: 243 bytes).</summary>
    public const int LegacyRecordSize = 243;

    // New-layout (struct MsgInfo) field offsets — verified byte-for-byte against bpqmail.h:615.
    private const int NewOffType = 0;
    private const int NewOffStatus = 1;
    private const int NewOffNumber = 2;
    private const int NewOffLength = 6;
    private const int NewOffBbsFrom = 14;   // char[7]
    private const int NewOffVia = 21;       // char[41]
    private const int NewOffFrom = 62;      // char[7]
    private const int NewOffTo = 69;        // char[7]
    private const int NewOffBid = 76;       // char[13]
    private const int NewOffTitle = 89;     // char[61]
    private const int NewOffB2Flags = 154;  // UCHAR
    private const int NewOffFbbs = 163;     // UCHAR[20]  (NBMASK = NBBBS/8 = 160/8 = 20)
    private const int NewOffForw = 183;     // UCHAR[20]
    private const int NewOffDateReceived = 247; // int64 LE
    private const int NewOffDateCreated = 255;  // int64 LE
    private const int NewOffDateChanged = 263;  // int64 LE
    private const int NewMaskBytes = 20;

    // Legacy-layout (struct OldMsgInfo) field offsets — verified against bpqmail.h:586.
    private const int OldOffType = 0;
    private const int OldOffStatus = 1;
    private const int OldOffNumber = 2;
    private const int OldOffLength = 6;
    private const int OldOffDateReceived = 10;  // int32 LE
    private const int OldOffBbsFrom = 14;
    private const int OldOffVia = 21;
    private const int OldOffFrom = 62;
    private const int OldOffTo = 69;
    private const int OldOffBid = 76;
    private const int OldOffTitle = 89;
    private const int OldOffB2Flags = 155;      // UCHAR (after char bin; int nntpnum)
    private const int OldOffDateCreated = 166;  // time_t (int64 LE)
    private const int OldOffDateChanged = 174;  // time_t (int64 LE)
    private const int OldOffFbbs = 182;         // char[10]
    private const int OldOffForw = 192;         // char[10]
    private const int OldMaskBytes = 10;

    /// <summary>The result of decoding a DIRMES.SYS file.</summary>
    internal sealed record Result(
        int LatestMessageNumber,
        bool LegacyLayout,
        IReadOnlyList<BpqMessageHeader> Messages,
        IReadOnlyList<string> Warnings);

    /// <summary>Reads and decodes a DIRMES.SYS file from disk.</summary>
    public static Result Read(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Decode(File.ReadAllBytes(path), path);
    }

    /// <summary>Decodes the raw bytes of a DIRMES.SYS file. Exposed for unit testing.</summary>
    public static Result Decode(byte[] data, string sourceName = "DIRMES.SYS")
    {
        ArgumentNullException.ThrowIfNull(data);
        var warnings = new List<string>();

        if (data.Length == 0)
        {
            warnings.Add($"{sourceName}: file is empty; treating as zero messages.");
            return new Result(0, false, [], warnings);
        }

        bool legacy = DetectLegacy(data, sourceName, warnings, out int recordSize);

        if (data.Length % recordSize != 0)
        {
            int trailing = data.Length % recordSize;
            warnings.Add(
                $"{sourceName}: length {data.Length} is not a multiple of the {(legacy ? "legacy" : "current")} " +
                $"record size {recordSize} ({trailing} trailing byte(s) ignored). The file may be truncated or corrupt.");
        }

        int recordCount = data.Length / recordSize;
        if (recordCount == 0)
        {
            warnings.Add($"{sourceName}: smaller than one record; no control record present.");
            return new Result(0, legacy, [], warnings);
        }

        // Control record [0]: latest message number is the int32 at the length offset.
        int lengthOff = legacy ? OldOffLength : NewOffLength;
        int latest = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(lengthOff, 4));

        var messages = new List<BpqMessageHeader>(recordCount - 1);
        for (int i = 1; i < recordCount; i++)
        {
            ReadOnlySpan<byte> rec = data.AsSpan(i * recordSize, recordSize);

            byte typeByte = rec[legacy ? OldOffType : NewOffType];
            int number = BinaryPrimitives.ReadInt32LittleEndian(rec.Slice(legacy ? OldOffNumber : NewOffNumber, 4));

            // BPQ skips slots with a zero type byte or zero number (BBSUtilities.c:1430/1453).
            if (typeByte == 0 || number == 0)
            {
                continue;
            }

            messages.Add(legacy ? DecodeLegacy(rec) : DecodeNew(rec));
        }

        return new Result(latest, legacy, messages, warnings);
    }

    private static bool DetectLegacy(byte[] data, string sourceName, List<string> warnings, out int recordSize)
    {
        bool divNew = data.Length % NewRecordSize == 0;
        bool divLegacy = data.Length % LegacyRecordSize == 0;
        byte controlStatus = data.Length >= 2 ? data[NewOffStatus] : (byte)0;
        bool statusSaysLegacy = controlStatus == 1;

        // Primary signal: exact divisibility. If only one size divides cleanly, trust it.
        if (divNew && !divLegacy)
        {
            if (statusSaysLegacy)
            {
                warnings.Add(
                    $"{sourceName}: control status byte = 1 (BPQ legacy marker) but the length only divides by the " +
                    $"current {NewRecordSize}-byte record size; reading as current layout.");
            }

            recordSize = NewRecordSize;
            return false;
        }

        if (divLegacy && !divNew)
        {
            recordSize = LegacyRecordSize;
            return true;
        }

        // Ambiguous or neither: fall back to the control-status byte (BPQ's own rule).
        if (statusSaysLegacy)
        {
            recordSize = LegacyRecordSize;
            return true;
        }

        if (!divNew && !divLegacy)
        {
            warnings.Add(
                $"{sourceName}: length {data.Length} divides neither the current ({NewRecordSize}) nor the legacy " +
                $"({LegacyRecordSize}) record size; assuming current layout and ignoring the trailing bytes.");
        }

        recordSize = NewRecordSize;
        return false;
    }

    private static BpqMessageHeader DecodeNew(ReadOnlySpan<byte> rec) => new()
    {
        Type = (char)rec[NewOffType],
        Status = (char)rec[NewOffStatus],
        Number = BinaryPrimitives.ReadInt32LittleEndian(rec.Slice(NewOffNumber, 4)),
        Length = BinaryPrimitives.ReadInt32LittleEndian(rec.Slice(NewOffLength, 4)),
        BbsFrom = ReadCString(rec.Slice(NewOffBbsFrom, 7)),
        Via = ReadCString(rec.Slice(NewOffVia, 41)),
        From = ReadCString(rec.Slice(NewOffFrom, 7)),
        To = ReadCString(rec.Slice(NewOffTo, 7)),
        Bid = ReadCString(rec.Slice(NewOffBid, 13)),
        Title = ReadCString(rec.Slice(NewOffTitle, 61)),
        B2Flags = rec[NewOffB2Flags],
        Fbbs = rec.Slice(NewOffFbbs, NewMaskBytes).ToArray(),
        Forw = rec.Slice(NewOffForw, NewMaskBytes).ToArray(),
        DateReceived = BinaryPrimitives.ReadInt64LittleEndian(rec.Slice(NewOffDateReceived, 8)),
        DateCreated = BinaryPrimitives.ReadInt64LittleEndian(rec.Slice(NewOffDateCreated, 8)),
        DateChanged = BinaryPrimitives.ReadInt64LittleEndian(rec.Slice(NewOffDateChanged, 8)),
    };

    private static BpqMessageHeader DecodeLegacy(ReadOnlySpan<byte> rec) => new()
    {
        Type = (char)rec[OldOffType],
        Status = (char)rec[OldOffStatus],
        Number = BinaryPrimitives.ReadInt32LittleEndian(rec.Slice(OldOffNumber, 4)),
        Length = BinaryPrimitives.ReadInt32LittleEndian(rec.Slice(OldOffLength, 4)),
        BbsFrom = ReadCString(rec.Slice(OldOffBbsFrom, 7)),
        Via = ReadCString(rec.Slice(OldOffVia, 41)),
        From = ReadCString(rec.Slice(OldOffFrom, 7)),
        To = ReadCString(rec.Slice(OldOffTo, 7)),
        Bid = ReadCString(rec.Slice(OldOffBid, 13)),
        Title = ReadCString(rec.Slice(OldOffTitle, 61)),
        B2Flags = rec[OldOffB2Flags],
        Fbbs = rec.Slice(OldOffFbbs, OldMaskBytes).ToArray(),
        Forw = rec.Slice(OldOffForw, OldMaskBytes).ToArray(),

        // Legacy stored datereceived as int32; datecreated/changed as time_t (int64 on this build).
        DateReceived = BinaryPrimitives.ReadInt32LittleEndian(rec.Slice(OldOffDateReceived, 4)),
        DateCreated = BinaryPrimitives.ReadInt64LittleEndian(rec.Slice(OldOffDateCreated, 8)),
        DateChanged = BinaryPrimitives.ReadInt64LittleEndian(rec.Slice(OldOffDateChanged, 8)),
    };

    /// <summary>
    /// Decodes a NUL-terminated fixed-width C string field as Latin-1 (ISO-8859-1). BPQ's char
    /// fields are single-byte; Latin-1 is the lossless 1:1 byte→char mapping for callsign/title text.
    /// </summary>
    private static string ReadCString(ReadOnlySpan<byte> field)
    {
        int nul = field.IndexOf((byte)0);
        int len = nul < 0 ? field.Length : nul;
        return len == 0 ? string.Empty : Encoding.Latin1.GetString(field[..len]);
    }
}
