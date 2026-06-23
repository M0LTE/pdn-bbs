using System.Buffers.Binary;
using System.Text;

namespace Bbs.Import.Bpq.Tests;

/// <summary>
/// Test builders that synthesise byte-exact <c>DIRMES.SYS</c> and <c>WFBID.SYS</c> images, matching
/// the verified on-disk layouts (bpqmail.h, pack(1), little-endian, NUL-terminated char fields). These
/// let the parser tests assert against records we control bit-for-bit.
/// </summary>
internal static class BpqBinaryBuilders
{
    public const int NewRecordSize = 308;
    public const int LegacyRecordSize = 243;

    /// <summary>A field for one synthetic message header.</summary>
    internal sealed record MsgSpec
    {
        public char Type { get; init; } = 'B';
        public char Status { get; init; } = 'N';
        public int Number { get; init; }
        public int Length { get; init; }
        public string BbsFrom { get; init; } = "";
        public string Via { get; init; } = "";
        public string From { get; init; } = "";
        public string To { get; init; } = "";
        public string Bid { get; init; } = "";
        public string Title { get; init; } = "";
        public byte B2Flags { get; init; }
        public IReadOnlyList<int> FbbsBits { get; init; } = [];
        public IReadOnlyList<int> ForwBits { get; init; } = [];
        public long DateReceived { get; init; }
        public long DateCreated { get; init; }
        public long DateChanged { get; init; }
    }

    /// <summary>Builds a new-layout (308-byte) DIRMES.SYS image with the given latest number + messages.</summary>
    public static byte[] BuildDirmesNew(int latestNumber, IEnumerable<MsgSpec> messages)
    {
        var control = new byte[NewRecordSize];
        control[1] = 2; // status = format version 2 (new layout)
        BinaryPrimitives.WriteInt32LittleEndian(control.AsSpan(6, 4), latestNumber); // length = LatestMsg

        var stream = new List<byte>(control);
        foreach (MsgSpec m in messages)
        {
            stream.AddRange(BuildMsgNew(m));
        }

        return [.. stream];
    }

    /// <summary>Builds a legacy-layout (243-byte) DIRMES.SYS image (status byte 1 = legacy marker).</summary>
    public static byte[] BuildDirmesLegacy(int latestNumber, IEnumerable<MsgSpec> messages)
    {
        var control = new byte[LegacyRecordSize];
        control[1] = 1; // status = format version 1 (legacy marker)
        BinaryPrimitives.WriteInt32LittleEndian(control.AsSpan(6, 4), latestNumber);

        var stream = new List<byte>(control);
        foreach (MsgSpec m in messages)
        {
            stream.AddRange(BuildMsgLegacy(m));
        }

        return [.. stream];
    }

    private static byte[] BuildMsgNew(MsgSpec m)
    {
        var r = new byte[NewRecordSize];
        r[0] = (byte)m.Type;
        r[1] = (byte)m.Status;
        BinaryPrimitives.WriteInt32LittleEndian(r.AsSpan(2, 4), m.Number);
        BinaryPrimitives.WriteInt32LittleEndian(r.AsSpan(6, 4), m.Length);
        WriteCString(r, 14, 7, m.BbsFrom);
        WriteCString(r, 21, 41, m.Via);
        WriteCString(r, 62, 7, m.From);
        WriteCString(r, 69, 7, m.To);
        WriteCString(r, 76, 13, m.Bid);
        WriteCString(r, 89, 61, m.Title);
        r[154] = m.B2Flags;
        WriteBits(r, 163, 20, m.FbbsBits);
        WriteBits(r, 183, 20, m.ForwBits);
        BinaryPrimitives.WriteInt64LittleEndian(r.AsSpan(247, 8), m.DateReceived);
        BinaryPrimitives.WriteInt64LittleEndian(r.AsSpan(255, 8), m.DateCreated);
        BinaryPrimitives.WriteInt64LittleEndian(r.AsSpan(263, 8), m.DateChanged);
        return r;
    }

    private static byte[] BuildMsgLegacy(MsgSpec m)
    {
        var r = new byte[LegacyRecordSize];
        r[0] = (byte)m.Type;
        r[1] = (byte)m.Status;
        BinaryPrimitives.WriteInt32LittleEndian(r.AsSpan(2, 4), m.Number);
        BinaryPrimitives.WriteInt32LittleEndian(r.AsSpan(6, 4), m.Length);
        BinaryPrimitives.WriteInt32LittleEndian(r.AsSpan(10, 4), (int)m.DateReceived); // legacy int32
        WriteCString(r, 14, 7, m.BbsFrom);
        WriteCString(r, 21, 41, m.Via);
        WriteCString(r, 62, 7, m.From);
        WriteCString(r, 69, 7, m.To);
        WriteCString(r, 76, 13, m.Bid);
        WriteCString(r, 89, 61, m.Title);
        r[155] = m.B2Flags;
        BinaryPrimitives.WriteInt64LittleEndian(r.AsSpan(166, 8), m.DateCreated);
        BinaryPrimitives.WriteInt64LittleEndian(r.AsSpan(174, 8), m.DateChanged);
        WriteBits(r, 182, 10, m.FbbsBits);
        WriteBits(r, 192, 10, m.ForwBits);
        return r;
    }

    /// <summary>Builds a WFBID.SYS image at the given record size (18 = 32-bit, 22 = 64-bit).</summary>
    public static byte[] BuildWfbid(int recordSize, IEnumerable<(char Mode, string Bid, ushort MsgNo, ushort TsDays)> bids)
    {
        var list = bids.ToList();
        var control = new byte[recordSize];
        BinaryPrimitives.WriteUInt16LittleEndian(control.AsSpan(14, 2), (ushort)list.Count); // count

        var stream = new List<byte>(control);
        foreach ((char mode, string bid, ushort msgno, ushort ts) in list)
        {
            var r = new byte[recordSize];
            r[0] = (byte)mode;
            WriteCString(r, 1, 13, bid);
            BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(14, 2), msgno);
            BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(16, 2), ts);
            stream.AddRange(r);
        }

        return [.. stream];
    }

    private static void WriteCString(byte[] buffer, int offset, int fieldLen, string value)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(value);
        int n = Math.Min(bytes.Length, fieldLen - 1); // leave room for NUL
        Array.Copy(bytes, 0, buffer, offset, n);
        // remaining bytes are already zero (NUL)
    }

    private static void WriteBits(byte[] buffer, int offset, int maskBytes, IReadOnlyList<int> bits)
    {
        foreach (int n in bits)
        {
            if (n >= 1 && (n - 1) / 8 < maskBytes)
            {
                buffer[offset + (n - 1) / 8] |= (byte)(1 << ((n - 1) % 8));
            }
        }
    }
}
