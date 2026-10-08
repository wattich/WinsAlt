using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace WinsAlt.Core.Protocol;

/// <summary>
/// The fixed 12-byte NBNS header (same layout as DNS): NAME_TRN_ID, flags, QD/AN/NS/AR counts.
/// A plain value type - parsing and writing never allocate.
/// </summary>
public readonly struct NbnsHeader
{
    public const int Size = 12;

    public readonly ushort TransactionId;
    public readonly ushort Flags;
    public readonly ushort QdCount;
    public readonly ushort AnCount;
    public readonly ushort NsCount;
    public readonly ushort ArCount;

    public NbnsHeader(ushort transactionId, ushort flags, ushort qdCount, ushort anCount, ushort nsCount, ushort arCount)
    {
        TransactionId = transactionId;
        Flags = flags;
        QdCount = qdCount;
        AnCount = anCount;
        NsCount = nsCount;
        ArCount = arCount;
    }

    public bool IsResponse => (Flags & NbnsFlags.Response) != 0;
    public NbnsOpcode Opcode => (NbnsOpcode)((Flags >> 11) & 0x0F);
    public NbnsRcode Rcode => (NbnsRcode)(Flags & 0x0F);
    public bool IsBroadcast => (Flags & NbnsFlags.Broadcast) != 0;
    public bool RecursionDesired => (Flags & NbnsFlags.RecursionDesired) != 0;

    public static ushort MakeFlags(bool response, NbnsOpcode opcode, ushort nmFlags, NbnsRcode rcode) =>
        (ushort)((response ? NbnsFlags.Response : 0) | (((int)opcode & 0x0F) << 11) | nmFlags | ((int)rcode & 0x0F));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryRead(ReadOnlySpan<byte> packet, out NbnsHeader header)
    {
        if (packet.Length < Size)
        {
            header = default;
            return false;
        }

        header = new NbnsHeader(
            BinaryPrimitives.ReadUInt16BigEndian(packet),
            BinaryPrimitives.ReadUInt16BigEndian(packet[2..]),
            BinaryPrimitives.ReadUInt16BigEndian(packet[4..]),
            BinaryPrimitives.ReadUInt16BigEndian(packet[6..]),
            BinaryPrimitives.ReadUInt16BigEndian(packet[8..]),
            BinaryPrimitives.ReadUInt16BigEndian(packet[10..]));
        return true;
    }

    public void Write(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt16BigEndian(destination, TransactionId);
        BinaryPrimitives.WriteUInt16BigEndian(destination[2..], Flags);
        BinaryPrimitives.WriteUInt16BigEndian(destination[4..], QdCount);
        BinaryPrimitives.WriteUInt16BigEndian(destination[6..], AnCount);
        BinaryPrimitives.WriteUInt16BigEndian(destination[8..], NsCount);
        BinaryPrimitives.WriteUInt16BigEndian(destination[10..], ArCount);
    }
}
